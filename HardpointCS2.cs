using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API; 
using Microsoft.Extensions.Logging;
using HardpointCS2.Services;
using HardpointCS2.Models;
using HardpointCS2.Enums;
using System.Collections.Generic;
using System.Linq;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace HardpointCS2
{
    public class HardpointCS2 : BasePlugin
    {
        public override string ModuleName => "HardpointCS2";
        public override string ModuleVersion => "0.0.1";
        public override string ModuleAuthor => "evanhh";
        public override string ModuleDescription => "Hardpoint game mode for CS2";

        private ZoneVisualization? _zoneVisualization;
        private ZoneManager? _zoneManager;
        private readonly Dictionary<CCSPlayerController, Zone> _activeZones = new();
        private System.Timers.Timer? _zoneCheckTimer;
        private Zone? activeZone;

        private GamePhase _gamePhase = GamePhase.Warmup;
        private readonly HashSet<CCSPlayerController> _readyPlayers = new();
        private bool _requireTeamReady = true; // Configurable: true = one per team, false = all players
        private DateTime _lastReadyMessage = DateTime.MinValue;
        private readonly double _readyMessageInterval = 10.0; // 10 seconds

        private float _ctZoneTime = 0f;
        private float _tZoneTime = 0f;
        private int _ctScore = 0;
        private int _tScore = 0;
        private System.Timers.Timer? _hardpointTimer;
        private Zone? _previousZone = null;
        private readonly float CAPTURE_TIME = 10f; // 10 seconds to capture
        private readonly float TIMER_INTERVAL = 100f; // 100ms updates
        private bool _waitingForNewZone = false;
        private DateTime _zoneResetTime;
        private string _lastCaptureTeam = "";
        private readonly double _newZoneTimer = 5.0; // 5 seconds
        

        public override void Load(bool hotReload)
        {
            Logger.LogInformation("HardpointCS2 plugin loaded");
            _zoneVisualization = new ZoneVisualization();
            _zoneManager = new ZoneManager(ModuleDirectory);

            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            _zoneCheckTimer = new System.Timers.Timer(50);
            _zoneCheckTimer.Elapsed += CheckPlayerZones;
            _zoneCheckTimer.Start();

            // Initialize hardpoint timer
            _hardpointTimer = new System.Timers.Timer(TIMER_INTERVAL);
            _hardpointTimer.Elapsed += UpdateHardpointTimer;
            _hardpointTimer.Start();
        }

        private void UpdateHardpointTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    // Always update HUD regardless of phase
                    UpdateHardpointHUD();

                    // Only process zone capture logic during active phase
                    if (_gamePhase != GamePhase.Active || activeZone == null)
                        return;

                    var zoneState = activeZone.GetZoneState();

                    switch (zoneState)
                    {
                        case ZoneState.CTControlled:
                            _ctZoneTime += TIMER_INTERVAL / 1000f;
                            break;
                        
                        case ZoneState.TControlled:
                            _tZoneTime += TIMER_INTERVAL / 1000f;
                            break;
                        
                        case ZoneState.Contested:
                        case ZoneState.Neutral:
                            break;
                    }

                    // Check for capture completion
                    if (_ctZoneTime >= CAPTURE_TIME)
                    {
                        _ctScore++;
                        _lastCaptureTeam = "Counter-Terrorists";
                        UpdateTeamScore(CsTeam.CounterTerrorist, _ctScore);
                        Server.PrintToChatAll($"{ChatColors.LightBlue}★ Counter-Terrorists{ChatColors.Default} captured {ChatColors.Yellow}{activeZone.Name}{ChatColors.Default}! Score: {ChatColors.LightBlue}CT {_ctScore}{ChatColors.Default} - {ChatColors.Red}T {_tScore}{ChatColors.Default}");
                        ResetZoneAndSelectNew();
                    }
                    else if (_tZoneTime >= CAPTURE_TIME)
                    {
                        _tScore++;
                        _lastCaptureTeam = "Terrorists";
                        UpdateTeamScore(CsTeam.Terrorist, _tScore);
                        Server.PrintToChatAll($"{ChatColors.Red}★ Terrorists{ChatColors.Default} captured {ChatColors.Yellow}{activeZone.Name}{ChatColors.Default}! Score: {ChatColors.LightBlue}CT {_ctScore}{ChatColors.Default} - {ChatColors.Red}T {_tScore}{ChatColors.Default}");
                        ResetZoneAndSelectNew();
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[HardpointCS2] Error in UpdateHardpointTimer: {ex.Message}");
                }
            });
        }

        private void UpdateTeamScore(CsTeam team, int newScore)
        {
            try
            {
                // Find the team entity and update the score
                var teamEntities = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
                
                foreach (var teamEntity in teamEntities)
                {
                    if (teamEntity?.TeamNum == (int)team)
                    {
                        teamEntity.Score = newScore;
                        Server.PrintToConsole($"[HardpointCS2] Updated {team} score to {newScore}");
                        
                        // Force a scoreboard update
                        Utilities.SetStateChanged(teamEntity, "CCSTeam", "m_iScore");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[HardpointCS2] Error updating team score: {ex.Message}");
            }
        }

        private void UpdateHardpointHUD()
        {
            if (_gamePhase == GamePhase.Warmup)
            {
                UpdateWarmupHUD();
                return;
            }

            if (activeZone == null && _waitingForNewZone)
            {
                var remainingTime = Math.Max(0, _newZoneTimer - (DateTime.Now - _zoneResetTime).TotalSeconds);
                string waitMessage = $"🏆 {_lastCaptureTeam} scored! New zone in {remainingTime:F0}s...";
                
                foreach (var player in Utilities.GetPlayers())
                {
                    if (player?.IsValid == true && 
                        player.Connected == PlayerConnectedState.PlayerConnected && 
                        !player.IsBot)
                    {
                        player.PrintToCenter(waitMessage);
                    }
                }
                return;
            }
            if (activeZone == null) return;

            var zoneState = activeZone.GetZoneState();
            var ctProgress = (_ctZoneTime / CAPTURE_TIME * 100f);
            var tProgress = (_tZoneTime / CAPTURE_TIME * 100f);

            string statusMessage = zoneState switch
            {
                ZoneState.CTControlled => $"🔵 CTs controlling {activeZone.Name} - Progress: {ctProgress:F0}%",
                ZoneState.TControlled => $"🔴 Ts controlling {activeZone.Name} - Progress: {tProgress:F0}%",
                ZoneState.Contested => $"⚪ {activeZone.Name} CONTESTED - Timers paused | CT: {ctProgress:F0}% | T: {tProgress:F0}%",
                ZoneState.Neutral => GetNeutralMessage(activeZone.Name, ctProgress, tProgress),
                _ => $"⚪ {activeZone.Name} - Status unknown"
            };

            // Print to center of screen for ALL players (not just those in zone)
            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true && 
                    player.Connected == PlayerConnectedState.PlayerConnected && 
                    !player.IsBot)
                {
                    player.PrintToCenter(statusMessage);
                }
            }
        }

        private void UpdateWarmupHUD()
        {
            var activePlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true && 
                        p.Connected == PlayerConnectedState.PlayerConnected && 
                        !p.IsBot)
                .ToList();

            string warmupMessage;

            if (activePlayers.Count < 2)
            {
                if (activePlayers.Count == 1)
                    warmupMessage = "⏳ At least 2 players required to play Hardpoint...";
                else
                    warmupMessage = "⏳ Waiting for players to join...";
            }
            else if (_requireTeamReady)
            {
                var ctPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
                var tPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();
                
                if (ctPlayers.Count == 0 || tPlayers.Count == 0)
                {
                    warmupMessage = "⏳ Need players on both teams to start...";
                }
                else
                {
                    var readyCT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.CounterTerrorist);
                    var readyT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.Terrorist);
                    
                    if (!readyCT && !readyT)
                        warmupMessage = "Type !ready to start the game";
                    else if (!readyCT)
                        warmupMessage = "⏳ Waiting for Counter-Terrorists to ready up";
                    else if (!readyT)
                        warmupMessage = "⏳ Waiting for Terrorists to ready up";
                    else
                        warmupMessage = "🎮 Starting soon...";
                }
            }
            else
            {
                var readyCount = _readyPlayers.Count;
                var totalCount = activePlayers.Count;
                if (readyCount == totalCount)
                    warmupMessage = "🎮 Starting soon...";
                else
                    warmupMessage = $"⏳ Waiting for {totalCount - readyCount} players to ready up ({readyCount}/{totalCount})";
            }

            // Send message to ALL players (including spectators)
            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true && 
                    player.Connected == PlayerConnectedState.PlayerConnected && 
                    !player.IsBot)
                {
                    player.PrintToCenter(warmupMessage);
                }
            }

            // Print not ready players every 10 seconds
            if ((DateTime.Now - _lastReadyMessage).TotalSeconds >= _readyMessageInterval)
            {
                PrintNotReadyPlayers(activePlayers);
                _lastReadyMessage = DateTime.Now;
            }
        }

        private void PrintNotReadyPlayers(List<CCSPlayerController> activePlayers)
        {
            if (activePlayers.Count < 2)
                return; // Don't spam about ready status if not enough players

            var notReadyPlayers = activePlayers.Where(p => !_readyPlayers.Contains(p)).ToList();

            if (notReadyPlayers.Count > 0)
            {
                var notReadyNames = string.Join(", ", notReadyPlayers.Select(p => p.PlayerName));
                Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.Red}Not ready: {notReadyNames}{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Yellow}Type !ready to start the game{ChatColors.Default}");
            }
        }

        private string GetNeutralMessage(string zoneName, float ctProgress, float tProgress)
        {
            if (ctProgress == 0 && tProgress == 0)
                return $"⚪ {zoneName} neutral - No progress";
            else
                return $"⚪ {zoneName} neutral - CT: {ctProgress:F0}% | T: {tProgress:F0}%";
        }

       private void ResetZoneAndSelectNew()
        {
            if (activeZone == null) return;

            _previousZone = activeZone;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;

            // Clear current zone visualization
            _zoneVisualization?.ClearZoneVisualization();
            
            // Set waiting state
            _waitingForNewZone = true;
            _zoneResetTime = DateTime.Now;
            
            activeZone = null; // Clear active zone immediately
            
            // Announce zone cleared and wait period
            Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} -{ChatColors.Orange} Zone cleared!{ChatColors.Default} Score: {ChatColors.LightBlue}CT {_ctScore}{ChatColors.Default} - {ChatColors.Red}T {_tScore}{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - ⏱{ChatColors.Yellow} New zone in 5 seconds...{ChatColors.Default}");
    
            // Wait 5 seconds before selecting new zone
            AddTimer(5.0f, () =>
            {
                _waitingForNewZone = false;
                SelectNewZone();
            });
        }

        private void SelectNewZone()
        {
            if (_zoneManager?.Zones == null || _zoneManager.Zones.Count <= 1)
            {
                Server.PrintToChatAll($"{ChatColors.Red}⚠ Not enough zones available for rotation!{ChatColors.Default}");
                return;
            }

            var availableZones = _zoneManager.Zones.Where(z => z != _previousZone).ToList();
            
            if (availableZones.Count == 0)
            {
                // Fallback: use any zone if filtering failed
                availableZones = _zoneManager.Zones.ToList();
            }

            if (availableZones.Count == 0)
            {
                Server.PrintToChatAll($"{ChatColors.Red}⚠ No zones available!{ChatColors.Default}");
                return;
            }

            var random = new Random();
            var newZone = availableZones[random.Next(availableZones.Count)];
            
            activeZone = newZone;
            _zoneVisualization?.DrawZone(activeZone);
            
            Server.PrintToChatAll($"{ChatColors.Green}🎯 New Hardpoint: {ChatColors.Yellow}{activeZone.Name}{ChatColors.Default}");
            Server.PrintToConsole($"[HardpointCS2] New zone selected: {activeZone.Name}");
        }

        private void OnMapStart(string mapName)
        {
            Server.NextFrame(() =>
            {
                Server.PrintToConsole($"[HardpointCS2] Map started: {mapName}");
                Logger.LogInformation($"Loading zones for map: {mapName}");
                
                _zoneManager?.LoadZonesForMap(mapName);

                Logger.LogInformation($"Loaded {_zoneManager?.Zones.Count ?? 0} zones for map {mapName}");
            });
        }

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            // Stop zone checking during round transition
            // Stop timers during round transition
            _zoneCheckTimer?.Stop();
            _hardpointTimer?.Stop();
            
            if (_gamePhase == GamePhase.Active)
            {
                // Reset scores and timers for new round
                _ctScore = 0;
                _tScore = 0;
                _ctZoneTime = 0f;
                _tZoneTime = 0f;
                _previousZone = null;
                
                // Reset team scores in the game
                UpdateTeamScore(CsTeam.CounterTerrorist, 0);
                UpdateTeamScore(CsTeam.Terrorist, 0);
            }
            else
            {
                // In warmup, clear any existing zones
                _zoneVisualization?.ClearZoneVisualization();
                activeZone = null;
            }
    
            AddTimer(3.0f, () =>
            {
                try
                {
                    // Clear players from all zones
                    foreach (var zone in _zoneManager?.Zones ?? new List<Zone>())
                    {
                        zone.PlayersInZone.Clear();
                    }
                    
                    if (_gamePhase == GamePhase.Active)
                    {
                        Server.NextFrame(() =>
                        {
                            DrawRandomZone();
                            
                            // Restart timers only if game is active
                            _zoneCheckTimer?.Start();
                            _hardpointTimer?.Start();
                            
                            Server.PrintToChatAll($"{ChatColors.Green}🎮 Hardpoint round started!{ChatColors.Default}");
                        });
                    }
                    else
                    {
                        // In warmup, only start the HUD timer for warmup messages
                        _hardpointTimer?.Start();
                        Server.PrintToChatAll($"{ChatColors.Yellow}⏳ Warmup phase - Type !ready to start{ChatColors.Default}");
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[HardpointCS2] Error in OnRoundStart: {ex.Message}");
                    if (_gamePhase == GamePhase.Active)
                    {
                        _zoneCheckTimer?.Start();
                    }
                    _hardpointTimer?.Start();
                }
            });
            
            return HookResult.Continue;
        }

        private void DrawAllZones()
        {
            // Only draw the active zone if one is selected
            if (activeZone != null)
            {
                try
                {
                    // Clear any existing visualizations first
                    _zoneVisualization?.ClearZoneVisualization();
                    
                    Logger.LogInformation($"Drawing active zone: {activeZone.Name} with {activeZone.Points.Count} points");
                    _zoneVisualization?.DrawZone(activeZone);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[HardpointCS2] Error drawing active zone {activeZone.Name}: {ex.Message}");
                }
            }
            else
            {
                // If no active zone, clear all visualizations
                _zoneVisualization?.ClearZoneVisualization();
                Logger.LogInformation("No active zone selected - clearing all visualizations");
            }
        }

        private void DrawRandomZone()
        {
            // Draw a random zone for testing purposes
            if (_zoneManager?.Zones != null && _zoneManager.Zones.Count > 0)
            {
                // Clear all existing visualizations first
                _zoneVisualization?.ClearZoneVisualization();
                
                var random = new Random();
                var randomZone = _zoneManager.Zones[random.Next(_zoneManager.Zones.Count)];
                
                // Only draw the selected zone
                _zoneVisualization?.DrawZone(randomZone);
                Server.PrintToConsole($"[HardpointCS2] Drew random zone: {randomZone.Name}");
                Server.PrintToChatAll($"{ChatColors.Green}🎯 Active Hardpoint: {ChatColors.Yellow}{randomZone.Name}{ChatColors.Default}");
                activeZone = randomZone;
                _ctZoneTime = 0f;
                _tZoneTime = 0f;
            }
            else
            {
                Server.PrintToConsole("[HardpointCS2] No zones available to draw");
                Server.PrintToChatAll($"{ChatColors.Red}⚠ No zones available to draw!{ChatColors.Default}");
                activeZone = null;
            }
        }

        public override void Unload(bool hotReload)
        {
            _zoneCheckTimer?.Stop();
            _zoneCheckTimer?.Dispose();
            _hardpointTimer?.Stop();
            _hardpointTimer?.Dispose();
            _zoneVisualization?.ClearZoneVisualization();
            Logger.LogInformation("HardpointCS2 plugin unloaded");
        }

        private void CheckPlayerZones(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    // Only check the active zone
                    if (activeZone != null)
                    {
                        var previousState = activeZone.GetZoneState();
                        var previousPlayers = new List<CCSPlayerController>(activeZone.PlayersInZone);
                        var previousPlayerCount = previousPlayers.Count; // Add this line
                        
                        // Clear and rebuild the players list
                        activeZone.PlayersInZone.Clear();

                        foreach (var player in Utilities.GetPlayers())
                        {
                            if (player?.IsValid == true && 
                                player.Connected == PlayerConnectedState.PlayerConnected &&
                                player.PlayerPawn?.Value != null && 
                                player.PawnIsAlive)
                            {
                                var playerPos = new CSVector(
                                    player.PlayerPawn.Value.AbsOrigin!.X,
                                    player.PlayerPawn.Value.AbsOrigin!.Y,
                                    player.PlayerPawn.Value.AbsOrigin!.Z
                                );

                                if (activeZone.IsPlayerInZone(playerPos))
                                {
                                    activeZone.PlayersInZone.Add(player);
                                    
                                    // Check if this player just entered
                                    if (!previousPlayers.Contains(player))
                                    {
                                        Server.PrintToConsole($"[HardpointCS2] Player {player.PlayerName} entered zone {activeZone.Name}");
                                    }
                                }
                                else
                                {
                                    // Check if this player just left
                                    if (previousPlayers.Contains(player))
                                    {
                                        Server.PrintToConsole($"[HardpointCS2] Player {player.PlayerName} left zone {activeZone.Name}");
                                    }
                                }
                            }
                        }

                        var currentState = activeZone.GetZoneState();
                        var currentPlayerCount = activeZone.PlayersInZone.Count;

                        // Update zone color if state changed OR if player count changed
                        if (currentState != previousState || 
                            currentPlayerCount != previousPlayerCount)
                        {
                            try
                            {
                                _zoneVisualization?.UpdateZoneColor(activeZone);
                                
                                if (currentState != previousState)
                                {
                                    Server.PrintToConsole($"[HardpointCS2] Zone {activeZone.Name} state changed: {previousState} -> {currentState}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Server.PrintToConsole($"[HardpointCS2] Error updating zone color: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[HardpointCS2] Error in CheckPlayerZones: {ex.Message}");
                }
            });
        }

        private void CheckReadyStatus()
        {
            var activePlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true && 
                        p.Connected == PlayerConnectedState.PlayerConnected && 
                        !p.IsBot)
                .ToList();

            if (activePlayers.Count < 2)
            {
                Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.Red}Need at least 2 players to start!{ChatColors.Default}");
                return;
            }

            bool canStart = false;

            if (_requireTeamReady)
            {
                // Check if at least one player from each team is ready
                var readyCT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.CounterTerrorist);
                var readyT = _readyPlayers.Any(p => p.IsValid && p.TeamNum == (byte)CsTeam.Terrorist);
                
                var ctPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
                var tPlayers = activePlayers.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();
                
                canStart = readyCT && readyT && ctPlayers.Count > 0 && tPlayers.Count > 0;
            }
            else
            {
                // Check if all players are ready
                canStart = activePlayers.All(p => _readyPlayers.Contains(p));
            }

            if (canStart)
            {
                Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.Yellow}All players ready! Starting game in 3 seconds...{ChatColors.Default}");
                AddTimer(3.0f, StartGame);
            }
        }

        private void StartGame()
        {
            _gamePhase = GamePhase.Active;
            _readyPlayers.Clear();
            
            Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.Yellow}🎮 GAME STARTED! 🎮{ChatColors.Default}");
            
            // Reset scores
            _ctScore = 0;
            _tScore = 0;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;
            _previousZone = null;
            
            UpdateTeamScore(CsTeam.CounterTerrorist, 0);
            UpdateTeamScore(CsTeam.Terrorist, 0);
            
            // Start the first zone
            DrawRandomZone();
            
            // Start timers
            _zoneCheckTimer?.Start();
            _hardpointTimer?.Start();
        }
        
        #region Commands

        [ConsoleCommand("css_savezones", "Saves all zones to file.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandSaveZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var mapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(mapName, _zoneManager.Zones);
            commandInfo.ReplyToCommand($"Saved {_zoneManager?.Zones.Count} zones for map {mapName}");
        }

        // Add command to reload zones
        [ConsoleCommand("css_reloadzones", "Reloads zones from file.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandReloadZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var mapName = Server.MapName;
            _zoneVisualization?.ClearZoneVisualization();
            _zoneManager?.LoadZonesForMap(mapName);

            foreach (var zone in _zoneManager?.Zones ?? new List<Zone>())
            {
                _zoneVisualization?.DrawZone(zone);
            }

            commandInfo.ReplyToCommand($"Reloaded {_zoneManager?.Zones.Count} zones for map {mapName}");
        }

        [ConsoleCommand("css_addzone", "Creates a new hardpoint zone.")]
        [CommandHelper(minArgs: 1, usage: "[ZONE_NAME]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid)
            {
                commandInfo.ReplyToCommand("Command must be used by a player.");
                return;
            }

            if (player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("You must have an alive player pawn.");
                return;
            }

            var zoneName = commandInfo.GetArg(1);
            if (string.IsNullOrWhiteSpace(zoneName))
            {
                commandInfo.ReplyToCommand("You must specify a zone name.");
                return;
            }

            if (_activeZones.ContainsKey(player))
            {
                commandInfo.ReplyToCommand("You already have an active zone. Use css_endzone to finish it first.");
                return;
            }

            var playerPos = new CSVector(player.PlayerPawn.Value.AbsOrigin!.X, 
                                       player.PlayerPawn.Value.AbsOrigin!.Y, 
                                       player.PlayerPawn.Value.AbsOrigin!.Z);

            var newZone = new Zone
            {
                Name = zoneName,
                Points = new List<CSVector> { playerPos }
            };

            _activeZones[player] = newZone;
            commandInfo.ReplyToCommand($"Zone '{zoneName}' started. Use css_addpoint to add more points, css_endzone to finish.");
        }

        [ConsoleCommand("css_addpoint", "Adds a point to the current zone.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddPoint(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid)
            {
                commandInfo.ReplyToCommand("Command must be used by a player.");
                return;
            }

            if (player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("You must have an alive player pawn.");
                return;
            }

            if (!_activeZones.ContainsKey(player))
            {
                commandInfo.ReplyToCommand("You don't have an active zone. Use css_addzone [name] to start one.");
                return;
            }

            var zone = _activeZones[player];
            var playerPos = new CSVector(player.PlayerPawn.Value.AbsOrigin!.X, 
                                       player.PlayerPawn.Value.AbsOrigin!.Y, 
                                       player.PlayerPawn.Value.AbsOrigin!.Z);

            // Calculate distance manually
            foreach (var point in zone.Points)
            {
                var dx = playerPos.X - point.X;
                var dy = playerPos.Y - point.Y;
                var dz = playerPos.Z - point.Z;
                var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                
                if (distance < 50.0f)
                {
                    commandInfo.ReplyToCommand("Too close to an existing point. Move away and try again.");
                    return;
                }
            }

            zone.Points.Add(playerPos);
            commandInfo.ReplyToCommand($"Point {zone.Points.Count} added to zone '{zone.Name}'. Total points: {zone.Points.Count}");
        }

        [ConsoleCommand("css_endzone", "Completes the current zone and makes it visible.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandEndZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid)
            {
                commandInfo.ReplyToCommand("Command must be used by a player.");
                return;
            }

            if (!_activeZones.ContainsKey(player))
            {
                commandInfo.ReplyToCommand("You don't have an active zone. Use css_addzone [name] to start one.");
                return;
            }

            var zone = _activeZones[player];

            if (zone.Points.Count < 3)
            {
                commandInfo.ReplyToCommand($"Zone needs at least 3 points. Current points: {zone.Points.Count}");
                return;
            }

            var centerX = zone.Points.Sum(p => p.X) / zone.Points.Count;
            var centerY = zone.Points.Sum(p => p.Y) / zone.Points.Count;
            var centerZ = zone.Points.Sum(p => p.Z) / zone.Points.Count;
            zone.Center = new CSVector(centerX, centerY, centerZ);

            Server.PrintToConsole($"[HardpointCS2] Adding zone '{zone.Name}' with {zone.Points.Count} points");
            
            _zoneManager?.AddZone(zone);
            _zoneVisualization?.DrawZone(zone);

            // Debug: Check zone count before saving
            var totalZones = _zoneManager?.Zones.Count ?? 0;
            Server.PrintToConsole($"[HardpointCS2] Total zones in manager: {totalZones}");

            // Save immediately
            var mapName = Server.MapName;
            Server.PrintToConsole($"[HardpointCS2] Calling SaveZonesForMap for map: {mapName}");
            
            if (_zoneManager != null)
            {
                _zoneManager.SaveZonesForMap(mapName, _zoneManager.Zones);
            }

            _activeZones.Remove(player);

            commandInfo.ReplyToCommand($"Zone '{zone.Name}' completed and saved! Total zones: {_zoneManager?.Zones.Count}");
            Logger.LogInformation($"Zone '{zone.Name}' saved for map {mapName}");
        }

        [ConsoleCommand("css_listzones", "Lists all zones.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandListZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var zones = _zoneManager?.Zones ?? new List<Zone>();
            commandInfo.ReplyToCommand($"Total zones: {zones.Count}");
            
            foreach (var zone in zones)
            {
                commandInfo.ReplyToCommand($"- {zone.Name} ({zone.Points.Count} points)");
            }
        }

        [ConsoleCommand("css_debugzoneload", "Debug zone loading.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandDebugZoneLoad(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var mapName = Server.MapName;
            var zonesPath = Path.Combine(ModuleDirectory, "zones");
            
            commandInfo.ReplyToCommand($"Map: {mapName}");
            commandInfo.ReplyToCommand($"Module directory: {ModuleDirectory}");
            commandInfo.ReplyToCommand($"Zones directory: {zonesPath}");
            commandInfo.ReplyToCommand($"Zones in memory: {_zoneManager?.Zones.Count ?? 0}");
            
            var filePath = Path.Combine(zonesPath, $"{mapName}.json");
            commandInfo.ReplyToCommand($"Expected file: {filePath}");
            commandInfo.ReplyToCommand($"File exists: {File.Exists(filePath)}");
            
            if (Directory.Exists(zonesPath))
            {
                var files = Directory.GetFiles(zonesPath, "*.json");
                commandInfo.ReplyToCommand($"JSON files found: {files.Length}");
                foreach (var file in files)
                {
                    commandInfo.ReplyToCommand($"- {Path.GetFileName(file)}");
                }
            }
            else
            {
                commandInfo.ReplyToCommand($"Zones directory doesn't exist!");
            }
        }

        [ConsoleCommand("css_redrawzones", "Redraws all zones.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandRedrawZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_zoneVisualization == null || _zoneManager == null)
            {
                commandInfo.ReplyToCommand("Zone system not initialized");
                return;
            }

            _zoneVisualization.ClearZoneVisualization();
            
            foreach (var zone in _zoneManager.Zones)
            {
                Server.PrintToConsole($"Redrawing zone: {zone.Name}");
                _zoneVisualization.DrawZone(zone);
            }
            
            commandInfo.ReplyToCommand($"Redrawn {_zoneManager.Zones.Count} zones");
        }

        [ConsoleCommand("css_testzone", "Test if you're in a zone.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandTestZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Invalid player.");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            commandInfo.ReplyToCommand($"Your position: {playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1}");
            commandInfo.ReplyToCommand($"Total completed zones: {_zoneManager?.Zones.Count ?? 0}");

            foreach (var zone in _zoneManager?.Zones ?? new List<Zone>())            {
                bool inZone = zone.IsPlayerInZone(playerPos);
                commandInfo.ReplyToCommand($"Zone '{zone.Name}': {(inZone ? "INSIDE" : "OUTSIDE")}");
                
                if (zone.Points.Count > 0)
                {
                    var firstPoint = zone.Points[0];
                    var distance = Math.Sqrt(
                        Math.Pow(playerPos.X - firstPoint.X, 2) +
                        Math.Pow(playerPos.Y - firstPoint.Y, 2)
                    );
                    commandInfo.ReplyToCommand($"Distance to first point: {distance:F1}");
                }
            }
        }

        [ConsoleCommand("css_selectzone", "Selects a specific zone by name.")]
        [CommandHelper(minArgs: 1, usage: "[ZONE_NAME]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandSelectZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var zoneName = commandInfo.GetArg(1);
            var zone = _zoneManager?.Zones.FirstOrDefault(z => z.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
            
            if (zone != null)
            {
                // Clear all existing visualizations
                _zoneVisualization?.ClearZoneVisualization();
                
                // Set and draw the selected zone
                activeZone = zone;
                _zoneVisualization?.DrawZone(activeZone);
                
                commandInfo.ReplyToCommand($"Selected and drew zone: {zone.Name}");
                Server.PrintToConsole($"[HardpointCS2] Manually selected zone: {zone.Name}");
            }
            else
            {
                commandInfo.ReplyToCommand($"Zone '{zoneName}' not found");
                
                // List available zones
                var availableZones = string.Join(", ", _zoneManager?.Zones.Select(z => z.Name) ?? new List<string>());
                commandInfo.ReplyToCommand($"Available zones: {availableZones}");
            }
        }

        [ConsoleCommand("css_clearzone", "Clears the active zone.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandClearZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            activeZone = null;
            _zoneVisualization?.ClearZoneVisualization();
            commandInfo.ReplyToCommand("Cleared active zone - no zones are now active");
            Server.PrintToConsole("[HardpointCS2] Cleared active zone");
        }

        [ConsoleCommand("css_hardpointstatus", "Shows current hardpoint status.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandHardpointStatus(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (activeZone == null)
            {
                commandInfo.ReplyToCommand("No active zone");
                return;
            }

            var zoneState = activeZone.GetZoneState();
            commandInfo.ReplyToCommand($"Zone: {activeZone.Name}");
            commandInfo.ReplyToCommand($"State: {zoneState}");
            commandInfo.ReplyToCommand($"CT Time: {_ctZoneTime:F1}s / {CAPTURE_TIME}s");
            commandInfo.ReplyToCommand($"T Time: {_tZoneTime:F1}s / {CAPTURE_TIME}s");
            commandInfo.ReplyToCommand($"Score: CT {_ctScore} - T {_tScore}");
            commandInfo.ReplyToCommand($"Players in zone: {activeZone.PlayersInZone.Count}");
        }

        [ConsoleCommand("css_ready", "Mark yourself as ready to start the game.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnCommandReady(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid)
                return;

            if (_gamePhase != GamePhase.Warmup)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Game is already in progress!{ChatColors.Default}");
                return;
            }

            if (_readyPlayers.Contains(player))
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}You are already ready!{ChatColors.Default}");
                return;
            }

            _readyPlayers.Add(player);
            Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.LightBlue}{player.PlayerName}{ChatColors.Default} is ready!");
            
            CheckReadyStatus();
        }

        [ConsoleCommand("css_unready", "Mark yourself as not ready.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnCommandUnready(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player == null || !player.IsValid)
                return;

            if (_gamePhase != GamePhase.Warmup)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Game is already in progress!{ChatColors.Default}");
                return;
            }

            if (_readyPlayers.Remove(player))
            {
                Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.Red}{player.PlayerName}{ChatColors.Default} is no longer ready!");
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}You are no longer ready.{ChatColors.Default}");
            }
            else
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You were not ready!{ChatColors.Default}");
            }
        }

        [ConsoleCommand("css_start", "Force start the game (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandStart(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_gamePhase != GamePhase.Warmup)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Game is already in progress!{ChatColors.Default}");
                return;
            }

            Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.Yellow}Admin forced game start!{ChatColors.Default}");
            StartGame();
        }

        [ConsoleCommand("css_stop", "Stop the game and return to warmup (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandStop(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_gamePhase == GamePhase.Warmup)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}Game is already in warmup phase!{ChatColors.Default}");
                return;
            }

            _gamePhase = GamePhase.Warmup;
            _readyPlayers.Clear();
            
            // Stop timers and clear zones
            _zoneCheckTimer?.Stop();
            _hardpointTimer?.Stop();
            _zoneVisualization?.ClearZoneVisualization();
            activeZone = null;
            
            // Reset scores
            _ctScore = 0;
            _tScore = 0;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;
            _previousZone = null;
            
            UpdateTeamScore(CsTeam.CounterTerrorist, 0);
            UpdateTeamScore(CsTeam.Terrorist, 0);
            
            // Restart HUD timer for warmup messages
            _hardpointTimer?.Start();
            
            Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - {ChatColors.Red}Game stopped by admin! Back to warmup.{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Yellow}⏳ Type !ready to start the game{ChatColors.Default}");
        }

        [ConsoleCommand("css_readyconfig", "Configure ready system (Admin only). Usage: css_readyconfig <team|all>")]
        [CommandHelper(minArgs: 1, usage: "<team|all>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandReadyConfig(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var mode = commandInfo.GetArg(1).ToLower();
            
            if (mode == "team")
            {
                _requireTeamReady = true;
                Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - Ready mode set to: {ChatColors.Yellow}One player per team{ChatColors.Default}");
            }
            else if (mode == "all")
            {
                _requireTeamReady = false;
                Server.PrintToChatAll($"{ChatColors.Green}[Hardpoint CS2]{ChatColors.Default} - Ready mode set to: {ChatColors.Yellow}All players{ChatColors.Default}");
            }
            else
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Usage: css_readyconfig <team|all>{ChatColors.Default}");
            }
        }
        #endregion
    }
}