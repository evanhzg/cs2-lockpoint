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
        private bool _isEditMode = false;
        private Zone? _editingZone = null;
        private readonly Dictionary<Zone, ZoneState> _lastZoneStates = new();

        public void SetEditMode(bool editMode, Zone? editingZone = null)
        {
            _isEditMode = editMode;
            _editingZone = editingZone;

            // Redraw the zone with appropriate color if there's an editing zone
            if (_editingZone != null)
            {
                DrawZone(_editingZone);
            }
        }

        public void DrawZone(Zone zone)
        {
            if (zone?.Points == null || zone.Points.Count < 3)
            {
                Server.PrintToConsole("[ZoneVisualization] Cannot draw zone - insufficient points");
                return;
            }

            ClearZoneBeams(zone);

            var beams = new List<CBeam>();

            // Start with default green color
            Color zoneColor = Color.FromArgb(255, 0, 255, 0); // Green default

            // Override to yellow if in edit mode
            if (_isEditMode && _editingZone == zone)
            {
                zoneColor = Color.FromArgb(255, 255, 255, 0); // Bright yellow for editing
                Server.PrintToConsole($"[ZoneVisualization] Drawing zone '{zone.Name}' in EDIT MODE (yellow)");
            }
            else
            {
                Server.PrintToConsole($"[ZoneVisualization] Drawing zone '{zone.Name}' in normal mode (green)");
            }

            // Draw lines connecting all points in sequence, including closing the loop
            for (int i = 0; i < zone.Points.Count; i++)
            {
                var currentPoint = zone.Points[i];
                var nextPoint = zone.Points[(i + 1) % zone.Points.Count]; // Wrap around to first point

                var beam = CreateBorderBeam(currentPoint, nextPoint, zoneColor);
                if (beam != null)
                {
                    beams.Add(beam);
                    Server.PrintToConsole($"[ZoneVisualization] Created beam from ({currentPoint.X:F1}, {currentPoint.Y:F1}, {currentPoint.Z:F1}) to ({nextPoint.X:F1}, {nextPoint.Y:F1}, {nextPoint.Z:F1})");
                }
                else
                {
                    Server.PrintToConsole($"[ZoneVisualization] Failed to create beam {i}");
                }
            }

            if (beams.Count > 0)
            {
                zoneBeams[zone] = beams;
                Server.PrintToConsole($"[ZoneVisualization] Drew zone '{zone.Name}' with {beams.Count} border segments");
            }
            else
            {
                Server.PrintToConsole($"[ZoneVisualization] No beams created for zone '{zone.Name}'");
            }
        }

        private Color GetColorForState(ZoneState state)
        {
            return state switch
            {
                ZoneState.CTControlled => Color.FromArgb(255, 0, 100, 255), // Blue for CT
                ZoneState.TControlled => Color.FromArgb(255, 255, 0, 0),    // Red for T
                ZoneState.Contested => Color.FromArgb(255, 128, 0, 128),    // Purple for contested
                ZoneState.Neutral => Color.FromArgb(255, 0, 255, 0),        // Green for neutral
                _ => Color.FromArgb(255, 255, 255, 255)                     // White for unknown
            };
        }

        // Also add the missing DrawZoneBeams method that's called in UpdateZoneColor:
        private void DrawZoneBeams(Zone zone, Color color)
        {
            if (zone?.Points == null || zone.Points.Count < 3)
                return;

            var beams = new List<CBeam>();

            // Draw lines connecting all points in sequence, including closing the loop
            for (int i = 0; i < zone.Points.Count; i++)
            {
                var currentPoint = zone.Points[i];
                var nextPoint = zone.Points[(i + 1) % zone.Points.Count]; // Wrap around to first point

                var beam = CreateBorderBeam(currentPoint, nextPoint, color);
                if (beam != null)
                {
                    beams.Add(beam);
                }
            }

            if (beams.Count > 0)
            {
                zoneBeams[zone] = beams;
                Server.PrintToConsole($"[ZoneVisualization] Redrawn zone '{zone.Name}' with {beams.Count} beams in new color for state {GetStateText(zone)}");
            }
        }

        // And add this helper method for cleaner logging:
        private string GetStateText(Zone zone)
        {
            if (!_lastZoneStates.TryGetValue(zone, out var state))
                return "Unknown";
                
            return state switch
            {
                ZoneState.CTControlled => "CT Controlled",
                ZoneState.TControlled => "T Controlled", 
                ZoneState.Contested => "Contested",
                ZoneState.Neutral => "Neutral",
                _ => "Unknown"
            };
        }

        public void UpdateZoneColor(Zone zone, ZoneState state)
        {
            if (zone?.Points == null || zone.Points.Count < 3)
                return;

            // Only update and log if the state has actually changed
            if (_lastZoneStates.TryGetValue(zone, out var lastState) && lastState == state)
            {
                return; // No change, don't update or log
            }

            _lastZoneStates[zone] = state;

            try
            {
                var color = GetColorForState(state);
                var stateText = state switch
                {
                    ZoneState.CTControlled => "BLUE (CT controlling)",
                    ZoneState.TControlled => "RED (T controlling)", 
                    ZoneState.Contested => "PURPLE (contested)",
                    ZoneState.Neutral => "GREEN (neutral)",
                    _ => "UNKNOWN"
                };

                // Clear existing beams for this zone
                ClearZoneBeams(zone);

                // Draw new beams with the correct color
                DrawZoneBeams(zone, color);

                // ONLY log when state actually changes
                Server.PrintToConsole($"[ZoneVisualization] Zone '{zone.Name}' state changed to {stateText}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneVisualization] Error updating zone color: {ex.Message}");
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

        private CBeam? CreateBorderBeam(CSVector start, CSVector end, Color? customColor = null)
        {
            try
            {
                var beam = Utilities.CreateEntityByName<CBeam>("beam");
                if (beam == null) return null;

                // Beam configuration for visible border
                beam.StartFrame = 0;
                beam.FrameRate = 0;
                beam.LifeState = 1;
                beam.Width = 3.0f;           // Make it more visible
                beam.EndWidth = 3.0f;        // Consistent width
                beam.Amplitude = 0;          // No oscillation
                beam.Speed = 0;              // Static beam
                beam.Flags = 0;              // No special effects
                beam.BeamType = BeamType_t.BEAM_POINTS;
                beam.FadeLength = 0;         // No fade

                // Fix the default color - green for normal zones, yellow for edit mode
                var beamColor = customColor ?? Color.FromArgb(255, 0, 255, 0); // Green default
                beam.Render = beamColor;

                // Don't lower the beam too much - keep it more visible
                var lowerHeight = 1.0f; // Only lower by 2 units instead of 5

                // Set end position (slightly lowered)
                beam.EndPos.X = end.X;
                beam.EndPos.Y = end.Y;
                beam.EndPos.Z = end.Z - lowerHeight;

                // Set start position (slightly lowered) via teleport
                var startPos = new Vector(start.X, start.Y, start.Z - lowerHeight);
                beam.Teleport(startPos, new QAngle(0, 0, 0), new Vector(0, 0, 0));

                beam.DispatchSpawn();
                return beam;
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneVisualization] Error creating border beam: {ex.Message}");
                return null;
            }
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
                DrawSpawnMarker(spawn.Position, Color.Blue);
            }

            // Draw T spawns (red crosses)
            foreach (var spawn in zone.TerroristSpawns)
            {
                DrawSpawnMarker(spawn.Position, Color.Red);
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
            // Create a smaller, less intrusive cross marker
            var size = 8.0f; // Smaller cross

            // Horizontal line
            var start1 = new Vector(position.X - size, position.Y, position.Z + 2);
            var end1 = new Vector(position.X + size, position.Y, position.Z + 2);

            // Vertical line  
            var start2 = new Vector(position.X, position.Y - size, position.Z + 2);
            var end2 = new Vector(position.X, position.Y + size, position.Z + 2);

            // Shorter vertical marker
            var verticalStart = new Vector(position.X, position.Y, position.Z);
            var verticalEnd = new Vector(position.X, position.Y, position.Z + 15); // Shorter

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
                beam.Width = 2.0f;           // Thin spawn markers
                beam.EndWidth = 2.0f;
                beam.Amplitude = 0;
                beam.Speed = 0;
                beam.Flags = 0;
                beam.BeamType = BeamType_t.BEAM_POINTS;
                beam.FadeLength = 0;

                // Solid colors for spawn points
                beam.Render = Color.FromArgb(255, color.R, color.G, color.B);

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
            try
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
                
                // Clear state tracking
                _lastZoneStates.Clear();

                // Clear spawn point beams
                foreach (var spawnBeam in spawnBeams.ToList())
                {
                    spawnBeam?.Remove();
                }
                spawnBeams.Clear();

                // Clear current spawn zone reference
                _currentSpawnZone = null;

                Server.PrintToConsole("[ZoneVisualization] Cleared all zone visualizations and state tracking");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneVisualization] Error clearing visualization: {ex.Message}");
            }
        }

        public void ClearEditMode()
        {
            _isEditMode = false;
            _editingZone = null;
        }
    }
}