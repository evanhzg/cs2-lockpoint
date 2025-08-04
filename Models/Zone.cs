using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Generic;
using System.Linq;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace HardpointCS2.Models
{
    public class Zone
    {
        public string Name { get; set; } = "";
        public List<CSVector> Points { get; set; } = new();
        public CSVector Center { get; set; } = new();
        public int ControllingTeam { get; set; } = -1;
        public float CaptureProgressTeam1 { get; set; } = 0;
        public float CaptureProgressTeam2 { get; set; } = 0;

        public bool IsPlayerInZone(CSVector playerPos)
        {
            return IsPointInPolygon(playerPos, Points);
        }

        private bool IsPointInPolygon(CSVector point, List<CSVector> polygon)
        {
            if (polygon.Count < 3) return false;
            
            bool inside = false;
            int j = polygon.Count - 1;

            for (int i = 0; i < polygon.Count; i++)
            {
                if (((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)) &&
                    (point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X))
                {
                    inside = !inside;
                }
                j = i;
            }
            return inside;
        }
    }
}