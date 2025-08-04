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

        public override void Load(bool hotReload)
        {
            Logger.LogInformation("HardpointCS2 plugin loaded");
            _zoneVisualization = new ZoneVisualization();
            _zoneManager = new ZoneManager(ModuleDirectory);

            var mapName = Server.MapName;
            Logger.LogInformation($"Loading zones for map: {mapName}");
            
            _zoneManager.LoadZonesForMap(mapName);

            // Visualize loaded zones
            foreach (var zone in _zoneManager.Zones)
            {
                _zoneVisualization.DrawZone(zone);
                Logger.LogInformation($"Visualizing zone: {zone.Name}");
            }

            _zoneCheckTimer = new System.Timers.Timer(500);
            _zoneCheckTimer.Elapsed += CheckPlayerZones;
            _zoneCheckTimer.Start();

            Logger.LogInformation($"Loaded {_zoneManager.Zones.Count} zones for map {mapName}");
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
                foreach (var zone in _zoneManager?.Zones ?? new List<Zone>())
                {
                    var previousState = zone.GetZoneState();
                    zone.PlayersInZone.Clear();

                    foreach (var player in Utilities.GetPlayers())
                    {
                        if (player?.IsValid == true && player.PlayerPawn?.Value != null && player.PawnIsAlive)
                        {
                            var playerPos = new CSVector(
                                player.PlayerPawn.Value.AbsOrigin!.X,
                                player.PlayerPawn.Value.AbsOrigin!.Y,
                                player.PlayerPawn.Value.AbsOrigin!.Z
                            );

                            if (zone.IsPlayerInZone(playerPos))
                            {
                                zone.PlayersInZone.Add(player);
                            }
                        }
                    }

                    if (zone.GetZoneState() != previousState)
                    {
                        _zoneVisualization?.UpdateZoneColor(zone);
                    }
                }
            });
        }

    #region Commands

    // Add command to manually save zones
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

        [ConsoleCommand("css_debugzones", "Debug zone system.")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        [RequiresPermissions("@css/root")]
        public void OnCommandDebugZones(CCSPlayerController? player, CommandInfo commandInfo)
        {
            var mapName = Server.MapName;
            var zonesPath = _zoneManager?.GetZonesDirectoryPath();
            
            // Get the assembly location for debugging
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
            
            commandInfo.ReplyToCommand($"Map: {mapName}");
            commandInfo.ReplyToCommand($"Assembly location: {assemblyLocation}");
            commandInfo.ReplyToCommand($"Assembly directory: {assemblyDirectory}");
            commandInfo.ReplyToCommand($"Zones directory: {zonesPath}");
            commandInfo.ReplyToCommand($"Zones in memory: {_zoneManager?.Zones.Count ?? 0}");
            
            // Check if directory exists
            if (!string.IsNullOrEmpty(zonesPath))
            {
                commandInfo.ReplyToCommand($"Directory exists: {Directory.Exists(zonesPath)}");
                
                // Check if file exists
                var filePath = Path.Combine(zonesPath, $"{mapName}.json");
                commandInfo.ReplyToCommand($"Full file path: {filePath}");
                commandInfo.ReplyToCommand($"Zone file exists: {File.Exists(filePath)}");
                
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    commandInfo.ReplyToCommand($"File size: {fileInfo.Length} bytes");
                    commandInfo.ReplyToCommand($"File created: {fileInfo.CreationTime}");
                    commandInfo.ReplyToCommand($"File modified: {fileInfo.LastWriteTime}");
                }
                
                // List all files in zones directory
                if (Directory.Exists(zonesPath))
                {
                    var files = Directory.GetFiles(zonesPath, "*.json");
                    commandInfo.ReplyToCommand($"JSON files in zones dir: {files.Length}");
                    foreach (var file in files)
                    {
                        commandInfo.ReplyToCommand($"- {Path.GetFileName(file)}");
                    }
                }
            }
            
            // Also print to server console for easier copying
            Server.PrintToConsole($"[HardpointCS2] FULL DEBUG PATH: {zonesPath}");
            Server.PrintToConsole($"[HardpointCS2] ASSEMBLY PATH: {assemblyLocation}");
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
        #endregion
    }
}