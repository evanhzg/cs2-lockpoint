using System.Numerics;
using System.Collections.Generic;

namespace HardpointCS2.Models
{
    public class ZoneDefinition
    {
        public string Name { get; set; } = "";
        public List<Vector3> Points { get; set; } = new();
    }
}