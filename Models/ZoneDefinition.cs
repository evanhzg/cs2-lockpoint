using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Linq;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace HardpointCS2.Models
{
    public class ZoneDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        
        [JsonPropertyName("points")]
        public List<SerializableVector> Points { get; set; } = new();
    }

    public class SerializableVector
    {
        [JsonPropertyName("x")]
        public float X { get; set; }
        
        [JsonPropertyName("y")]
        public float Y { get; set; }
        
        [JsonPropertyName("z")]
        public float Z { get; set; }

        public SerializableVector() { }

        public SerializableVector(CSVector vector)
        {
            X = vector.X;
            Y = vector.Y;
            Z = vector.Z;
        }

        public CSVector ToCSVector()
        {
            return new CSVector(X, Y, Z);
        }
    }

    public class MapZoneData
    {
        [JsonPropertyName("map_name")]
        public string MapName { get; set; } = "";
        
        [JsonPropertyName("zones")]
        public List<ZoneDefinition> Zones { get; set; } = new();
    }
}