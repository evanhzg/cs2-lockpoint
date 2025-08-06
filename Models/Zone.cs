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

        public List<CSVector> TerroristSpawns { get; set; } = new ();
        public List<CSVector> CounterTerroristSpawns { get; set; } = new ();

        public CSVector Center { get; set; } = new();
        public int ControllingTeam { get; set; } = -1;
        public float CaptureProgressTeam1 { get; set; } = 0;
        public float CaptureProgressTeam2 { get; set; } = 0;

        public bool IsPlayerInZone(CSVector playerPos)
        {
            return IsPointInPolygon(playerPos, Points);
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

    public enum ZoneState
    {
        Neutral,      // Green - no players
        CTControlled, // Blue - only CT players
        TControlled,  // Red - only T players
        Contested     // Purple - both teams
    }
}