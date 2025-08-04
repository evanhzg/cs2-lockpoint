using CounterStrikeSharp.API.Modules.Utils;
using CSVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace HardpointCS2.Models
{
    public class ZonePoint
    {
        public CSVector Position { get; set; }
        public string ZoneId { get; set; }

        public ZonePoint(CSVector position, string zoneId)
        {
            Position = position;
            ZoneId = zoneId;
        }
    }
}