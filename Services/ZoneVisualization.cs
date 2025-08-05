using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Lockpoint.Models;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Lockpoint.Services
{
    public class ZoneVisualization
    {
        private Dictionary<Zone, List<CBeam>> zoneBeams = new();
        private readonly List<CBeam> beams = new();
        private readonly List<CBeam> spawnBeams = new();
        private Zone? _currentSpawnZone = null;

        public void DrawZone(Zone zone)
        {
            if (zone.Points == null || zone.Points.Count < 3) 
            {
                Server.PrintToConsole($"[ZoneVisualization] Cannot draw zone {zone.Name}: insufficient points ({zone.Points?.Count ?? 0})");
                return;
            }

            Server.PrintToConsole($"[ZoneVisualization] Drawing zone {zone.Name} with {zone.Points.Count} points");

            // Safely clear existing beams for this zone
            try
            {
                ClearZoneBeams(zone);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneVisualization] Error clearing zone beams: {ex.Message}");
            }

            var beams = new List<CBeam>();
            
            try
            {
                CreateGroundOverlay(zone, beams);
                CreateZoneBorders(zone, beams);
                
                zoneBeams[zone] = beams;
                
                Server.PrintToConsole($"[ZoneVisualization] Created {beams.Count} beams for zone {zone.Name}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneVisualization] Error creating beams for zone {zone.Name}: {ex.Message}");
            }
        }

        public void UpdateZoneColor(Zone zone)
        {
            // Add a small delay to avoid conflicts with entity cleanup
            Server.NextFrame(() =>
            {
                try
                {
                    DrawZone(zone);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[ZoneVisualization] Error updating zone color: {ex.Message}");
                }
            });
        }

        private Color GetZoneColor(ZoneState state)
        {
            return state switch
            {
                ZoneState.CTControlled => Color.FromArgb(180, Color.Blue),
                ZoneState.TControlled => Color.FromArgb(180, Color.Red),
                ZoneState.Contested => Color.FromArgb(180, Color.Purple),
                ZoneState.Neutral => Color.FromArgb(180, Color.Green),
                _ => Color.FromArgb(180, Color.Green)
            };
        }

        private void CreateGroundOverlay(Zone zone, List<CBeam> beams)
        {
            var minX = zone.Points.Min(p => p.X);
            var maxX = zone.Points.Max(p => p.X);
            var minY = zone.Points.Min(p => p.Y);
            var maxY = zone.Points.Max(p => p.Y);
            var avgZ = zone.Points.Average(p => p.Z);

            var gridSize = 32.0f;
            var color = GetZoneColor(zone.GetZoneState());
            
            for (float x = minX; x <= maxX; x += gridSize)
            {
                for (float y = minY; y <= maxY; y += gridSize)
                {
                    var testPoint = new CSVector(x, y, avgZ);
                    
                    if (zone.IsPlayerInZone(testPoint))
                    {
                        var beam = CreateGroundBeam(testPoint, color);
                        if (beam != null) beams.Add(beam);
                    }
                }
            }
        }

        private void CreateZoneBorders(Zone zone, List<CBeam> beams)
        {
            var color = GetZoneColor(zone.GetZoneState());
            
            for (int i = 0; i < zone.Points.Count; i++)
            {
                var startPoint = zone.Points[i];
                var endPoint = zone.Points[(i + 1) % zone.Points.Count];

                var beam = CreateBorderBeam(startPoint, endPoint, color);
                if (beam != null) beams.Add(beam);
            }
        }

        private CBeam? CreateGroundBeam(CSVector position, Color color)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return null;

            beam.StartFrame = 0;
            beam.FrameRate = 0;
            beam.LifeState = 1;
            beam.Width = 15;
            beam.EndWidth = 15;
            beam.Amplitude = 0;
            beam.Speed = 0;
            beam.Flags = 0;
            beam.BeamType = BeamType_t.BEAM_POINTS;
            beam.FadeLength = 0;

            beam.Render = color;

            beam.EndPos.X = position.X;
            beam.EndPos.Y = position.Y;
            beam.EndPos.Z = position.Z + 2.0f;

            beam.Teleport(new CSVector(position.X, position.Y, position.Z - 1.0f), 
                         new QAngle(0, 0, 0), 
                         new CSVector(0, 0, 0));
            
            beam.DispatchSpawn();
            return beam;
        }

        private CBeam? CreateBorderBeam(CSVector startPos, CSVector endPos, Color color)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return null;

            beam.StartFrame = 0;
            beam.FrameRate = 0;
            beam.LifeState = 1;
            beam.Width = 5;
            beam.EndWidth = 5;
            beam.Amplitude = 0;
            beam.Speed = 50;
            beam.Flags = 0;
            beam.BeamType = BeamType_t.BEAM_POINTS;
            beam.FadeLength = 10.0f;

            beam.Render = color;

            beam.EndPos.X = endPos.X;
            beam.EndPos.Y = endPos.Y;
            beam.EndPos.Z = endPos.Z + 20.0f;

            beam.Teleport(new CSVector(startPos.X, startPos.Y, startPos.Z + 20.0f), 
                         new QAngle(0, 0, 0), 
                         new CSVector(0, 0, 0));
            
            beam.DispatchSpawn();
            return beam;
        }

        public void ClearZoneBeams(Zone zone)
        {
            if (zoneBeams.ContainsKey(zone))
            {
                foreach (var beam in zoneBeams[zone])
                {
                    try
                    {
                        if (beam?.IsValid == true)
                        {
                            beam.Remove();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Entity might already be removed, just log and continue
                        Server.PrintToConsole($"[ZoneVisualization] Failed to remove beam: {ex.Message}");
                    }
                }
                zoneBeams[zone].Clear();
            }
        }

        public void DrawSpawnPoints(Zone zone)
        {
            if (zone == null) return;

            // Clear existing spawn visualizations first
            ClearSpawnPoints();
            
            _currentSpawnZone = zone;

            // Draw CT spawns (blue crosses)
            foreach (var spawn in zone.CounterTerroristSpawns)
            {
                DrawSpawnMarker(spawn, Color.Blue);
            }

            // Draw T spawns (red crosses)
            foreach (var spawn in zone.TerroristSpawns)
            {
                DrawSpawnMarker(spawn, Color.Red);
            }

            Server.PrintToConsole($"[ZoneVisualization] Drew {zone.CounterTerroristSpawns.Count} CT and {zone.TerroristSpawns.Count} T spawn points for zone '{zone.Name}'");
        }

        public void ClearSpawnPoints(Zone? specificZone = null)
        {
            // If specific zone provided, only clear if it matches current
            if (specificZone != null && _currentSpawnZone != specificZone)
                return;

            try
            {
                foreach (var beam in spawnBeams)
                {
                    if (beam?.IsValid == true)
                    {
                        beam.Remove();
                    }
                }
                spawnBeams.Clear();
                _currentSpawnZone = null;
                
                Server.PrintToConsole("[ZoneVisualization] Cleared spawn point visualizations");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneVisualization] Error clearing spawn beams: {ex.Message}");
            }
        }

        private void DrawSpawnMarker(CSVector position, Color color)
        {
            // Create a cross marker on the ground
            var size = 15.0f;
            
            // Horizontal line
            var start1 = new Vector(position.X - size, position.Y, position.Z + 1);
            var end1 = new Vector(position.X + size, position.Y, position.Z + 1);
            
            // Vertical line  
            var start2 = new Vector(position.X, position.Y - size, position.Z + 1);
            var end2 = new Vector(position.X, position.Y + size, position.Z + 1);
            
            // Vertical marker
            var verticalStart = new Vector(position.X, position.Y, position.Z);
            var verticalEnd = new Vector(position.X, position.Y, position.Z + 30);
            
            // Draw the cross
            DrawSpawnBeam(start1, end1, color);
            DrawSpawnBeam(start2, end2, color);
            DrawSpawnBeam(verticalStart, verticalEnd, color);
        }

        private void DrawSpawnBeam(Vector start, Vector end, Color color)
        {
            try
            {
                var beam = Utilities.CreateEntityByName<CBeam>("beam");
                if (beam == null) return;

                beam.StartFrame = 0;
                beam.FrameRate = 0;
                beam.LifeState = 1;
                beam.Width = 3;
                beam.EndWidth = 3;
                beam.Amplitude = 0;
                beam.Speed = 0;
                beam.Flags = 0;
                beam.BeamType = BeamType_t.BEAM_POINTS;
                beam.FadeLength = 0;
                beam.Render = color;

                // Use the same pattern as your existing CreateBorderBeam method
                beam.EndPos.X = end.X;
                beam.EndPos.Y = end.Y;
                beam.EndPos.Z = end.Z;

                beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                
                beam.DispatchSpawn();
                spawnBeams.Add(beam);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneVisualization] Error creating spawn beam: {ex.Message}");
            }
        }

        public void ClearZoneVisualization()
        {
            // Clear all zone beams
            foreach (var beamList in zoneBeams.Values)
            {
                foreach (var beam in beamList)
                {
                    if (beam?.IsValid == true)
                    {
                        beam.Remove();
                    }
                }
            }
            zoneBeams.Clear();

            // Also clear spawn points
            ClearSpawnPoints();
            
            Server.PrintToConsole("[ZoneVisualization] Cleared all zone visualizations");
        }
    }
}