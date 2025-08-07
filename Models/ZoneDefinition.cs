using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Linq;
using CounterStrikeSharp.API.Modules.Utils;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;
namespace Lockpoint.Models
{
    public class ZoneDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("points")]
        public List<SerializableVector> Points { get; set; } = new();

        [JsonPropertyName("t_spawns")]
        public List<SerializableSpawnPoint> TerroristSpawns { get; set; } = new();
        [JsonPropertyName("ct_spawns")]
        public List<SerializableSpawnPoint> CounterTerroristSpawns { get; set; } = new();

    }

    public class SerializableVector
    {
        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }

        // Add constructor
        public SerializableVector() { }

        public SerializableVector(CSVector vector)
        {
            X = vector.X;
            Y = vector.Y;
            Z = vector.Z;
        }

        // Add ToCSVector method
        public CSVector ToCSVector()
        {
            return new CSVector(X, Y, Z);
        }
    }

    public class SerializableQAngle
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        // Add constructor
        public SerializableQAngle() { }

        public SerializableQAngle(QAngle angle)
        {
            X = angle.X;
            Y = angle.Y;
            Z = angle.Z;
        }

        // Add ToQAngle method
        public QAngle ToQAngle()
        {
            return new QAngle(X, Y, Z);
        }
    }

    public class SerializableSpawnPoint
    {
        [JsonPropertyName("position")]
        public SerializableVector Position { get; set; } = new();

        [JsonPropertyName("view_angle")]
        public SerializableQAngle ViewAngle { get; set; } = new();
    }
    public class MapZoneData
    {
        [JsonPropertyName("map_name")]
        public string MapName { get; set; } = "";

        [JsonPropertyName("zones")]
        public List<ZoneDefinition> Zones { get; set; } = new();
    }
}