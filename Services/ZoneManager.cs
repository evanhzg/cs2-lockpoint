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
            var filePath = Path.Combine(_zonesDirectory, $"{mapName}.json");
            
            Server.PrintToConsole($"[Lockpoint] LoadZonesForMap called for map: {mapName}");
            Server.PrintToConsole($"[Lockpoint] Looking for file: {filePath}");
            Server.PrintToConsole($"[Lockpoint] File exists: {File.Exists(filePath)}");
            
            if (!File.Exists(filePath))
            {
                Server.PrintToConsole($"[Lockpoint] No zone file found for map {mapName}");
                
                // List all files in the directory to see what's there
                if (Directory.Exists(_zonesDirectory))
                {
                    var files = Directory.GetFiles(_zonesDirectory);
                    Server.PrintToConsole($"[Lockpoint] Files in zones directory: {files.Length}");
                    foreach (var file in files)
                    {
                        Server.PrintToConsole($"[Lockpoint] - {Path.GetFileName(file)}");
                    }
                }
                else
                {
                    Server.PrintToConsole($"[Lockpoint] Zones directory doesn't exist: {_zonesDirectory}");
                }
                return;
            }

            try
            {
                var jsonContent = File.ReadAllText(filePath);
                Server.PrintToConsole($"[Lockpoint] Read JSON content, length: {jsonContent.Length}");
                
                var mapData = JsonSerializer.Deserialize<MapZoneData>(jsonContent);
                Server.PrintToConsole($"[Lockpoint] Deserialized map data, zones: {mapData?.Zones?.Count ?? 0}");
                
                if (mapData?.Zones != null)
                {
                    Zones.Clear();
                    Server.PrintToConsole($"[Lockpoint] Processing {mapData.Zones.Count} zone definitions");
                    
                    foreach (var zoneDef in mapData.Zones)
                    {
                        var zone = new Zone
                        {
                            Name = zoneDef.Name,
                            Points = zoneDef.Points.Select(p => new CSVector(p.X, p.Y, p.Z)).ToList(),
                            TerroristSpawns = zoneDef.TerroristSpawns?.Select(p => new CSVector(p.X, p.Y, p.Z)).ToList() ?? new List<CSVector>(),
                            CounterTerroristSpawns = zoneDef.CounterTerroristSpawns?.Select(p => new CSVector(p.X, p.Y, p.Z)).ToList() ?? new List<CSVector>()
                        };

                        // Calculate center
                        if (zone.Points.Count > 0)
                        {
                            var centerX = zone.Points.Sum(p => p.X) / zone.Points.Count;
                            var centerY = zone.Points.Sum(p => p.Y) / zone.Points.Count;
                            var centerZ = zone.Points.Sum(p => p.Z) / zone.Points.Count;
                            zone.Center = new CSVector(centerX, centerY, centerZ);
                        }

                        Zones.Add(zone);
                        Server.PrintToConsole($"[Lockpoint] Added/Edited zone '{zone.Name}' with {zone.Points.Count} points, {zone.TerroristSpawns.Count} T spawns, {zone.CounterTerroristSpawns.Count} CT spawns");
                    }

                    Server.PrintToConsole($"[Lockpoint] Final Zones count: {Zones.Count}");
                }
                else
                {
                    Server.PrintToConsole($"[Lockpoint] mapData or mapData.Zones is null");
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[Lockpoint] Error loading zones for map {mapName}: {ex.Message}");
                Server.PrintToConsole($"[Lockpoint] Stack trace: {ex.StackTrace}");
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
                var mapData = new MapZoneData
                {
                    MapName = mapName,
                    Zones = zonesToSave.Select(zone => new ZoneDefinition
                    {
                        Name = zone.Name,
                        Points = zone.Points.Select(p => new SerializableVector(p)).ToList(),
                        TerroristSpawns = zone.TerroristSpawns.Select(p => new SerializableVector(p)).ToList(),
                        CounterTerroristSpawns = zone.CounterTerroristSpawns.Select(p => new SerializableVector(p)).ToList()
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