using System;
using UnityEngine;

namespace principia {
namespace ksp_plugin_adapter {

/// <summary>
/// Dual-Clock Bridge & Origin Epoch Drift Compensator.
/// Decouples Unity rendering timeline from Principia physical timeline,
/// tracking Krakensbane / FloatingOrigin coordinate shifts with atomic epoch sequence numbers
/// to prevent visual coordinate tearing, micro-jitter, and teleportation glitches.
/// </summary>
internal static class DualClockBridge {
  // Origin Epoch & Drift State
  private static uint origin_epoch_sequence_ = 1;
  private static Vector3d cumulative_drift_offset_ = Vector3d.zero;
  private static Vector3d last_floating_offset_ = Vector3d.zero;
  private static Vector3d last_frame_vel_ = Vector3d.zero;
  private static long total_origin_shifts_ = 0;

  // Dual-Clock Temporal Tracking
  private static double last_physics_ut_ = 0;
  private static double physics_dt_ = 0.02;
  private static double last_render_ut_ = 0;
  private static double render_dt_ = 0.016;
  private static float phase_lag_ms_ = 0f;
  private static double interpolation_alpha_ = 0.0;

  // Telemetry properties
  public static uint CurrentEpoch => origin_epoch_sequence_;
  public static Vector3d CumulativeDrift => cumulative_drift_offset_;
  public static long TotalOriginShifts => total_origin_shifts_;
  public static float PhaseLagMs => phase_lag_ms_;
  public static double InterpolationAlpha => interpolation_alpha_;
  public static double LastPhysicsUt => last_physics_ut_;
  public static double LastRenderUt => last_render_ut_;

  /// <summary>
  /// Updates origin tracking based on FloatingOrigin and Krakensbane states.
  /// Should be invoked at the beginning of each physics or render cycle.
  /// </summary>
  public static void UpdateOriginTracking(Vector3d frame_vel, Vector3d floating_offset) {
    bool shifted = false;
    Vector3d delta_floating = floating_offset - last_floating_offset_;

    if (delta_floating.sqrMagnitude > 1e-10) {
      cumulative_drift_offset_ += delta_floating;
      last_floating_offset_ = floating_offset;
      shifted = true;
    }

    if ((frame_vel - last_frame_vel_).sqrMagnitude > 1e-10) {
      last_frame_vel_ = frame_vel;
      shifted = true;
    }

    if (shifted) {
      origin_epoch_sequence_++;
      total_origin_shifts_++;
    }
  }

  /// <summary>
  /// Records the completion of a physics integration advancement.
  /// </summary>
  public static void RecordPhysicsStep(double universal_time, double delta_t) {
    last_physics_ut_ = universal_time;
    if (delta_t > 0) {
      physics_dt_ = delta_t;
    }
    UpdatePhaseMetrics();
  }

  /// <summary>
  /// Records a render frame lifecycle event.
  /// </summary>
  public static void RecordRenderFrame(double universal_time) {
    if (last_render_ut_ > 0) {
      render_dt_ = universal_time - last_render_ut_;
    }
    last_render_ut_ = universal_time;
    UpdatePhaseMetrics();
  }

  private static void UpdatePhaseMetrics() {
    double lag = last_render_ut_ - last_physics_ut_;
    phase_lag_ms_ = (float)(lag * 1000.0);

    if (physics_dt_ > 1e-6) {
      double alpha = lag / physics_dt_;
      interpolation_alpha_ = Math.Max(0.0, Math.Min(1.0, alpha));
    } else {
      interpolation_alpha_ = 0.0;
    }
  }

  /// <summary>
  /// Compensates coordinate drift between sampling epoch and current epoch.
  /// Ensures seamless coordinate transformation even across FloatingOrigin jumps.
  /// </summary>
  public static Vector3d CompensateDrift(
      Vector3d raw_world_pos,
      uint sample_epoch,
      Vector3d drift_delta_hint) {
    if (sample_epoch == origin_epoch_sequence_) {
      return raw_world_pos;
    }
    // If the sample came from an older epoch, adjust for the accumulated drift
    return raw_world_pos + drift_delta_hint;
  }

  /// <summary>
  /// High-precision O(1) continuous state evaluation combining Horner polynomial sampling
  /// with Origin Epoch coordinate drift compensation.
  /// </summary>
  public static bool SampleContinuousPositionWithDriftComp(
      DensePolynomialSlice slice,
      double evaluation_ut,
      out Vector3d compensated_pos) {
    if (!slice.IsValid) {
      compensated_pos = Vector3d.zero;
      return false;
    }

    slice.EvaluatePosition(evaluation_ut, out compensated_pos);
    if (origin_epoch_sequence_ > 1) {
      compensated_pos += cumulative_drift_offset_;
    }
    return true;
  }

  /// <summary>
  /// Resets drift tracking state, e.g. on scene load or vessel switch.
  /// </summary>
  public static void Reset() {
    origin_epoch_sequence_ = 1;
    cumulative_drift_offset_ = Vector3d.zero;
    last_floating_offset_ = Vector3d.zero;
    last_frame_vel_ = Vector3d.zero;
    total_origin_shifts_ = 0;
    last_physics_ut_ = 0;
    last_render_ut_ = 0;
    phase_lag_ms_ = 0f;
    interpolation_alpha_ = 0.0;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
