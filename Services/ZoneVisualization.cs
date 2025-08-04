using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using HardpointCS2.Models;
using System.Collections.Generic;
using System.Drawing;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace HardpointCS2.Services
{
    public class ZoneVisualization
    {
        private List<CBeam> activeBeams = new();

        public void DrawZone(Zone zone)
        {
            if (zone.Points == null || zone.Points.Count < 3) return;

            for (int i = 0; i < zone.Points.Count; i++)
            {
                var startPoint = zone.Points[i];
                var endPoint = zone.Points[(i + 1) % zone.Points.Count];

                CreateZoneBeam(startPoint, endPoint);
            }
        }

        private void CreateZoneBeam(CSVector startPos, CSVector endPos)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return;

            beam.StartFrame = 0;
            beam.FrameRate = 0;
            beam.LifeState = 1;
            beam.Width = 3;
            beam.EndWidth = 3;
            beam.Amplitude = 0;
            beam.Speed = 50;
            beam.Flags = 0;
            beam.BeamType = BeamType_t.BEAM_POINTS;
            beam.FadeLength = 10.0f;

            beam.Render = System.Drawing.Color.FromArgb(255, System.Drawing.Color.Green);

            beam.EndPos.X = endPos.X;
            beam.EndPos.Y = endPos.Y;
            beam.EndPos.Z = endPos.Z + 5.0f;

            beam.Teleport(new CSVector(startPos.X, startPos.Y, startPos.Z + 5.0f), 
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
        }
    }
}