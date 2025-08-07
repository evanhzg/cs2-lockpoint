using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API;
using System.Collections.Generic;
using System.Linq;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Lockpoint.Models
{
    public class Zone
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<CSVector> Points { get; set; } = new();
        public List<CCSPlayerController> PlayersInZone { get; set; } = new();

        public List<SpawnPoint> TerroristSpawns { get; set; } = new();
        public List<SpawnPoint> CounterTerroristSpawns { get; set; } = new();

        public CSVector Center { get; set; } = new();
        public int ControllingTeam { get; set; } = -1;
        public float CaptureProgressTeam1 { get; set; } = 0;
        public float CaptureProgressTeam2 { get; set; } = 0;

        public bool IsPlayerInZone(CSVector playerPos)
        {
            return IsPointInPolygonWithBuffer(playerPos.X, playerPos.Y, Points, 32.0f);
        }

        public void CleanupInvalidPlayers()
        {
            try
            {
                PlayersInZone.RemoveAll(p =>
                    p?.IsValid != true ||
                    p.Connected != PlayerConnectedState.PlayerConnected ||
                    p.PlayerPawn?.Value == null);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Zone] Error cleaning up players: {ex.Message}");
                // If cleanup fails, just clear the entire list
                PlayersInZone.Clear();
            }
        }

        public class SpawnPoint
        {
            public CSVector Position { get; set; } = new();
            public QAngle ViewAngle { get; set; } = new();
            public SpawnPoint() { }
            public SpawnPoint(CSVector position, QAngle viewAngle)
            {
                Position = position;
                ViewAngle = viewAngle;
            }
        }

        public ZoneState GetZoneState()
        {
            try
            {
                // Filter out invalid players first and ensure they have valid entities
                var validPlayers = PlayersInZone.Where(p =>
                    p?.IsValid == true &&
                    p.Connected == PlayerConnectedState.PlayerConnected &&
                    p.PlayerPawn?.Value != null).ToList();

                int ctCount = 0;
                int tCount = 0;

                foreach (var player in validPlayers)
                {
                    try
                    {
                        if (player?.IsValid == true && player.TeamNum > 0) // Fix: Remove null check on int
                        {
                            if (player.TeamNum == (byte)CsTeam.CounterTerrorist)
                                ctCount++;
                            else if (player.TeamNum == (byte)CsTeam.Terrorist)
                                tCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Skip this player if we can't access their team
                        Server.PrintToConsole($"[Zone] Error accessing player TeamNum: {ex.Message}");
                        continue;
                    }
                }

                if (ctCount > 0 && tCount > 0)
                    return ZoneState.Contested; // Purple
                else if (ctCount > 0)
                    return ZoneState.CTControlled; // Blue
                else if (tCount > 0)
                    return ZoneState.TControlled; // Red
                else
                    return ZoneState.Neutral; // White
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Zone] Error in GetZoneState: {ex.Message}");
                return ZoneState.Neutral; // Default to neutral on any error
            }
        }

        private bool IsPointInPolygonWithBuffer(float x, float y, List<CSVector> polygon, float buffer = 32.0f)
        {
            // First check if point is inside the polygon normally
            if (IsPointInPolygon(x, y, polygon))
            {
                return true;
            }

            // If not inside, check if point is within buffer distance of any edge
            return IsPointNearPolygonEdge(x, y, polygon, buffer);
        }

        private bool IsPointInPolygon(float x, float y, List<CSVector> polygon)
        {
            int intersections = 0;
            int vertexCount = polygon.Count;

            for (int i = 0; i < vertexCount; i++)
            {
                int j = (i + 1) % vertexCount;

                if (((polygon[i].Y > y) != (polygon[j].Y > y)) &&
                    (x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X))
                {
                    intersections++;
                }
            }

            return (intersections % 2) == 1;
        }

        private bool IsPointNearPolygonEdge(float x, float y, List<CSVector> polygon, float buffer)
        {
            int vertexCount = polygon.Count;

            for (int i = 0; i < vertexCount; i++)
            {
                int j = (i + 1) % vertexCount;

                var p1 = polygon[i];
                var p2 = polygon[j];

                float distance = DistanceFromPointToLineSegment(x, y, p1.X, p1.Y, p2.X, p2.Y);

                if (distance <= buffer)
                {
                    return true;
                }
            }

            return false;
        }

        private float DistanceFromPointToLineSegment(float px, float py, float x1, float y1, float x2, float y2)
        {
            float dx = px - x1;
            float dy = py - y1;

            float lineX = x2 - x1;
            float lineY = y2 - y1;

            float lineLengthSquared = lineX * lineX + lineY * lineY;

            if (lineLengthSquared == 0)
            {
                return (float)Math.Sqrt(dx * dx + dy * dy);
            }

            float t = Math.Max(0, Math.Min(1, (dx * lineX + dy * lineY) / lineLengthSquared));

            float closestX = x1 + t * lineX;
            float closestY = y1 + t * lineY;

            float distX = px - closestX;
            float distY = py - closestY;

            return (float)Math.Sqrt(distX * distX + distY * distY);
        }
    }

    public enum ZoneState
    {
        Neutral,      // Green - no players
        CTControlled, // Blue - only CT players
        TControlled,  // Red - only T players
        Contested     // Purple - both teams
    }
}