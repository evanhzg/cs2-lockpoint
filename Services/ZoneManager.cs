using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using Lockpoint.Models;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;
using System.Reflection;

namespace Lockpoint.Services
{
    public class ZoneManager
    {
        public List<Zone> Zones { get; set; } = new();
        private readonly string _zonesDirectory;


        public string GetZonesDirectoryPath()
        {
            return _zonesDirectory;
        }

        public ZoneManager(string moduleDirectory)
        {
            _zonesDirectory = Path.Join(moduleDirectory, "zones");
            Zones = new List<Zone>();

            Server.PrintToConsole($"[Lockpoint] Module directory: {moduleDirectory}");
            Server.PrintToConsole($"[Lockpoint] Zones directory: {_zonesDirectory}");

            if (!Directory.Exists(_zonesDirectory))
            {
                Directory.CreateDirectory(_zonesDirectory);
                Server.PrintToConsole($"[Lockpoint] Created zones directory");
            }
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
            SaveZonesForMap(zoneName, Zones);
        }

        public void LoadZonesForMap(string mapName)
        {
            try
            {
                Server.PrintToConsole($"[ZoneManager] LoadZonesForMap called for map: {mapName}");

                // Clear existing zones first
                Zones.Clear();

                var filePath = Path.Combine(_zonesDirectory, $"{mapName}.json");
                Server.PrintToConsole($"[ZoneManager] Looking for file: {filePath}");
                Server.PrintToConsole($"[ZoneManager] File exists: {File.Exists(filePath)}");

                if (!File.Exists(filePath))
                {
                    Server.PrintToConsole($"[ZoneManager] No zone file found for map {mapName}");

                    // Debug: Show what files exist
                    if (Directory.Exists(_zonesDirectory))
                    {
                        var files = Directory.GetFiles(_zonesDirectory, "*.json");
                        Server.PrintToConsole($"[ZoneManager] Files in zones directory: {files.Length}");
                        foreach (var file in files)
                        {
                            Server.PrintToConsole($"[ZoneManager] - {Path.GetFileName(file)}");
                        }
                    }

                    // IMPORTANT: Return here instead of continuing to load zones
                    return;
                }

                var jsonContent = File.ReadAllText(filePath);
                if (string.IsNullOrEmpty(jsonContent))
                {
                    Server.PrintToConsole($"[ZoneManager] Zone file for {mapName} is empty");
                    return;
                }

                // Try to deserialize as new format first (MapZoneData)
                try
                {
                    var loadedData = JsonSerializer.Deserialize<MapZoneData>(jsonContent);
                    if (loadedData?.Zones != null)
                    {
                        Zones = loadedData.Zones.Select(zd => new Zone
                        {
                            Name = zd.Name,
                            Points = zd.Points.Select(p => p.ToCSVector()).ToList(),
                            TerroristSpawns = zd.TerroristSpawns.Select(sp => new Zone.SpawnPoint(
                                sp.Position.ToCSVector(),
                                sp.ViewAngle.ToQAngle()
                            )).ToList(),
                            CounterTerroristSpawns = zd.CounterTerroristSpawns.Select(sp => new Zone.SpawnPoint(
                                sp.Position.ToCSVector(),
                                sp.ViewAngle.ToQAngle()
                            )).ToList()
                        }).ToList();

                        Server.PrintToConsole($"[ZoneManager] Successfully loaded {Zones.Count} zones with new format (includes view angles)");
                    }
                }
                catch (JsonException)
                {
                    // If new format fails, try old format
                    var loadedZones = JsonSerializer.Deserialize<List<Zone>>(jsonContent);
                    if (loadedZones != null)
                    {
                        Zones = loadedZones;
                        Server.PrintToConsole($"[ZoneManager] Successfully loaded {Zones.Count} zones with old format (no view angles)");
                    }
                }

                if (Zones != null)
                {
                    // Assign IDs after loading
                    for (int i = 0; i < Zones.Count; i++)
                    {
                        Zones[i].Id = i;
                    }

                    Server.PrintToConsole($"[ZoneManager] Total zones loaded: {Zones.Count}");
                }
                else
                {
                    Server.PrintToConsole($"[ZoneManager] Failed to deserialize zones for map {mapName}");
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[ZoneManager] Error loading zones for map {mapName}: {ex.Message}");
                Zones.Clear(); // Clear zones on error
            }
        }
        public void SaveZonesForMap(string mapName, List<Zone> zonesToSave)
        {
            var filePath = Path.Combine(_zonesDirectory, $"{mapName}.json");

            Server.PrintToConsole($"[Lockpoint] === SAVE DEBUG START ===");
            Server.PrintToConsole($"[Lockpoint] SaveZonesForMap called");
            Server.PrintToConsole($"[Lockpoint] Map: {mapName}");
            Server.PrintToConsole($"[Lockpoint] Zones to save: {zonesToSave.Count}");
            Server.PrintToConsole($"[Lockpoint] Zones directory: {_zonesDirectory}");
            Server.PrintToConsole($"[Lockpoint] Full file path: {filePath}");
            Server.PrintToConsole($"[Lockpoint] Directory exists: {Directory.Exists(_zonesDirectory)}");

            // Test write permissions by creating a test file
            try
            {
                var testFile = Path.Combine(_zonesDirectory, "test.txt");
                File.WriteAllText(testFile, "test");
                Server.PrintToConsole($"[Lockpoint] Write permission test: SUCCESS");
                File.Delete(testFile);
            }
            catch (Exception testEx)
            {
                Server.PrintToConsole($"[Lockpoint] Write permission test: FAILED - {testEx.Message}");
            }

            try
            {
                for (int i = 0; i < zonesToSave.Count; i++)
                {
                    zonesToSave[i].Id = i;
                }
                var mapData = new MapZoneData
                {
                    MapName = mapName,
                    Zones = zonesToSave.Select(zone => new ZoneDefinition
                    {
                        Name = zone.Name,
                        Points = zone.Points.Select(p => new SerializableVector(p)).ToList(),
                        TerroristSpawns = zone.TerroristSpawns.Select(sp => new SerializableSpawnPoint
                        {
                            Position = new SerializableVector(sp.Position),
                            ViewAngle = new SerializableQAngle(sp.ViewAngle)
                        }).ToList(),
                        CounterTerroristSpawns = zone.CounterTerroristSpawns.Select(sp => new SerializableSpawnPoint
                        {
                            Position = new SerializableVector(sp.Position),
                            ViewAngle = new SerializableQAngle(sp.ViewAngle)
                        }).ToList()
                    }).ToList()
                };

                Server.PrintToConsole($"[Lockpoint] MapData created with {mapData.Zones.Count} zones");

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                var jsonContent = JsonSerializer.Serialize(mapData, options);
                Server.PrintToConsole($"[Lockpoint] JSON serialized, length: {jsonContent.Length}");

                // Print first 100 chars of JSON for verification
                var preview = jsonContent.Length > 100 ? jsonContent.Substring(0, 100) + "..." : jsonContent;
                Server.PrintToConsole($"[Lockpoint] JSON preview: {preview}");

                File.WriteAllText(filePath, jsonContent);
                Server.PrintToConsole($"[Lockpoint] File.WriteAllText completed");

                // Verify file was created
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    Server.PrintToConsole($"[Lockpoint] File verification: EXISTS, size: {fileInfo.Length} bytes");

                    // Read back the content to verify
                    var readBack = File.ReadAllText(filePath);
                    Server.PrintToConsole($"[Lockpoint] Read back length: {readBack.Length}");
                }
                else
                {
                    Server.PrintToConsole($"[Lockpoint] ERROR: File was not created!");
                }

                Server.PrintToConsole($"[Lockpoint] === SAVE DEBUG END ===");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] ERROR saving zones: {ex.Message}");
                Server.PrintToConsole($"[Lockpoint] Exception type: {ex.GetType().Name}");
                Server.PrintToConsole($"[Lockpoint] Stack trace: {ex.StackTrace}");
            }
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