using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Helpers
{
    /// <summary>
    /// Monitors geofence violations and triggers alerts
    /// </summary>
    public class GeofenceMonitor
    {
        private readonly IGeofenceService _geofenceService;
        private readonly IDialogService _dialogService;
        private readonly ILoggingService _logger;
        /// <summary>
        /// Event fired when geofence violation is detected
        /// </summary>
        public event EventHandler<GeofenceViolationEventArgs>? GeofenceViolated;

        /// <summary>
        /// Event fired when vehicle re-enters geofence
        /// </summary>
        public event EventHandler<GeofenceViolationEventArgs>? GeofenceRestored;

        public GeofenceMonitor(
            IGeofenceService geofenceService,
            IDialogService dialogService,
            ILoggingService logger)
        {
            _geofenceService = geofenceService;
            _dialogService = dialogService;
            _logger = logger;
        }

        /// <summary>
        /// Checks if position violates any active geofences
        /// </summary>
        public async Task CheckGeofenceViolationAsync(GeoCoordinate currentPosition)
        {
            var geofences = await _geofenceService.GetActiveGeofencesAsync();

            foreach (var geofence in geofences)
            {
                var altitude = currentPosition.Altitude;
                var distanceToBoundary = _geofenceService.CalculateDistanceToBoundary(
                    geofence,
                    currentPosition.Latitude,
                    currentPosition.Longitude,
                    altitude);
                var isInside = distanceToBoundary >= 0;
                var wasViolated = geofence.IsViolated;

                if (!isInside && !wasViolated)
                {
                    geofence.IsViolated = true;
                    await TriggerViolationAlert(geofence, currentPosition, Math.Abs(distanceToBoundary));
                }
                else if (isInside && wasViolated)
                {
                    geofence.IsViolated = false;
                    await TriggerRestoredAlert(geofence, currentPosition, distanceToBoundary);
                }
            }
        }

        private async Task TriggerViolationAlert(GeofenceData geofence, GeoCoordinate position, double outsideMeters)
        {
            var message = $"GEOFENCE VIOLATION: Vehicle left {geofence.Name} by {outsideMeters:F0} m";
            _logger.LogError(message, nameof(GeofenceMonitor));

            GeofenceViolated?.Invoke(this, new GeofenceViolationEventArgs
            {
                Geofence = geofence,
                Position = position,
                Message = message
            });

            await _dialogService.ShowAlertAsync(message, "Geofence Alert");
        }

        private async Task TriggerRestoredAlert(GeofenceData geofence, GeoCoordinate position, double insideMeters)
        {
            var message = $"Vehicle re-entered {geofence.Name}; {insideMeters:F0} m inside boundary";
            _logger.LogInfo(message, nameof(GeofenceMonitor));

            GeofenceRestored?.Invoke(this, new GeofenceViolationEventArgs
            {
                Geofence = geofence,
                Position = position,
                Message = message
            });

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Event args for geofence violations
    /// </summary>
    public class GeofenceViolationEventArgs : EventArgs
    {
        public GeofenceData Geofence { get; set; } = null!;
        public GeoCoordinate Position { get; set; } = null!;
        public string Message { get; set; } = string.Empty;
    }
}
