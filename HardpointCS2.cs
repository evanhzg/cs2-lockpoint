using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
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
        private readonly Dictionary<CCSPlayerController, Zone> _activeZones = new();
        private readonly List<Zone> _completedZones = new();

        public override void Load(bool hotReload)
        {
            Logger.LogInformation("HardpointCS2 plugin loaded");
            _zoneVisualization = new ZoneVisualization();
        }

        public override void Unload(bool hotReload)
        {
            _zoneVisualization?.ClearZoneVisualization();
            Logger.LogInformation("HardpointCS2 plugin unloaded");
        }

        #region Commands
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

            _completedZones.Add(zone);
            _zoneVisualization?.DrawZone(zone);

            _activeZones.Remove(player);

            commandInfo.ReplyToCommand($"Zone '{zone.Name}' completed with {zone.Points.Count} points and is now visible!");
        }
    }
}