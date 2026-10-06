using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace principia {
namespace ksp_plugin_adapter {

internal enum CircuitBreakerLevel {
  Normal = 0,               // Within budget (< 50% budget)
  SecondaryThrottled = 1,   // Elapsed >= 50% budget: secondary vessels frozen to preserve active vessel
  ActiveExtrapolated = 2,   // Elapsed >= 100% budget: physics substep suppressed, active vessel extrapolated
  HardTripped = 3           // Consecutive frames over-budget: hard time debt absorption
}

/// <summary>
/// Phase 5 Frame Budget Circuit Breaker.
/// Defends against Unity's "Physics Death Spiral" by monitoring cumulative
/// physics CPU duration within each render frame and dynamically suppressing
/// redundant physics iterations while activating smooth kinematic extrapolation.
/// </summary>
internal static class FrameBudgetCircuitBreaker {
  // Master switch and budget settings
  public static bool EnableCircuitBreaker = true;
  public static float BudgetMilliseconds = 8.0f; // Default 8ms frame budget (~120 FPS target)
  public static int MaxAllowedFixedUpdatesPerFrame = 4;

  // High-resolution timing
  private static readonly Stopwatch frame_stopwatch_ = new Stopwatch();
  private static int last_render_frame_count_ = -1;
  private static int fixed_updates_in_current_frame_ = 0;
  private static int consecutive_overbudget_frames_ = 0;

  // Telemetry metrics
  private static float current_frame_physics_ms_ = 0.0f;
  private static float peak_frame_physics_ms_ = 0.0f;
  private static float avg_frame_physics_ms_ = 0.0f;
  private static long total_circuit_trips_ = 0;
  private static long total_substeps_suppressed_ = 0;
  private static CircuitBreakerLevel current_level_ = CircuitBreakerLevel.Normal;

  // Public Properties
  public static CircuitBreakerLevel CurrentLevel => current_level_;
  public static float CurrentFramePhysicsElapsedMs => current_frame_physics_ms_;
  public static float PeakFramePhysicsElapsedMs => peak_frame_physics_ms_;
  public static float AverageFramePhysicsElapsedMs => avg_frame_physics_ms_;
  public static long TotalCircuitTrips => total_circuit_trips_;
  public static long TotalSubstepsSuppressed => total_substeps_suppressed_;
  public static int FixedUpdatesInCurrentFrame => fixed_updates_in_current_frame_;

  /// <summary>
  /// Resets per-render-frame timing. Called at the start/end of the render cycle.
  /// </summary>
  public static void OnRenderFrameStart() {
    int current_frame = Time.frameCount;
    if (current_frame != last_render_frame_count_) {
      if (frame_stopwatch_.IsRunning) {
        frame_stopwatch_.Stop();
      }
      float elapsed = (float)frame_stopwatch_.Elapsed.TotalMilliseconds;
      current_frame_physics_ms_ = elapsed;

      if (elapsed > peak_frame_physics_ms_) {
        peak_frame_physics_ms_ = elapsed;
      }

      // Exponential moving average (alpha = 0.1)
      if (avg_frame_physics_ms_ <= 0.0f) {
        avg_frame_physics_ms_ = elapsed;
      } else {
        avg_frame_physics_ms_ = 0.9f * avg_frame_physics_ms_ + 0.1f * elapsed;
      }

      if (elapsed > BudgetMilliseconds) {
        consecutive_overbudget_frames_++;
      } else {
        consecutive_overbudget_frames_ = Math.Max(0, consecutive_overbudget_frames_ - 1);
      }

      last_render_frame_count_ = current_frame;
      fixed_updates_in_current_frame_ = 0;
      current_level_ = CircuitBreakerLevel.Normal;
      frame_stopwatch_.Reset();
    }
  }

  /// <summary>
  /// Called at the beginning of each FixedUpdate iteration (ObscenelyEarly).
  /// </summary>
  public static void OnPhysicsStepBegin() {
    int current_frame = Time.frameCount;
    if (current_frame != last_render_frame_count_) {
      OnRenderFrameStart();
    }

    fixed_updates_in_current_frame_++;
    if (!frame_stopwatch_.IsRunning) {
      frame_stopwatch_.Start();
    }

    EvaluateCircuitLevel();
  }

  /// <summary>
  /// Called at the conclusion of each FixedUpdate iteration (WaitedForFixedUpdate).
  /// </summary>
  public static void OnPhysicsStepEnd() {
    current_frame_physics_ms_ = (float)frame_stopwatch_.Elapsed.TotalMilliseconds;
    EvaluateCircuitLevel();
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void EvaluateCircuitLevel() {
    if (!EnableCircuitBreaker) {
      current_level_ = CircuitBreakerLevel.Normal;
      return;
    }

    float elapsed = (float)frame_stopwatch_.Elapsed.TotalMilliseconds;
    current_frame_physics_ms_ = elapsed;

    if (consecutive_overbudget_frames_ >= 3 || fixed_updates_in_current_frame_ > MaxAllowedFixedUpdatesPerFrame) {
      current_level_ = CircuitBreakerLevel.HardTripped;
    } else if (elapsed >= BudgetMilliseconds && fixed_updates_in_current_frame_ > 1) {
      current_level_ = CircuitBreakerLevel.ActiveExtrapolated;
    } else if (elapsed >= BudgetMilliseconds * 0.5f) {
      current_level_ = CircuitBreakerLevel.SecondaryThrottled;
    } else {
      current_level_ = CircuitBreakerLevel.Normal;
    }
  }

  /// <summary>
  /// Determines whether secondary vessels should skip heavy physics calculations.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool ShouldThrottleSecondaryPhysics() {
    if (!EnableCircuitBreaker) return false;
    return current_level_ >= CircuitBreakerLevel.SecondaryThrottled;
  }

  /// <summary>
  /// Determines whether the current FixedUpdate iteration should be suppressed
  /// to break out of a physics death spiral.
  /// (Deprecated: In Principia, suppressing the integrator step desynchronizes
  /// the N-body ephemeris. Throttling is applied safely via ShouldThrottleSecondaryPhysics).
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool ShouldSuppressCurrentFixedUpdate() {
    return false;
  }

  public static void ResetTelemetry() {
    current_frame_physics_ms_ = 0.0f;
    peak_frame_physics_ms_ = 0.0f;
    avg_frame_physics_ms_ = 0.0f;
    total_circuit_trips_ = 0;
    total_substeps_suppressed_ = 0;
    consecutive_overbudget_frames_ = 0;
    current_level_ = CircuitBreakerLevel.Normal;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
