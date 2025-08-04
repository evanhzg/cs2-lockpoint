using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;
using HardpointCS2.Models;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace HardpointCS2.Services
{
    public class ZoneManager
    {
        public List<Zone> Zones { get; set; } = new();
        private const string zonesFilePath = "src/Data/zones.json";

        public void AddZone(string zoneName)
        {
            Zones.Add(new Zone { Name = zoneName, Points = new List<CSVector>() });
        }

        public void AddPointToZone(string zoneName, CSVector position)
        {
            var zone = Zones.Find(z => z.Name == zoneName);
            if (zone != null)
            {
                zone.Points.Add(position); 
            }
        }

        public void EndZone(string zoneName)
        {
            SaveZones();
        }

        private void SaveZones()
        {
            var json = JsonSerializer.Serialize(Zones);
            File.WriteAllText(zonesFilePath, json);
        }

        public void LoadZonesForMap(string mapName)
        {
            // Implementation for loading zones from JSON
            // Example of creating a zone point if needed:
            // var point = new ZonePoint(new CSVector(0, 0, 0), "zone1");
        }

        public Zone? GetZoneAtPosition(CSVector position)
        {
            foreach (var zone in Zones)
            {
                if (zone.IsPlayerInZone(position))
                    return zone;
            }
            return null;
        }

        public void AddZone(Zone zone)
        {
            Zones.Add(zone);
        }

        public void RemoveZone(string zoneName)
        {
            Zones.RemoveAll(z => z.Name == zoneName);
        }
    }
}