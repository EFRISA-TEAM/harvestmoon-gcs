using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Tests.Services;

public class VideoScenarioServiceTests
{
    [Fact]
    public void PredefinedScenarios_HasAtLeastFourScenarios()
    {
        var service = new VideoScenarioService();
        Assert.NotNull(service.PredefinedScenarios);
        Assert.True(service.PredefinedScenarios.Count >= 4);
    }

    [Fact]
    public void PredefinedScenarios_AreAllDistinctInWaypointsAndTelemetry()
    {
        var service = new VideoScenarioService();
        var scenarios = service.PredefinedScenarios;

        for (int i = 0; i < scenarios.Count; i++)
        {
            var s1 = scenarios[i];
            Assert.NotEmpty(s1.Waypoints);
            Assert.True(s1.FieldCenterLat != 0);
            Assert.True(s1.FieldCenterLon != 0);
            Assert.True(s1.CruiseAltitudeMeters > 0);
            Assert.True(s1.CruiseSpeedMps > 0);
            Assert.NotEmpty(s1.MissionId);
            Assert.NotEmpty(s1.AreaName);
            Assert.NotEmpty(s1.FarmerRecommendation);

            for (int j = i + 1; j < scenarios.Count; j++)
            {
                var s2 = scenarios[j];

                // Coordinates must not match
                Assert.NotEqual(s1.FieldCenterLat, s2.FieldCenterLat);
                Assert.NotEqual(s1.FieldCenterLon, s2.FieldCenterLon);

                // Flight parameters should be different
                Assert.True(
                    Math.Abs(s1.BearingDeg - s2.BearingDeg) > 0.001 ||
                    Math.Abs(s1.CruiseAltitudeMeters - s2.CruiseAltitudeMeters) > 0.001 ||
                    Math.Abs(s1.TargetFhi - s2.TargetFhi) > 0.001,
                    $"Scenario {s1.Name} and {s2.Name} must have different parameters"
                );

                // Waypoint coordinates must not be identical
                var wpCoords1 = s1.Waypoints.Select(w => (w.Latitude, w.Longitude)).ToList();
                var wpCoords2 = s2.Waypoints.Select(w => (w.Latitude, w.Longitude)).ToList();
                Assert.NotEqual(wpCoords1, wpCoords2);
            }
        }
    }

    [Fact]
    public void SelectScenarioByIndex_ChangesCurrentScenarioAndFiresEvent()
    {
        var service = new VideoScenarioService();
        bool eventFired = false;
        VideoScenarioProfile? receivedProfile = null;

        service.ScenarioChanged += (s, profile) =>
        {
            eventFired = true;
            receivedProfile = profile;
        };

        var selected = service.SelectScenarioByIndex(1);
        Assert.True(eventFired);
        Assert.NotNull(receivedProfile);
        Assert.Equal(service.PredefinedScenarios[1].Id, selected.Id);
        Assert.Equal(selected.Id, service.CurrentScenario.Id);
    }

    [Fact]
    public void LoadScenarioForVideo_DifferentVideos_GenerateDifferentWaypointsAndTelemetry()
    {
        var service = new VideoScenarioService();

        var profileA = service.LoadScenarioForVideo("/path/to/custom_flight_alpha.mp4", 60.0);
        var profileB = service.LoadScenarioForVideo("/path/to/custom_flight_beta.mp4", 90.0);
        var profileC = service.LoadScenarioForVideo("/another/path/field_gamma.mp4", 120.0);

        // Centers must be different
        Assert.NotEqual(profileA.FieldCenterLat, profileB.FieldCenterLat);
        Assert.NotEqual(profileB.FieldCenterLat, profileC.FieldCenterLat);

        // Waypoints must be non-empty and different
        Assert.NotEmpty(profileA.Waypoints);
        Assert.NotEmpty(profileB.Waypoints);
        Assert.NotEmpty(profileC.Waypoints);

        var wp1 = profileA.Waypoints.Select(w => (w.Latitude, w.Longitude)).ToList();
        var wp2 = profileB.Waypoints.Select(w => (w.Latitude, w.Longitude)).ToList();
        Assert.NotEqual(wp1, wp2);

        // Target FHI, Altitude, Bearing must differ
        Assert.NotEqual(profileA.TargetFhi, profileB.TargetFhi);
        Assert.NotEqual(profileA.BearingDeg, profileB.BearingDeg);
    }

    [Fact]
    public void LoadScenarioForVideo_SameVideo_IsDeterministic()
    {
        var service = new VideoScenarioService();

        var profile1 = service.LoadScenarioForVideo("/some/directory/rice_survey.mp4", 75.0);
        var profile2 = service.LoadScenarioForVideo("/some/directory/rice_survey.mp4", 75.0);

        Assert.Equal(profile1.FieldCenterLat, profile2.FieldCenterLat);
        Assert.Equal(profile1.FieldCenterLon, profile2.FieldCenterLon);
        Assert.Equal(profile1.BearingDeg, profile2.BearingDeg);
        Assert.Equal(profile1.CruiseAltitudeMeters, profile2.CruiseAltitudeMeters);
        Assert.Equal(profile1.TargetFhi, profile2.TargetFhi);
        Assert.Equal(profile1.Waypoints.Count, profile2.Waypoints.Count);
        for (int i = 0; i < profile1.Waypoints.Count; i++)
        {
            Assert.Equal(profile1.Waypoints[i].Latitude, profile2.Waypoints[i].Latitude);
            Assert.Equal(profile1.Waypoints[i].Longitude, profile2.Waypoints[i].Longitude);
        }
    }

    [Fact]
    public void BuildLinearWaypoints_GeneratesValidWaypoints()
    {
        double centerLat = -6.4258;
        double centerLon = 107.6189;
        int count = 5;
        double totalMeters = 600.0;
        double bearingDeg = 90.0;
        double altitude = 65.0;

        var waypoints = VideoScenarioProfile.BuildLinearWaypoints(centerLat, centerLon, count, totalMeters, bearingDeg, altitude);

        Assert.Equal(count, waypoints.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i + 1, waypoints[i].Sequence);
            Assert.Equal(altitude, waypoints[i].Altitude);
            Assert.InRange(waypoints[i].Latitude, -90.0, 90.0);
            Assert.InRange(waypoints[i].Longitude, -180.0, 180.0);
        }

        // Bearing 90 deg (East): longitudes must strictly increase
        for (int i = 1; i < count; i++)
        {
            Assert.True(waypoints[i].Longitude > waypoints[i - 1].Longitude);
        }
    }
}
