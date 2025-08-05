using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API; 
using Microsoft.Extensions.Logging;
using HardpointCS2.Services;
using HardpointCS2.Models;
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

        public override void Load(bool hotReload)
        {
            Logger.LogInformation("HardpointCS2 plugin loaded");
            _zoneVisualization = new ZoneVisualization();
            _zoneManager = new ZoneManager(ModuleDirectory);

            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            _zoneCheckTimer = new System.Timers.Timer(500);
            _zoneCheckTimer.Elapsed += CheckPlayerZones;
            _zoneCheckTimer.Start();
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
            _zoneCheckTimer?.Stop();
            
            AddTimer(3.0f, () =>
            {
                try
                {
                    Server.PrintToConsole($"[HardpointCS2] Round started - cleaning up and redrawing zones");
                    
                    // Clear all existing visualizations completely
                    try
                    {
                        _zoneVisualization?.ClearZoneVisualization();
                    }
                    catch (Exception ex)
                    {
                        Server.PrintToConsole($"[HardpointCS2] Error clearing visualizations: {ex.Message}");
                    }
                    
                    // Clear players from all zones
                    foreach (var zone in _zoneManager?.Zones ?? new List<Zone>())
                    {
                        zone.PlayersInZone.Clear();
                    }
                    
                    // Wait a bit more before redrawing
                    Server.NextFrame(() =>
                    {
                        DrawRandomZone();
                        
                        // Restart zone checking
                        _zoneCheckTimer?.Start();
                    });
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[HardpointCS2] Error in OnRoundStart: {ex.Message}");
                    // Make sure to restart the timer even if there's an error
                    _zoneCheckTimer?.Start();
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
            if (_zoneManager?.Zones.Count > 0)
            {
                // Clear all existing visualizations first
                _zoneVisualization?.ClearZoneVisualization();
                
                var random = new Random();
                var randomZone = _zoneManager.Zones[random.Next(_zoneManager.Zones.Count)];
                
                // Only draw the selected zone
                _zoneVisualization?.DrawZone(randomZone);
                Server.PrintToConsole($"[HardpointCS2] Drew random zone: {randomZone.Name}");
                Server.PrintToChatAll($"[HardpointCS2] Active Zone: {randomZone.Name}");
                activeZone = randomZone;
            }
            else
            {
                Server.PrintToConsole("[HardpointCS2] No zones available to draw");
                activeZone = null;
            }
        }

        public override void Unload(bool hotReload)
        {
            _zoneCheckTimer?.Stop();
            _zoneCheckTimer?.Dispose();
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
                        
                        // Clear and rebuild the players list with only valid players
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
                                }
                            }
                        }

                        // Only update zone color if state changed
                        var currentState = activeZone.GetZoneState();
                        if (currentState != previousState)
                        {
                            try
                            {
                                _zoneVisualization?.UpdateZoneColor(activeZone);
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
        #endregion
    }
}