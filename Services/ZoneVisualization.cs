using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using HardpointCS2.Models;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace HardpointCS2.Services
{
    public class ZoneVisualization
    {
        private Dictionary<Zone, List<CBeam>> zoneBeams = new();

        public void DrawZone(Zone zone)
        {
            if (zone.Points == null || zone.Points.Count < 3) return;

            // Clear existing beams for this zone
            ClearZoneBeams(zone);

            // Create new beams with current color
            var beams = new List<CBeam>();
            CreateGroundOverlay(zone, beams);
            CreateZoneBorders(zone, beams);
            
            zoneBeams[zone] = beams;
        }

        public void UpdateZoneColor(Zone zone)
        {
            if (!zoneBeams.ContainsKey(zone)) return;

            var color = GetZoneColor(zone.GetZoneState());
            
            // Instead of just updating the color, recreate the zone visualization
            // This ensures the color change is properly applied
            Server.NextFrame(() =>
            {
                DrawZone(zone);
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

        private void ClearZoneBeams(Zone zone)
        {
            if (zoneBeams.ContainsKey(zone))
            {
                foreach (var beam in zoneBeams[zone])
                {
                    beam?.Remove();
                }
                zoneBeams[zone].Clear();
            }
        }

        public void ClearZoneVisualization()
        {
            foreach (var beamList in zoneBeams.Values)
            {
                foreach (var beam in beamList)
                {
                    beam?.Remove();
                }
            }
            zoneBeams.Clear();
        }
    }
}