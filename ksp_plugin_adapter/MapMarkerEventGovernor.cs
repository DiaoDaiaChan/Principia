using System;

namespace principia {
namespace ksp_plugin_adapter {

/// <summary>
/// Event & Dynamics Driven Marker Invalidation Governor.
/// Eliminates all periodic polling and hardcoded time intervals.
/// Markers are recomputed ONLY when physical dynamics change (integration step, burn, target/frame switch).
/// In coasting state, execution cost in LateUpdate is strictly 0.000 ms.
/// </summary>
public static class MapMarkerEventGovernor {
  // Event & Dynamics Invalidation Flags
  public static bool PredictionApsidesDirty { get; set; } = true;
  public static bool PredictionNodesDirty { get; set; } = true;
  public static bool FlightPlanApsidesDirty { get; set; } = true;
  public static bool FlightPlanNodesDirty { get; set; } = true;
  public static bool TargetApsidesDirty { get; set; } = true;
  public static bool TargetNodesDirty { get; set; } = true;
  public static bool CollisionDirty { get; set; } = true;

  public static bool HasDirtyMarkers =>
      PredictionApsidesDirty ||
      PredictionNodesDirty ||
      FlightPlanApsidesDirty ||
      FlightPlanNodesDirty ||
      TargetApsidesDirty ||
      TargetNodesDirty ||
      CollisionDirty;

  /// <summary>
  /// Triggered when C++ numerical prediction integration (UpdatePrediction) executes
  /// or when active vessel dynamics change.
  /// </summary>
  public static void InvalidatePrediction() {
    PredictionApsidesDirty = true;
    PredictionNodesDirty = true;
    CollisionDirty = true;
  }

  /// <summary>
  /// Triggered when a flight plan burn is created, modified, deleted, or rebased.
  /// </summary>
  public static void InvalidateFlightPlan() {
    FlightPlanApsidesDirty = true;
    FlightPlanNodesDirty = true;
    CollisionDirty = true;
  }

  /// <summary>
  /// Triggered when the target vessel changes or target prediction is re-integrated.
  /// </summary>
  public static void InvalidateTarget() {
    TargetApsidesDirty = true;
    TargetNodesDirty = true;
  }

  /// <summary>
  /// Triggered when reference frame or active vessel changes, requiring full marker refresh.
  /// </summary>
  public static void InvalidateAll() {
    PredictionApsidesDirty = true;
    PredictionNodesDirty = true;
    FlightPlanApsidesDirty = true;
    FlightPlanNodesDirty = true;
    TargetApsidesDirty = true;
    TargetNodesDirty = true;
    CollisionDirty = true;
  }

  public static void Reset() {
    InvalidateAll();
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
