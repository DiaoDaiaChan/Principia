using System;
using System.Diagnostics;

namespace principia {
namespace ksp_plugin_adapter {

/// <summary>
/// Event & Dynamics Driven Marker Invalidation Governor with Time-Slicing Budget.
/// Eliminates all periodic polling and hardcoded time intervals.
/// Markers are recomputed ONLY when physical dynamics change (integration step, burn, target/frame switch).
/// In coasting state, execution cost in LateUpdate is strictly 0.000 ms.
/// When dirty, computation is smoothly budgeted (<= 0.6 ms/frame) to eliminate frame spikes.
/// </summary>
public static class MapMarkerEventGovernor {
  public const double LateUpdateBudgetMs = 0.6;
  private static readonly Stopwatch stopwatch_ = new Stopwatch();

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

  public static bool TaskExecutedThisFrame { get; set; } = false;

  public static void BeginFrame() {
    stopwatch_.Reset();
    stopwatch_.Start();
    TaskExecutedThisFrame = false;
  }

  public static bool HasBudgetRemaining => stopwatch_.Elapsed.TotalMilliseconds < LateUpdateBudgetMs;

  public static double ElapsedMs => stopwatch_.Elapsed.TotalMilliseconds;

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
    stopwatch_.Reset();
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
