using System.Collections.Generic;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Interface for managing waypoints in a mission
/// </summary>
public interface IWaypointService
{
    /// <summary>
    /// Gets all waypoints in the current mission
    /// </summary>
    Task<List<WaypointData>> GetWaypointsAsync();

    /// <summary>
    /// Gets a specific waypoint by sequence number
    /// </summary>
    Task<WaypointData?> GetWaypointAsync(int sequence);

    /// <summary>
    /// Adds a new waypoint to the mission
    /// </summary>
    Task AddWaypointAsync(WaypointData waypoint);

    /// <summary>
    /// Updates an existing waypoint
    /// </summary>
    Task UpdateWaypointAsync(WaypointData waypoint);

    /// <summary>
    /// Removes a waypoint by sequence number
    /// </summary>
    Task RemoveWaypointAsync(int sequence);

    /// <summary>
    /// Clears all waypoints from the mission
    /// </summary>
    Task ClearWaypointsAsync();

    /// <summary>
    /// Replaces all waypoints in the mission with the specified collection
    /// </summary>
    Task ReplaceWaypointsAsync(IEnumerable<WaypointData> waypoints);

    /// <summary>
    /// Gets the current waypoint the vehicle is heading to
    /// </summary>
    Task<WaypointData?> GetCurrentWaypointAsync();

    /// <summary>
    /// Sets the current waypoint the vehicle should go to
    /// </summary>
    Task SetCurrentWaypointAsync(int sequence);

    /// <summary>
    /// Gets the number of waypoints in the mission
    /// </summary>
    int WaypointCount { get; }

    /// <summary>
    /// Event fired when waypoints are added, removed, or modified
    /// </summary>
    event EventHandler? WaypointsChanged;

    /// <summary>
    /// Loads waypoints from a file
    /// </summary>
    Task LoadWaypointsFromFileAsync(string filePath);

    /// <summary>
    /// Saves waypoints to a file
    /// </summary>
    Task SaveWaypointsToFileAsync(string filePath);

    /// <summary>
    /// Undo the last waypoint change (add/remove/update/replace/clear)
    /// </summary>
    Task UndoAsync();

    /// <summary>
    /// Redo the last undone waypoint change
    /// </summary>
    Task RedoAsync();

    /// <summary>
    /// Whether an undo operation is available
    /// </summary>
    bool CanUndo { get; }

    /// <summary>
    /// Whether a redo operation is available
    /// </summary>
    bool CanRedo { get; }
}
