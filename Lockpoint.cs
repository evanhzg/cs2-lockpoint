using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API; 
using Microsoft.Extensions.Logging;
using Lockpoint.Services;
using Lockpoint.Models;
using Lockpoint.Enums;
using System.Collections.Generic;
using System.Linq;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Lockpoint
{
    public class Lockpoint : BasePlugin
    {
        public override string ModuleName => "Lockpoint";
        public override string ModuleVersion => "0.2.1";
        public override string ModuleAuthor => "evanhh";
        public override string ModuleDescription => "Lockpoint game mode for CS2";

        private readonly Dictionary<CCSPlayerController, DateTime> _playerDeathTimes = new();
        private readonly Dictionary<CCSPlayerController, System.Timers.Timer> _respawnTimers = new();
        private readonly float RESPAWN_DELAY = 5.0f; // 5 seconds

        private Zone? _zoneBeingEdited = null;
        private bool _isEditingExistingZone = false;
        
        private ZoneVisualization? _zoneVisualization;
        private ZoneManager? _zoneManager;
        private readonly Dictionary<CCSPlayerController, Zone> _activeZones = new();
        private System.Timers.Timer? _zoneCheckTimer;
        private Zone? activeZone;

        private GamePhase _gamePhase = GamePhase.Warmup;
        private GamePhase _previousGamePhase = GamePhase.Warmup;
        private bool _editMode = false;
        private bool _previousCheatsEnabled = false;
        private readonly HashSet<CCSPlayerController> _readyPlayers = new();
        private bool _requireTeamReady = true; // Configurable: true = one per team, false = all players
        private DateTime _lastReadyMessage = DateTime.MinValue;
        private readonly double _readyMessageInterval = 10.0; // 10 seconds

        private float _ctZoneTime = 0f;
        private float _tZoneTime = 0f;
        private int _ctScore = 0;
        private int _tScore = 0;
        private System.Timers.Timer? _LockpointTimer;
        private Zone? _previousZone = null;
        private readonly float CAPTURE_TIME = 10f; // 10 seconds to capture
        private readonly float TIMER_INTERVAL = 100f; // 100ms updates
        private bool _waitingForNewZone = false;
        private DateTime _zoneResetTime;
        private string _lastCaptureTeam = "";
        private readonly double _newZoneTimer = 5.0; // 5 seconds
        

        public override void Load(bool hotReload)
        {
            Logger.LogInformation("Lockpoint plugin loaded");
            _zoneVisualization = new ZoneVisualization();
            _zoneManager = new ZoneManager(ModuleDirectory);

            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath); // Add this
            RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect); // Add this

            _zoneCheckTimer = new System.Timers.Timer(50);
            _zoneCheckTimer.Elapsed += CheckPlayerZones;
            _zoneCheckTimer.Start();

            // Initialize Lockpoint timer
            _LockpointTimer = new System.Timers.Timer(TIMER_INTERVAL);
            _LockpointTimer.Elapsed += UpdateLockpointTimer;
            _LockpointTimer.Start();
        }

        private void UpdateLockpointTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    // Always update HUD regardless of phase
                    UpdateLockpointHUD();

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
                    Server.PrintToConsole($"[Lockpoint] Error in UpdateLockpointTimer: {ex.Message}");
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
                        Server.PrintToConsole($"[Lockpoint] Updated {team} score to {newScore}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error updating team score: {ex.Message}");
            }
        }

        private void UpdateLockpointHUD()
        {
            // Handle edit mode
            if (_gamePhase == GamePhase.EditMode)
            {
                UpdateEditModeHUD();
                return;
            }
            // Handle warmup phase
            if (_gamePhase == GamePhase.Warmup)
            {
                UpdateWarmupHUD();
                return;
            }

            // Check if we're in the waiting period between zones
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
                        // Only show zone message if player is alive (not waiting for respawn)
                        if (player.PawnIsAlive && !_playerDeathTimes.ContainsKey(player))
                        {
                            player.PrintToCenter(waitMessage);
                        }
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

            // Print to center of screen for living players only
            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true && 
                    player.Connected == PlayerConnectedState.PlayerConnected && 
                    !player.IsBot)
                {
                    // Only show zone message if player is alive (not waiting for respawn)
                    if (player.PawnIsAlive && !_playerDeathTimes.ContainsKey(player))
                    {
                        player.PrintToCenter(statusMessage);
                    }
                }
            }
        }

        private void UpdateEditModeHUD()
        {
            var zoneCount = _zoneManager?.Zones?.Count ?? 0;
            var editMessage = $"🔧 EDIT MODE - {zoneCount} zones | Use !createzone, !removezone";
            
            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true && 
                    player.Connected == PlayerConnectedState.PlayerConnected && 
                    !player.IsBot)
                {
                    player.PrintToCenter(editMessage);
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
                    warmupMessage = "⏳ At least 2 players required to play Lockpoint...";
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

            // Send warmup message to ALL players (dead players don't get respawn timer in warmup)
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
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}Not ready: {notReadyNames}{ChatColors.Default}");
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
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} -{ChatColors.Orange} Zone cleared!{ChatColors.Default} Score: {ChatColors.LightBlue}CT {_ctScore}{ChatColors.Default} - {ChatColors.Red}T {_tScore}{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - ⏱{ChatColors.Yellow} New zone in 5 seconds...{ChatColors.Default}");
    
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
            
            Server.PrintToChatAll($"{ChatColors.Green}🎯 New Lockpoint: {ChatColors.Yellow}{activeZone.Name}{ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] New zone selected: {activeZone.Name}");
        }

        private void OnMapStart(string mapName)
        {
            Server.NextFrame(() =>
            {
                Server.PrintToConsole($"[Lockpoint] Map started: {mapName}");
                Logger.LogInformation($"Loading zones for map: {mapName}");
                
                _zoneManager?.LoadZonesForMap(mapName);

                Logger.LogInformation($"Loaded {_zoneManager?.Zones.Count ?? 0} zones for map {mapName}");
            });
        }

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            // Stop timers during round transition
            _zoneCheckTimer?.Stop();
            _LockpointTimer?.Stop();
            
            // Clear all respawn timers
            CleanupAllRespawnTimers();
            
            // Only reset game state if game is active
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
                    
                    // Respawn all players with appropriate spawn logic
                    RespawnAllPlayers();
                    
                    if (_gamePhase == GamePhase.Active)
                    {
                        // Wait for respawns to complete before starting zones
                        AddTimer(1.0f, () =>
                        {
                            Server.NextFrame(() =>
                            {
                                DrawRandomZone();
                                
                                // Restart timers only if game is active
                                _zoneCheckTimer?.Start();
                                _LockpointTimer?.Start();
                                
                                Server.PrintToChatAll($"{ChatColors.Green}🎮 Lockpoint round started!{ChatColors.Default}");
                            });
                        });
                    }
                    else
                    {
                        // In warmup, only start the HUD timer for warmup messages
                        _LockpointTimer?.Start();
                        Server.PrintToChatAll($"{ChatColors.Yellow}⏳ Warmup phase - Type !ready to start{ChatColors.Default}");
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error in OnRoundStart: {ex.Message}");
                    if (_gamePhase == GamePhase.Active)
                    {
                        _zoneCheckTimer?.Start();
                    }
                    _LockpointTimer?.Start();
                }
            });
            
            return HookResult.Continue;
        }

        private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            var player = @event.Userid;
            
            if (player?.IsValid == true)
            {
                // Clean up any respawn timers for disconnecting player
                CleanupRespawnTimer(player);
                
                // Remove from ready list if they were ready
                _readyPlayers.Remove(player);
            }
            
            return HookResult.Continue;
        }

        private void CleanupAllRespawnTimers()
        {
            try
            {
                foreach (var timer in _respawnTimers.Values)
                {
                    timer.Stop();
                    timer.Dispose();
                }
                _respawnTimers.Clear();
                _playerDeathTimes.Clear();
                
                Server.PrintToConsole("[Lockpoint] Cleaned up all respawn timers");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error cleaning up respawn timers: {ex.Message}");
            }
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
                    Server.PrintToConsole($"[Lockpoint] Error drawing active zone {activeZone.Name}: {ex.Message}");
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
                Server.PrintToConsole($"[Lockpoint] Drew random zone: {randomZone.Name}");
                Server.PrintToChatAll($"{ChatColors.Green}🎯 Active Lockpoint: {ChatColors.Yellow}{randomZone.Name}{ChatColors.Default}");
                activeZone = randomZone;
                _ctZoneTime = 0f;
                _tZoneTime = 0f;
            }
            else
            {
                Server.PrintToConsole("[Lockpoint] No zones available to draw");
                Server.PrintToChatAll($"{ChatColors.Red}⚠ No zones available to draw!{ChatColors.Default}");
                activeZone = null;
            }
        }

        public override void Unload(bool hotReload)
        {
            _zoneCheckTimer?.Stop();
            _zoneCheckTimer?.Dispose();
            _LockpointTimer?.Stop();
            _LockpointTimer?.Dispose();

            CleanupAllRespawnTimers();

            _zoneVisualization?.ClearZoneVisualization();
            Logger.LogInformation("Lockpoint plugin unloaded");
        }

        private void CheckPlayerZones(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    if (_gamePhase == GamePhase.EditMode) return;
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
                                        Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} entered zone {activeZone.Name}");
                                    }
                                }
                                else
                                {
                                    // Check if this player just left
                                    if (previousPlayers.Contains(player))
                                    {
                                        Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} left zone {activeZone.Name}");
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
                                    Server.PrintToConsole($"[Lockpoint] Zone {activeZone.Name} state changed: {previousState} -> {currentState}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Server.PrintToConsole($"[Lockpoint] Error updating zone color: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error in CheckPlayerZones: {ex.Message}");
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
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}Need at least 2 players to start!{ChatColors.Default}");
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
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}All players ready! Starting game in 3 seconds...{ChatColors.Default}");
                AddTimer(3.0f, StartGame);
            }
        }

        private void StartGame()
        {
            _gamePhase = GamePhase.Active;
            _readyPlayers.Clear();
            
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}🎮 GAME STARTED! 🎮{ChatColors.Default}");
            
            // Reset scores
            _ctScore = 0;
            _tScore = 0;
            _ctZoneTime = 0f;
            _tZoneTime = 0f;
            _previousZone = null;
            
            UpdateTeamScore(CsTeam.CounterTerrorist, 0);
            UpdateTeamScore(CsTeam.Terrorist, 0);
            
            // Respawn all players for the new game phase
            Server.NextFrame(() =>
            {
                RespawnAllPlayers();
                
                // Wait a moment for respawns to complete, then start the first zone
                AddTimer(1.0f, () =>
                {
                    DrawRandomZone();
                    
                    // Start timers
                    _zoneCheckTimer?.Start();
                    _LockpointTimer?.Start();
                });
            });
        }

        private void RespawnAllPlayers()
        {
            Server.PrintToConsole("[Lockpoint] Respawning all players...");
            
            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true && 
                    player.Connected == PlayerConnectedState.PlayerConnected && 
                    !player.IsBot &&
                    player.TeamNum != (byte)CsTeam.None &&
                    player.TeamNum != (byte)CsTeam.Spectator)
                {
                    try
                    {
                        // Force respawn everyone, dead or alive
                        player.Respawn();
                        
                        // If in warmup phase, teleport to random spawn after respawn
                        if (_gamePhase == GamePhase.Warmup)
                        {
                            AddTimer(0.2f, () =>
                            {
                                if (player?.IsValid == true && player.PawnIsAlive && player.PlayerPawn?.Value != null)
                                {
                                    var randomSpawn = GetRandomSpawnPointAny();
                                    if (randomSpawn != null)
                                    {
                                        player.PlayerPawn.Value.Teleport(randomSpawn, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                        Server.PrintToConsole($"[Lockpoint] Teleported {player.PlayerName} to random warmup spawn");
                                    }
                                }
                            });
                        }
                        
                        Server.PrintToConsole($"[Lockpoint] Respawned player: {player.PlayerName}");
                        player.PrintToChat($"{ChatColors.Green}You have been respawned for the new phase!{ChatColors.Default}");
                    }
                    catch (Exception ex)
                    {
                        Server.PrintToConsole($"[Lockpoint] Error respawning player {player.PlayerName}: {ex.Message}");
                        
                        // Fallback: try teleporting to spawn if respawn fails
                        try
                        {
                            Vector? spawnPoint;
                            
                            if (_gamePhase == GamePhase.Warmup)
                            {
                                // Use random spawn during warmup
                                spawnPoint = GetRandomSpawnPointAny();
                            }
                            else
                            {
                                // Use team spawn during active game
                                spawnPoint = GetRandomSpawnPoint(player.TeamNum);
                            }
                            
                            if (spawnPoint != null && player.PlayerPawn?.Value != null)
                            {
                                player.PlayerPawn.Value.Teleport(spawnPoint, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                player.PlayerPawn.Value.Health = 100;
                                Server.PrintToConsole($"[Lockpoint] Teleported player {player.PlayerName} to spawn as fallback");
                            }
                        }
                        catch (Exception teleportEx)
                        {
                            Server.PrintToConsole($"[Lockpoint] Fallback teleport failed for {player.PlayerName}: {teleportEx.Message}");
                        }
                    }
                }
            }
        }

        private Vector? GetRandomSpawnPoint(byte teamNum)
        {
            try
            {
                // Get spawn points for the team
                var spawnEntities = teamNum == (byte)CsTeam.CounterTerrorist 
                    ? Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist")
                    : Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist");

                if (spawnEntities.Any())
                {
                    var random = new Random();
                    var spawnPoint = spawnEntities.ElementAt(random.Next(spawnEntities.Count()));
                    return spawnPoint.AbsOrigin;
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting spawn point: {ex.Message}");
            }
            
            return null;
        }

        private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            var player = @event.Userid;
            
            if (player?.IsValid != true || player.IsBot)
                return HookResult.Continue;

            try
            {
                if (_gamePhase == GamePhase.Warmup)
                {
                    // Instant respawn during warmup at a random spawn point
                    Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} died in warmup, respawning instantly");
                    
                    AddTimer(0.1f, () => // Very short delay to ensure death is processed
                    {
                        if (player?.IsValid == true && !player.PawnIsAlive)
                        {
                            RespawnPlayerAtRandomSpawn(player);
                        }
                    });
                }
                else if (_gamePhase == GamePhase.Active)
                {
                    // 5-second respawn timer during active game
                    _playerDeathTimes[player] = DateTime.Now;
                    
                    // Clear any existing respawn timer for this player
                    if (_respawnTimers.ContainsKey(player))
                    {
                        _respawnTimers[player].Stop();
                        _respawnTimers[player].Dispose();
                        _respawnTimers.Remove(player);
                    }

                    Server.PrintToConsole($"[Lockpoint] Player {player.PlayerName} died, will respawn in {RESPAWN_DELAY} seconds");

                    // Create respawn timer with null check
                    var respawnTimer = new System.Timers.Timer(100); // Update every 100ms for smooth countdown
                    respawnTimer.Elapsed += (sender, e) => 
                    {
                        if (player?.IsValid == true)
                        {
                            UpdateRespawnCountdown(player);
                        }
                        else
                        {
                            respawnTimer.Stop();
                            respawnTimer.Dispose();
                        }
                    };
                    respawnTimer.Start();
                    
                    _respawnTimers[player] = respawnTimer;

                    // Schedule the actual respawn with null check
                    AddTimer(RESPAWN_DELAY, () => 
                    {
                        if (player?.IsValid == true)
                        {
                            RespawnPlayer(player);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error handling player death: {ex.Message}");
            }

            return HookResult.Continue;
        }

        private void RespawnPlayerAtRandomSpawn(CCSPlayerController player)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    if (player?.IsValid != true)
                        return;

                    // Try to respawn the player first
                    player.Respawn();
                    
                    // Then teleport to a deathmatch spawn point after a small delay
                    AddTimer(0.2f, () =>
                    {
                        if (player?.IsValid == true && player.PawnIsAlive && player.PlayerPawn?.Value != null)
                        {
                            var randomSpawn = GetDeathmatchSpawn(); // Use deathmatch spawns preferentially
                            if (randomSpawn != null)
                            {
                                player.PlayerPawn.Value.Teleport(randomSpawn, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                Server.PrintToConsole($"[Lockpoint] Teleported {player.PlayerName} to deathmatch spawn");
                            }
                        }
                    });
                    
                    player.PrintToChat($"{ChatColors.Green}Respawned instantly (warmup mode)!{ChatColors.Default}");
                    Server.PrintToConsole($"[Lockpoint] Instantly respawned player: {player.PlayerName}");
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error instantly respawning player {player?.PlayerName}: {ex.Message}");
                }
            });
        }

        private Vector? GetRandomSpawnPointAny()
        {
            try
            {
                var allSpawns = new List<Vector>();
                
                // First priority: Use deathmatch spawns if they exist
                var dmSpawns = Utilities.FindAllEntitiesByDesignerName<CInfoDeathmatchSpawn>("info_deathmatch_spawn");
                if (dmSpawns.Any())
                {
                    allSpawns.AddRange(dmSpawns
                        .Where(spawn => spawn?.AbsOrigin != null)
                        .Select(spawn => spawn.AbsOrigin!));
                    
                    Server.PrintToConsole($"[Lockpoint] Found {dmSpawns.Count()} deathmatch spawn points");
                }
                
                // If no deathmatch spawns, fall back to regular spawns
                if (!allSpawns.Any())
                {
                    // Add CT spawns
                    var ctSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist");
                    allSpawns.AddRange(ctSpawns
                        .Where(spawn => spawn?.AbsOrigin != null)
                        .Select(spawn => spawn.AbsOrigin!));
                    
                    // Add T spawns
                    var tSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist");
                    allSpawns.AddRange(tSpawns
                        .Where(spawn => spawn?.AbsOrigin != null)
                        .Select(spawn => spawn.AbsOrigin!));
                    
                    Server.PrintToConsole($"[Lockpoint] No deathmatch spawns found, using {allSpawns.Count} team spawn points");
                }
                
                // Try other spawn types as additional options
                var armsraceSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_armsrace_spawn");
                allSpawns.AddRange(armsraceSpawns
                    .Where(spawn => spawn?.AbsOrigin != null)
                    .Select(spawn => spawn.AbsOrigin!));

                // Also add zone center points as potential spawns during warmup
                if (_zoneManager?.Zones != null)
                {
                    foreach (var zone in _zoneManager.Zones)
                    {
                        if (zone.Points?.Count > 0)
                        {
                            // Calculate zone center
                            var centerX = zone.Points.Average(p => p.X);
                            var centerY = zone.Points.Average(p => p.Y);
                            var centerZ = zone.Points.Average(p => p.Z);
                            allSpawns.Add(new Vector(centerX, centerY, centerZ + 10)); // +10 to spawn slightly above ground
                        }
                    }
                }

                if (allSpawns.Any())
                {
                    var random = new Random();
                    var selectedSpawn = allSpawns[random.Next(allSpawns.Count)];
                    Server.PrintToConsole($"[Lockpoint] Selected random spawn from {allSpawns.Count} available spawns");
                    return selectedSpawn;
                }
                else
                {
                    Server.PrintToConsole("[Lockpoint] No spawn points found");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting random spawn point: {ex.Message}");
                return null;
            }
        }

        private Vector? GetDeathmatchSpawn()
        {
            try
            {
                // Try to get deathmatch spawns first
                var dmSpawns = Utilities.FindAllEntitiesByDesignerName<CInfoDeathmatchSpawn>("info_deathmatch_spawn");
                
                if (dmSpawns.Any())
                {
                    var random = new Random();
                    var selectedSpawn = dmSpawns.ElementAt(random.Next(dmSpawns.Count()));
                    Server.PrintToConsole($"[Lockpoint] Using deathmatch spawn point");
                    return selectedSpawn.AbsOrigin;
                }
                
                // Fallback to regular random spawn
                Server.PrintToConsole("[Lockpoint] No deathmatch spawns found, falling back to regular spawns");
                return GetRandomSpawnPointAny();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting deathmatch spawn: {ex.Message}");
                return GetRandomSpawnPointAny();
            }
        }

        private void UpdateRespawnCountdown(CCSPlayerController? player)
        {
            if (player?.IsValid != true)
                return;
                
            Server.NextFrame(() =>
            {
                try
                {
                    if (player?.IsValid != true || !_playerDeathTimes.ContainsKey(player))
                        return;

                    // Check if player is already alive (respawned by other means)
                    if (player.PawnIsAlive)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    var timeSinceDeath = (DateTime.Now - _playerDeathTimes[player]).TotalSeconds;
                    var remainingTime = Math.Max(0, RESPAWN_DELAY - timeSinceDeath);

                    if (remainingTime <= 0)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    // Show countdown message with only seconds (no decimals)
                    var remainingSeconds = (int)Math.Ceiling(remainingTime);
                    var countdownMessage = $"💀 Respawning in {remainingSeconds}s...";
                    player.PrintToCenter(countdownMessage);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error updating respawn countdown: {ex.Message}");
                }
            });
        }

        private void RespawnPlayer(CCSPlayerController? player)
        {
            if (player?.IsValid != true)
                return;
                
            Server.NextFrame(() =>
            {
                try
                {
                    if (player?.IsValid != true || player.PawnIsAlive)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    // Only respawn if game is still active
                    if (_gamePhase != GamePhase.Active)
                    {
                        CleanupRespawnTimer(player);
                        return;
                    }

                    player.Respawn();

                    // Use zone-based spawn after respawn
                    AddTimer(0.2f, () =>
                    {
                        if (player?.IsValid == true && player.PawnIsAlive && player.PlayerPawn?.Value != null)
                        {
                            var spawnPoint = GetZoneBasedSpawn(player.TeamNum); // This should use zone spawns
                            if (spawnPoint != null)
                            {
                                player.PlayerPawn.Value.Teleport(spawnPoint, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                                Server.PrintToConsole($"[Lockpoint] Teleported {player.PlayerName} to zone-based spawn");
                            }
                        }
                    });
                    
                    player.PrintToChat($"{ChatColors.Green}You have been respawned!{ChatColors.Default}");
                    Server.PrintToConsole($"[Lockpoint] Respawned player: {player.PlayerName}");
                    
                    CleanupRespawnTimer(player);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[Lockpoint] Error respawning player {player?.PlayerName}: {ex.Message}");
                }
            });
        }

        private void CleanupRespawnTimer(CCSPlayerController? player)
        {
            if (player?.IsValid != true)
                return;
                
            try
            {
                if (_respawnTimers.ContainsKey(player))
                {
                    _respawnTimers[player].Stop();
                    _respawnTimers[player].Dispose();
                    _respawnTimers.Remove(player);
                }
                
                _playerDeathTimes.Remove(player);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error cleaning up respawn timer: {ex.Message}");
            }
        }
        
        #region Commands

        [ConsoleCommand("css_savezones", "Saves all zones to file.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandSaveZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to create zones! Use !edit{ChatColors.Default}");
                return;
            }
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

        [ConsoleCommand("css_addzone", "Creates a new Lockpoint zone.")]
        [CommandHelper(minArgs: 1, usage: "[ZONE_NAME]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to create zones! Use !edit{ChatColors.Default}");
                return;
            }

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
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to create zones! Use !edit{ChatColors.Default}");
                return;
            }
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

        [ConsoleCommand("css_lastpoint", "Add the last point and finish the zone area (but don't save yet).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandLastPoint(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (!_activeZones.ContainsKey(player))
            {
                commandInfo.ReplyToCommand("You don't have an active zone. Use css_addzone first.");
                return;
            }

            var zone = _activeZones[player];
            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            zone.Points.Add(playerPos);
            
            // Move to editing state but don't save yet
            _zoneBeingEdited = zone;
            _isEditingExistingZone = false;
            _activeZones.Remove(player);

            // Draw the zone for visualization
            _zoneVisualization?.DrawZone(zone);
            
            commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{zone.Name}' area completed with {zone.Points.Count} points. Add spawn points then use css_endzone to save.{ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Zone '{zone.Name}' area finished by {player.PlayerName}");
        }

        [ConsoleCommand("css_endzone", "Save the current zone being edited.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandEndZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use !edit{ChatColors.Default}");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand("No zone is being edited. Create a zone area first.");
                return;
            }

            try
            {
                var currentMapName = Server.MapName;
                
                if (!_isEditingExistingZone)
                {
                    // Add new zone
                    if (_zoneManager?.Zones != null)
                    {
                        _zoneManager.Zones.Add(_zoneBeingEdited);
                    }
                }
                // If editing existing zone, it's already in the list and modified by reference

                // Save zones with updated spawn points
                _zoneManager?.SaveZonesForMap(currentMapName, _zoneManager.Zones);
                
                commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{_zoneBeingEdited.Name}' saved successfully! (T:{_zoneBeingEdited.TerroristSpawns.Count} CT:{_zoneBeingEdited.CounterTerroristSpawns.Count} spawns){ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Zone '{_zoneBeingEdited.Name}' saved by {player?.PlayerName}");

                // Clear spawn visualization for the zone we just finished editing
                _zoneVisualization?.ClearSpawnPoints(_zoneBeingEdited);

                // Clear editing state
                _zoneBeingEdited = null;
                _isEditingExistingZone = false;

                // Refresh zone visualization (without spawns)
                DrawAllZonesForEdit();
            }
            catch (Exception ex)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Error saving zone: {ex.Message}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Error saving zone: {ex.Message}");
            }
        }

        [ConsoleCommand("css_editzone", "Edit an existing zone (closest or by name).")]
        [CommandHelper(minArgs: 0, usage: "[zone_name]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandEditZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (_zoneBeingEdited != null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Already editing zone '{_zoneBeingEdited.Name}'. Use css_endzone to save or css_cancelzone to cancel.{ChatColors.Default}");
                return;
            }

            var zoneName = commandInfo.GetArg(1);
            Zone? zoneToEdit = null;

            if (string.IsNullOrWhiteSpace(zoneName))
            {
                // Find closest zone
                zoneToEdit = FindClosestZone(player);
                if (zoneToEdit == null)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}No zones found nearby.{ChatColors.Default}");
                    return;
                }
            }
            else
            {
                // Find by name
                zoneToEdit = _zoneManager?.Zones?.FirstOrDefault(z => 
                    string.Equals(z.Name, zoneName, StringComparison.OrdinalIgnoreCase));
                
                if (zoneToEdit == null)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}Zone '{zoneName}' not found.{ChatColors.Default}");
                    return;
                }
            }

            _zoneBeingEdited = zoneToEdit;
            _isEditingExistingZone = true;
            
            // Show spawn points for this zone
            _zoneVisualization?.DrawSpawnPoints(_zoneBeingEdited);
            
            commandInfo.ReplyToCommand($"{ChatColors.Green}Now editing zone '{zoneToEdit.Name}' (T:{zoneToEdit.TerroristSpawns.Count} CT:{zoneToEdit.CounterTerroristSpawns.Count} spawns). Use css_addspawn/css_removespawn to modify.{ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] {player.PlayerName} started editing zone '{zoneToEdit.Name}'");
        }

        [ConsoleCommand("css_cancelzone", "Cancel current zone editing.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandCancelZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand("No zone is being edited.");
                return;
            }

            var zoneName = _zoneBeingEdited.Name;
            
            // Clear spawn visualization for the zone we're canceling
            _zoneVisualization?.ClearSpawnPoints(_zoneBeingEdited);
            
            _zoneBeingEdited = null;
            _isEditingExistingZone = false;
            
            commandInfo.ReplyToCommand($"{ChatColors.Yellow}Cancelled editing zone '{zoneName}'.{ChatColors.Default}");
        }

        [ConsoleCommand("css_addspawn", "Add a spawn point to the current zone being edited.")]
        [CommandHelper(minArgs: 1, usage: "<ct|t>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone is being edited. Use css_editzone or create a new zone first.{ChatColors.Default}");
                return;
            }

            var teamArg = commandInfo.GetArg(1).ToLower();
            if (teamArg != "ct" && teamArg != "t")
            {
                commandInfo.ReplyToCommand("Usage: css_addspawn <ct|t>");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            if (teamArg == "ct")
            {
                _zoneBeingEdited.CounterTerroristSpawns.Add(playerPos);
                commandInfo.ReplyToCommand($"{ChatColors.Blue}Added CT spawn to zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.CounterTerroristSpawns.Count} CT spawns total){ChatColors.Default}");
            }
            else
            {
                _zoneBeingEdited.TerroristSpawns.Add(playerPos);
                commandInfo.ReplyToCommand($"{ChatColors.Red}Added T spawn to zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.TerroristSpawns.Count} T spawns total){ChatColors.Default}");
            }

            // Update spawn visualization
            _zoneVisualization?.DrawSpawnPoints(_zoneBeingEdited);
            
            Server.PrintToConsole($"[Lockpoint] Added {teamArg.ToUpper()} spawn to zone '{_zoneBeingEdited.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_removespawn", "Remove the closest spawn point from the current zone being edited.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandRemoveSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (_zoneBeingEdited == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone is being edited. Use css_editzone or create a new zone first.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            // Find closest spawn point
            CSVector? closestSpawn = null;
            bool isCtSpawn = false;
            float closestDistance = float.MaxValue;

            // Check CT spawns
            foreach (var spawn in _zoneBeingEdited.CounterTerroristSpawns)
            {
                var distance = CalculateDistance(playerPos, spawn);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestSpawn = spawn;
                    isCtSpawn = true;
                }
            }

            // Check T spawns
            foreach (var spawn in _zoneBeingEdited.TerroristSpawns)
            {
                var distance = CalculateDistance(playerPos, spawn);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestSpawn = spawn;
                    isCtSpawn = false;
                }
            }

            if (closestSpawn == null || closestDistance > 100.0f) // Within 100 units
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No spawn points found nearby (within 100 units).{ChatColors.Default}");
                return;
            }

            // Remove the closest spawn
            if (isCtSpawn)
            {
                _zoneBeingEdited.CounterTerroristSpawns.Remove(closestSpawn);
                commandInfo.ReplyToCommand($"{ChatColors.Blue}Removed CT spawn from zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.CounterTerroristSpawns.Count} CT spawns remaining){ChatColors.Default}");
            }
            else
            {
                _zoneBeingEdited.TerroristSpawns.Remove(closestSpawn);
                commandInfo.ReplyToCommand($"{ChatColors.Red}Removed T spawn from zone '{_zoneBeingEdited.Name}' ({_zoneBeingEdited.TerroristSpawns.Count} T spawns remaining){ChatColors.Default}");
            }

            // Update spawn visualization
            _zoneVisualization?.DrawSpawnPoints(_zoneBeingEdited);
            
            Server.PrintToConsole($"[Lockpoint] Removed {(isCtSpawn ? "CT" : "T")} spawn from zone '{_zoneBeingEdited.Name}' by {player.PlayerName}");
        }

        private float CalculateDistance(CSVector pos1, CSVector pos2)
        {
            var dx = pos1.X - pos2.X;
            var dy = pos1.Y - pos2.Y;
            var dz = pos1.Z - pos2.Z;
            
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        [ConsoleCommand("css_removezone", "Remove a zone (Admin only, Edit mode required).")]
        [CommandHelper(minArgs: 0, usage: "[zone_name]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandRemoveZone(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to remove zones! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var zoneName = commandInfo.GetArg(1);
            Zone? zoneToRemove = null;

            if (string.IsNullOrWhiteSpace(zoneName))
            {
                // No zone name specified, find the closest zone
                zoneToRemove = FindClosestZone(player);
                
                if (zoneToRemove == null)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}No zones found nearby.{ChatColors.Default}");
                    return;
                }
            }
            else
            {
                // Zone name specified, find by name
                zoneToRemove = _zoneManager?.Zones?.FirstOrDefault(z => 
                    string.Equals(z.Name, zoneName, StringComparison.OrdinalIgnoreCase));
                
                if (zoneToRemove == null)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}Zone '{zoneName}' not found.{ChatColors.Default}");
                    return;
                }
            }

            try
            {
                // Remove from zone manager
                var mapName = Server.MapName;

                if (_zoneManager?.Zones != null)
                {
                    _zoneManager.Zones.Remove(zoneToRemove);
                    _zoneManager.SaveZonesForMap(mapName, _zoneManager.Zones); // Save the updated zone list
                }
                

                // Clear active zone if it was the one being removed
                if (activeZone == zoneToRemove)
                {
                    activeZone = null;
                }

                // Update visualization in edit mode
                Server.NextFrame(() =>
                {
                    DrawAllZonesForEdit();
                });

                commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{zoneToRemove.Name}' has been removed.{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Yellow}Zone '{zoneToRemove.Name}' was removed by {player.PlayerName}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Zone '{zoneToRemove.Name}' removed by {player.PlayerName}");
            }
            catch (Exception ex)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Error removing zone: {ex.Message}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Error removing zone '{zoneToRemove.Name}': {ex.Message}");
            }
        }

        private Zone? FindClosestZone(CCSPlayerController player)
        {
            if (player?.PlayerPawn?.Value?.AbsOrigin == null || _zoneManager?.Zones == null)
                return null;

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin.X,
                player.PlayerPawn.Value.AbsOrigin.Y,
                player.PlayerPawn.Value.AbsOrigin.Z
            );

            Zone? closestZone = null;
            float closestDistance = float.MaxValue;

            foreach (var zone in _zoneManager.Zones)
            {
                if (zone.Points == null || zone.Points.Count == 0)
                    continue;

                // Calculate distance to zone center
                var centerX = zone.Points.Average(p => p.X);
                var centerY = zone.Points.Average(p => p.Y);
                var centerZ = zone.Points.Average(p => p.Z);
                
                var zoneCenter = new CSVector(centerX, centerY, centerZ);
                var distance = CalculateDistance(playerPos, zoneCenter);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestZone = zone;
                }
            }

            // Only return if the closest zone is within reasonable distance (e.g., 500 units)
            if (closestDistance <= 500.0f)
            {
                return closestZone;
            }

            return null;
        }

        private Vector? GetZoneBasedSpawn(byte teamNum)
        {
            // Only use zone-based spawns during active game phase
            if (_gamePhase != GamePhase.Active || activeZone == null)
            {
                return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
            }

            try
            {
                List<CSVector> teamSpawns = teamNum == (byte)CsTeam.CounterTerrorist 
                    ? activeZone.CounterTerroristSpawns 
                    : activeZone.TerroristSpawns;

                if (teamSpawns.Count > 0)
                {
                    // Try to find a spawn point that's not too close to other players
                    var availableSpawns = FindAvailableSpawnPoints(teamSpawns);
                    
                    if (availableSpawns.Count > 0)
                    {
                        var random = new Random();
                        var spawnPoint = availableSpawns[random.Next(availableSpawns.Count)];
                        Server.PrintToConsole($"[Lockpoint] Using zone-based spawn for team {teamNum} in zone {activeZone.Name}");
                        return new Vector(spawnPoint.X, spawnPoint.Y, spawnPoint.Z);
                    }
                    else
                    {
                        Server.PrintToConsole($"[Lockpoint] All zone spawns occupied for team {teamNum} in zone {activeZone.Name}, using default spawns");
                        return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
                    }
                }
                else
                {
                    Server.PrintToConsole($"[Lockpoint] No zone spawns for team {teamNum} in zone {activeZone.Name}, using default spawns");
                    return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error getting zone-based spawn: {ex.Message}");
                return GetRandomSpawnPoint(teamNum); // Fall back to default spawns
            }
        }

        private List<CSVector> FindAvailableSpawnPoints(List<CSVector> spawnPoints)
        {
            var availableSpawns = new List<CSVector>();
            const float minDistance = 100.0f; // Minimum distance between players (adjust as needed)

            var allPlayers = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true && 
                        p.Connected == PlayerConnectedState.PlayerConnected && 
                        !p.IsBot && 
                        p.PawnIsAlive && 
                        p.PlayerPawn?.Value?.AbsOrigin != null)
                .ToList();

            foreach (var spawnPoint in spawnPoints)
            {
                bool isTooClose = false;

                foreach (var player in allPlayers)
                {
                    var playerPos = new CSVector(
                        player.PlayerPawn!.Value!.AbsOrigin!.X,
                        player.PlayerPawn.Value.AbsOrigin.Y,
                        player.PlayerPawn.Value.AbsOrigin.Z
                    );

                    var distance = CalculateDistance(spawnPoint, playerPos);
                    if (distance < minDistance)
                    {
                        isTooClose = true;
                        break;
                    }
                }

                if (!isTooClose)
                {
                    availableSpawns.Add(spawnPoint);
                }
            }

            return availableSpawns;
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
                Server.PrintToConsole($"[Lockpoint] Manually selected zone: {zone.Name}");
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
            Server.PrintToConsole("[Lockpoint] Cleared active zone");
        }

        [ConsoleCommand("css_Lockpointstatus", "Shows current Lockpoint status.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandLockpointStatus(CCSPlayerController? player, CommandInfo commandInfo)
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
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.LightBlue}{player.PlayerName}{ChatColors.Default} is ready!");
            
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
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}{player.PlayerName}{ChatColors.Default} is no longer ready!");
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

            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}Admin forced game start!{ChatColors.Default}");
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
            _LockpointTimer?.Stop();
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
            
            Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Red}Game stopped by admin! Back to warmup.{ChatColors.Default}");
            Server.PrintToChatAll($"{ChatColors.Yellow}⏳ Type !ready to start the game{ChatColors.Default}");
            
            // Respawn all players for warmup phase
            Server.NextFrame(() =>
            {
                RespawnAllPlayers();
                
                // Restart HUD timer for warmup messages
                _LockpointTimer?.Start();
            });
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
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - Ready mode set to: {ChatColors.Yellow}One player per team{ChatColors.Default}");
            }
            else if (mode == "all")
            {
                _requireTeamReady = false;
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - Ready mode set to: {ChatColors.Yellow}All players{ChatColors.Default}");
            }
            else
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Usage: css_readyconfig <team|all>{ChatColors.Default}");
            }
        }

        [ConsoleCommand("css_kill", "Kill yourself or another player (admin).")]
        [CommandHelper(minArgs: 0, usage: "[player_name]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnCommandKill(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var targetName = commandInfo.GetArg(1);
            
            // If no target specified, kill yourself (client only)
            if (string.IsNullOrEmpty(targetName))
            {
                if (player?.IsValid != true)
                {
                    commandInfo.ReplyToCommand("Cannot kill yourself from server console. Specify a player name.");
                    return;
                }

                if (!player.PawnIsAlive)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}You are already dead!{ChatColors.Default}");
                    return;
                }

                try
                {
                    player.PlayerPawn?.Value?.CommitSuicide(false, true);
                    Server.PrintToChatAll($"{ChatColors.Yellow}{player.PlayerName}{ChatColors.Default} committed suicide");
                    Server.PrintToConsole($"[Lockpoint] {player.PlayerName} killed themselves");
                }
                catch (Exception ex)
                {
                    commandInfo.ReplyToCommand($"{ChatColors.Red}Error killing yourself: {ex.Message}{ChatColors.Default}");
                    Server.PrintToConsole($"[Lockpoint] Error in self-kill for {player.PlayerName}: {ex.Message}");
                }
                return;
            }

            // If target specified, admin kill (requires permissions)
            if (player?.IsValid == true && !AdminManager.PlayerHasPermissions(player, "@css/slay"))
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You don't have permission to kill other players!{ChatColors.Default}");
                return;
            }

            // Find the target player
            var targetPlayer = FindPlayerByName(targetName);
            
            if (targetPlayer == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Player '{targetName}' not found!{ChatColors.Default}");
                return;
            }

            if (!targetPlayer.PawnIsAlive)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}{targetPlayer.PlayerName} is already dead!{ChatColors.Default}");
                return;
            }

            try
            {
                targetPlayer.PlayerPawn?.Value?.CommitSuicide(false, true);
                
                var killerName = player?.IsValid == true ? player.PlayerName : "Server";
                Server.PrintToChatAll($"{ChatColors.Red}{targetPlayer.PlayerName}{ChatColors.Default} was killed by {ChatColors.Yellow}{killerName}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] {killerName} killed {targetPlayer.PlayerName}");
                
                commandInfo.ReplyToCommand($"{ChatColors.Green}Killed {targetPlayer.PlayerName}!{ChatColors.Default}");
            }
            catch (Exception ex)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}Error killing {targetPlayer.PlayerName}: {ex.Message}{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Error killing {targetPlayer.PlayerName}: {ex.Message}");
            }
        }

        private CCSPlayerController? FindPlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var players = Utilities.GetPlayers()
                .Where(p => p?.IsValid == true && 
                        p.Connected == PlayerConnectedState.PlayerConnected && 
                        !p.IsBot)
                .ToList();

            // First try exact match
            var exactMatch = players.FirstOrDefault(p => 
                string.Equals(p.PlayerName, name, StringComparison.OrdinalIgnoreCase));
            
            if (exactMatch != null)
                return exactMatch;

            // Then try partial match
            var partialMatch = players.FirstOrDefault(p => 
                p.PlayerName.Contains(name, StringComparison.OrdinalIgnoreCase));
            
            if (partialMatch != null)
                return partialMatch;

            // Try by user ID if it's a number
            if (int.TryParse(name, out int userId))
            {
                var userIdMatch = players.FirstOrDefault(p => p.UserId == userId);
                if (userIdMatch != null)
                    return userIdMatch;
            }

            return null;
        }

        [ConsoleCommand("css_addtspawn", "Add a terrorist spawn point to the active zone (Edit mode only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddTSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to add spawn points! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var closestZone = FindClosestZone(player);
            if (closestZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone found nearby. Stand closer to a zone.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            closestZone.TerroristSpawns.Add(playerPos);
            
            // Save zones
            var currentMapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(currentMapName, _zoneManager.Zones);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Added terrorist spawn to zone '{closestZone.Name}' ({closestZone.TerroristSpawns.Count} T spawns total){ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Added T spawn to zone '{closestZone.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_addctspawn", "Add a counter-terrorist spawn point to the active zone (Edit mode only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandAddCTSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to add spawn points! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true || player.PlayerPawn?.Value == null)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var closestZone = FindClosestZone(player);
            if (closestZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone found nearby. Stand closer to a zone.{ChatColors.Default}");
                return;
            }

            var playerPos = new CSVector(
                player.PlayerPawn.Value.AbsOrigin!.X,
                player.PlayerPawn.Value.AbsOrigin!.Y,
                player.PlayerPawn.Value.AbsOrigin!.Z
            );

            closestZone.CounterTerroristSpawns.Add(playerPos);
            
            // Save zones
            var currentMapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(currentMapName, _zoneManager.Zones);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Added CT spawn to zone '{closestZone.Name}' ({closestZone.CounterTerroristSpawns.Count} CT spawns total){ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Added CT spawn to zone '{closestZone.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_clearspawns", "Clear all spawn points from the closest zone (Edit mode only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandClearSpawns(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (!_editMode)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}You must be in edit mode to clear spawn points! Use !edit{ChatColors.Default}");
                return;
            }

            if (player?.IsValid != true)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            var closestZone = FindClosestZone(player);
            if (closestZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No zone found nearby. Stand closer to a zone.{ChatColors.Default}");
                return;
            }

            var tSpawnCount = closestZone.TerroristSpawns.Count;
            var ctSpawnCount = closestZone.CounterTerroristSpawns.Count;

            closestZone.TerroristSpawns.Clear();
            closestZone.CounterTerroristSpawns.Clear();
            
            // Save zones
            var currentMapName = Server.MapName;
            _zoneManager?.SaveZonesForMap(currentMapName, _zoneManager.Zones);

            commandInfo.ReplyToCommand($"{ChatColors.Green}Cleared all spawn points from zone '{closestZone.Name}' ({tSpawnCount} T + {ctSpawnCount} CT spawns removed){ChatColors.Default}");
            Server.PrintToConsole($"[Lockpoint] Cleared spawns from zone '{closestZone.Name}' by {player.PlayerName}");
        }

        [ConsoleCommand("css_testspawn", "Test spawn points for current zone (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandTestSpawn(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (player?.IsValid != true)
            {
                commandInfo.ReplyToCommand("Command must be used by a valid player.");
                return;
            }

            if (activeZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No active zone to test spawns for.{ChatColors.Default}");
                return;
            }

            var teamNum = player.TeamNum;
            var teamSpawns = teamNum == (byte)CsTeam.CounterTerrorist 
                ? activeZone.CounterTerroristSpawns 
                : activeZone.TerroristSpawns;

            if (teamSpawns.Count == 0)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No spawn points defined for your team in zone '{activeZone.Name}'.{ChatColors.Default}");
                return;
            }

            var availableSpawns = FindAvailableSpawnPoints(teamSpawns);
            
            commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{activeZone.Name}' - Team spawns: {teamSpawns.Count}, Available: {availableSpawns.Count}{ChatColors.Default}");
            
            if (availableSpawns.Count > 0)
            {
                // Teleport to a random available spawn for testing
                var random = new Random();
                var testSpawn = availableSpawns[random.Next(availableSpawns.Count)];
                var spawnVector = new Vector(testSpawn.X, testSpawn.Y, testSpawn.Z);
                
                player.PlayerPawn?.Value?.Teleport(spawnVector, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                commandInfo.ReplyToCommand($"{ChatColors.Blue}Teleported to available spawn point.{ChatColors.Default}");
            }
            else
            {
                commandInfo.ReplyToCommand($"{ChatColors.Yellow}All spawn points are occupied, would use default spawns.{ChatColors.Default}");
            }
        }

        [ConsoleCommand("css_spawninfo", "Show spawn point information for current zone (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandSpawnInfo(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (activeZone == null)
            {
                commandInfo.ReplyToCommand($"{ChatColors.Red}No active zone.{ChatColors.Default}");
                return;
            }

            var ctSpawns = activeZone.CounterTerroristSpawns.Count;
            var tSpawns = activeZone.TerroristSpawns.Count;
            
            var availableCT = FindAvailableSpawnPoints(activeZone.CounterTerroristSpawns).Count;
            var availableT = FindAvailableSpawnPoints(activeZone.TerroristSpawns).Count;
            
            commandInfo.ReplyToCommand($"{ChatColors.Green}Zone '{activeZone.Name}' Spawns:{ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Blue}CT: {ctSpawns} total, {availableCT} available{ChatColors.Default}");
            commandInfo.ReplyToCommand($"{ChatColors.Red}T: {tSpawns} total, {availableT} available{ChatColors.Default}");
        }

        [ConsoleCommand("css_edit", "Enter/exit edit mode (Admin only).")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/root")]
        public void OnCommandEdit(CCSPlayerController? player, CommandInfo commandInfo)
        {
            if (_editMode)
            {
                // Exit edit mode
                ExitEditMode(player);
            }
            else
            {
                // Enter edit mode
                EnterEditMode(player);
            }
        }

        private void EnterEditMode(CCSPlayerController? player)
        {
            try
            {
                // Store previous state
                _previousGamePhase = _gamePhase;
                _editMode = true;
                _gamePhase = GamePhase.EditMode;
                
                // Stop all game timers
                _zoneCheckTimer?.Stop();
                _LockpointTimer?.Stop();
                
                // Clear all respawn timers
                CleanupAllRespawnTimers();
                
                // Enable cheats
                Server.ExecuteCommand("sv_cheats 1");
                _previousCheatsEnabled = false; // Assume cheats were off before
                
                var editorName = player?.IsValid == true ? player.PlayerName : "Server";
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}🔧 EDIT MODE ACTIVATED by {editorName}!{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.LightBlue}Use noclip to fly around and create/edit zones{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Red}Game is paused during edit mode{ChatColors.Default}");
                
                // Respawn all players
                Server.NextFrame(() =>
                {
                    RespawnAllPlayersForEdit();
                    
                    // Draw all zones for visibility
                    AddTimer(1.0f, () =>
                    {
                        DrawAllZonesForEdit();
                        _LockpointTimer?.Start(); // Start timer for HUD updates only
                    });
                });
                
                Server.PrintToConsole($"[Lockpoint] Edit mode activated by {editorName}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error entering edit mode: {ex.Message}");
            }
        }

        private void ExitEditMode(CCSPlayerController? player)
        {
            try
            {
                _editMode = false;
                _gamePhase = _previousGamePhase;
                
                // Disable cheats (restore previous state)
                if (!_previousCheatsEnabled)
                {
                    Server.ExecuteCommand("sv_cheats 0");
                }
                
                var editorName = player?.IsValid == true ? player.PlayerName : "Server";
                Server.PrintToChatAll($"{ChatColors.Green}[Lockpoint]{ChatColors.Default} - {ChatColors.Yellow}🔧 EDIT MODE DEACTIVATED by {editorName}!{ChatColors.Default}");
                Server.PrintToChatAll($"{ChatColors.Green}Game resumed{ChatColors.Default}");
                
                // Clear all zone visualizations
                _zoneVisualization?.ClearZoneVisualization();
                
                // Respawn all players back to normal mode
                Server.NextFrame(() =>
                {
                    RespawnAllPlayers();
                    
                    // Restart game timers based on previous phase
                    AddTimer(1.0f, () =>
                    {
                        if (_gamePhase == GamePhase.Active)
                        {
                            DrawRandomZone();
                            _zoneCheckTimer?.Start();
                        }
                        _LockpointTimer?.Start();
                    });
                });
                
                Server.PrintToConsole($"[Lockpoint] Edit mode deactivated by {editorName}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error exiting edit mode: {ex.Message}");
            }
        }

        private void RespawnAllPlayersForEdit()
        {
            Server.PrintToConsole("[Lockpoint] Respawning all players for edit mode...");
            
            foreach (var player in Utilities.GetPlayers())
            {
                if (player?.IsValid == true && 
                    player.Connected == PlayerConnectedState.PlayerConnected && 
                    !player.IsBot &&
                    player.TeamNum != (byte)CsTeam.None &&
                    player.TeamNum != (byte)CsTeam.Spectator)
                {
                    try
                    {
                        // Force respawn everyone
                        player.Respawn();
                        
                        // Give noclip after respawn
                        AddTimer(0.3f, () =>
                        {
                            if (player?.IsValid == true && player.PawnIsAlive)
                            {
                                Server.ExecuteCommand($"noclip {player.UserId}");
                                player.PrintToChat($"{ChatColors.Green}Edit mode: Use noclip to fly around!{ChatColors.Default}");
                            }
                        });
                        
                        Server.PrintToConsole($"[Lockpoint] Respawned player for edit: {player.PlayerName}");
                    }
                    catch (Exception ex)
                    {
                        Server.PrintToConsole($"[Lockpoint] Error respawning player for edit {player.PlayerName}: {ex.Message}");
                    }
                }
            }
        }

        private void DrawAllZonesForEdit()
        {
            if (_zoneManager?.Zones == null || _zoneManager.Zones.Count == 0)
            {
                Server.PrintToChatAll($"{ChatColors.Yellow}No zones to display{ChatColors.Default}");
                return;
            }

            try
            {
                // Clear existing visualizations first
                _zoneVisualization?.ClearZoneVisualization();
                
                // Draw all zones
                foreach (var zone in _zoneManager.Zones)
                {
                    _zoneVisualization?.DrawZone(zone);
                }
                
                Server.PrintToChatAll($"{ChatColors.Green}Displaying all {_zoneManager.Zones.Count} zones for editing{ChatColors.Default}");
                Server.PrintToConsole($"[Lockpoint] Drew all {_zoneManager.Zones.Count} zones for edit mode");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error drawing zones for edit mode: {ex.Message}");
            }
        }

        #endregion
    }
}