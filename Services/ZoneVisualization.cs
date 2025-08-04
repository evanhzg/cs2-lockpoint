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
        private List<CBeam> activeBeams = new();
        private List<CPointClientUIWorldPanel> activeSprites = new();

        public void DrawZone(Zone zone)
        {
            if (zone.Points == null || zone.Points.Count < 3) return;

            CreateGroundOverlay(zone);
            CreateZoneBorders(zone); // Keep borders for clarity
        }

        private void CreateGroundOverlay(Zone zone)
        {
            // Calculate the bounding box of the zone
            var minX = zone.Points.Min(p => p.X);
            var maxX = zone.Points.Max(p => p.X);
            var minY = zone.Points.Min(p => p.Y);
            var maxY = zone.Points.Max(p => p.Y);
            var avgZ = zone.Points.Average(p => p.Z);

            // Create a grid of small beams to simulate ground fill
            var gridSize = 32.0f; // Distance between fill elements
            
            for (float x = minX; x <= maxX; x += gridSize)
            {
                for (float y = minY; y <= maxY; y += gridSize)
                {
                    var testPoint = new CSVector(x, y, avgZ);
                    
                    // Only place fill if point is inside the zone polygon
                    if (zone.IsPlayerInZone(testPoint))
                    {
                        CreateGroundBeam(testPoint);
                    }
                }
            }
        }

        private void CreateGroundBeam(CSVector position)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return;

            beam.StartFrame = 0;
            beam.FrameRate = 0;
            beam.LifeState = 1;
            beam.Width = 15; // Wider beam for ground fill
            beam.EndWidth = 15;
            beam.Amplitude = 0;
            beam.Speed = 0;
            beam.Flags = 0;
            beam.BeamType = BeamType_t.BEAM_POINTS;
            beam.FadeLength = 0;

            // Semi-transparent green for ground fill
            beam.Render = System.Drawing.Color.FromArgb(80, System.Drawing.Color.LimeGreen);

            // Create a very short vertical beam (ground to slightly above)
            beam.EndPos.X = position.X;
            beam.EndPos.Y = position.Y;
            beam.EndPos.Z = position.Z + 2.0f; // Very short beam

            beam.Teleport(new CSVector(position.X, position.Y, position.Z - 1.0f), 
                         new QAngle(0, 0, 0), 
                         new CSVector(0, 0, 0));
            
            beam.DispatchSpawn();
            activeBeams.Add(beam);
        }

        private void CreateZoneBorders(Zone zone)
        {
            // Create border beams for zone outline
            for (int i = 0; i < zone.Points.Count; i++)
            {
                var startPoint = zone.Points[i];
                var endPoint = zone.Points[(i + 1) % zone.Points.Count];

                CreateBorderBeam(startPoint, endPoint);
            }
        }

        private void CreateBorderBeam(CSVector startPos, CSVector endPos)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return;

            beam.StartFrame = 0;
            beam.FrameRate = 0;
            beam.LifeState = 1;
            beam.Width = 5; // Thinner for borders
            beam.EndWidth = 5;
            beam.Amplitude = 0;
            beam.Speed = 50;
            beam.Flags = 0;
            beam.BeamType = BeamType_t.BEAM_POINTS;
            beam.FadeLength = 10.0f;

            // Bright green for borders
            beam.Render = System.Drawing.Color.FromArgb(255, System.Drawing.Color.Green);

            beam.EndPos.X = endPos.X;
            beam.EndPos.Y = endPos.Y;
            beam.EndPos.Z = endPos.Z + 20.0f; // Taller border beam

            beam.Teleport(new CSVector(startPos.X, startPos.Y, startPos.Z + 20.0f), 
                         new QAngle(0, 0, 0), 
                         new CSVector(0, 0, 0));
            
            beam.DispatchSpawn();
            activeBeams.Add(beam);
        }

        public void ClearZoneVisualization()
        {
            foreach (var beam in activeBeams)
            {
                beam?.Remove();
            }
            activeBeams.Clear();

            foreach (var sprite in activeSprites)
            {
                sprite?.Remove();
            }
            activeSprites.Clear();
        }
    }
}