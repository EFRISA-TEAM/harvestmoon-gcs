using System.Collections.Generic;
using System.Linq;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Helpers;

public static class WaypointMapper
{
    public static List<MissionWaypoint> ToMissionWaypoints(IEnumerable<WaypointData> waypoints)
    {
        return waypoints
            .OrderBy(w => w.Sequence)
            .Select((wp, idx) => new MissionWaypoint
            {
                Sequence = wp.Sequence,
                Command = (MavCommand)wp.Command,
                Latitude = wp.Latitude,
                Longitude = wp.Longitude,
                Altitude = wp.Altitude,
                Frame = MavFrame.GlobalRelativeAlt,
                IsAutoContinue = true,
                Param1 = (float)wp.Param1,
                Param2 = (float)wp.Param2,
                Param3 = (float)wp.Param3,
                Param4 = (float)wp.Param4,
                IsCurrent = wp.IsCurrent
            })
            .ToList();
    }

    public static List<WaypointData> FromMissionWaypoints(IEnumerable<MissionWaypoint> missionWaypoints)
    {
        return missionWaypoints
            .OrderBy(w => w.Sequence)
            .Select((mw, idx) => new WaypointData
            {
                Sequence = mw.Sequence,
                Latitude = mw.Latitude,
                Longitude = mw.Longitude,
                Altitude = mw.Altitude,
                Command = (WaypointCommand)mw.Command,
                Param1 = mw.Param1,
                Param2 = mw.Param2,
                Param3 = mw.Param3,
                Param4 = mw.Param4,
                IsCurrent = mw.IsCurrent
            })
            .ToList();
    }
}
