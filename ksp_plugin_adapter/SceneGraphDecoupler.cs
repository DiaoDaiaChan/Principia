using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace principia {
namespace ksp_plugin_adapter {

internal enum CelestialSaliencyTier {
  Tier0_LocalPhysics = 0,   // Active SOI, camera target, or close proximity (full fidelity)
  Tier1_CloseSystem = 1,    // Immediate parent/child of active body, within medium range (~10 Hz)
  Tier2_DistantMajor = 2,   // Other major planetary bodies (~1 Hz)
  Tier3_BackgroundMinor = 3 // Distant moons, asteroids, comets (~0.2 Hz)
}

internal static class SceneGraphDecoupler {
  // Configurable update intervals in seconds per saliency tier
  public static float tier0_interval = 0.0f;     // Every frame
  public static float tier1_interval = 0.100f;   // 10 Hz
  public static float tier2_interval = 1.000f;   // 1 Hz
  public static float tier3_interval = 5.000f;   // 0.2 Hz

  // Proximity threshold: within 10 body radii, PQS surface terrain LOD is sensitive
  public const double PqsProximityRadiusMultiplier = 10.0;

  // Tracking state per celestial body index
  private static float[] last_transform_update_time_ = new float[64];
  private static int[] last_transform_update_frame_ = new int[64];
  private static CelestialSaliencyTier[] body_tiers_ = new CelestialSaliencyTier[64];

  // Telemetry metrics
  private static long total_transform_evaluations_ = 0;
  private static long transform_updates_performed_ = 0;
  private static long transform_updates_throttled_ = 0;
  private static long pqs_invalidation_sweeps_avoided_ = 0;

  // Tier counts recorded in last evaluation pass
  private static int count_tier0_ = 0;
  private static int count_tier1_ = 0;
  private static int count_tier2_ = 0;
  private static int count_tier3_ = 0;

  private static void EnsureCapacity(int index) {
    if (index >= last_transform_update_time_.Length) {
      int new_capacity = Math.Max(index + 1, last_transform_update_time_.Length * 2);
      Array.Resize(ref last_transform_update_time_, new_capacity);
      Array.Resize(ref last_transform_update_frame_, new_capacity);
      Array.Resize(ref body_tiers_, new_capacity);
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static CelestialSaliencyTier ClassifyBody(CelestialBody body, Vector3d camera_position) {
    if (body == null) {
      return CelestialSaliencyTier.Tier3_BackgroundMinor;
    }

    // 1. Priority bodies: Active vessel SOI or PlanetariumCamera target
    CelestialBody active_main = FlightGlobals.ActiveVessel?.mainBody;
    if (body == active_main) {
      return CelestialSaliencyTier.Tier0_LocalPhysics;
    }

    CelestialBody camera_target = MapView.MapIsEnabled
        ? PlanetariumCamera.fetch?.target?.celestialBody
        : null;
    if (body == camera_target) {
      return CelestialSaliencyTier.Tier0_LocalPhysics;
    }

    // 2. Camera proximity: if camera is within 10 radii, terrain PQS may be active
    double distance_to_camera = (body.position - camera_position).magnitude;
    if (distance_to_camera < body.Radius * PqsProximityRadiusMultiplier) {
      return CelestialSaliencyTier.Tier0_LocalPhysics;
    }

    // 3. Close system bodies (Sun or direct parent of active main body)
    if (body == Planetarium.fetch?.Sun ||
        (active_main != null && (body == active_main.orbit?.referenceBody || body.orbit?.referenceBody == active_main))) {
      return CelestialSaliencyTier.Tier1_CloseSystem;
    }

    // 4. Major bodies vs minor bodies (radius threshold > 50 km)
    if (body.Radius > 50000.0) {
      return CelestialSaliencyTier.Tier2_DistantMajor;
    }

    return CelestialSaliencyTier.Tier3_BackgroundMinor;
  }

  /// <summary>
  /// Evaluates whether the Unity GameObject transform (bodyTransform.rotation / position)
  /// should be written this frame, or throttled to avoid triggering expensive PQS quadtree
  /// dirty invalidation and scene graph propagation sweeps.
  /// </summary>
  public static bool ShouldUpdateTransform(CelestialBody body,
                                           Vector3d camera_position,
                                           float current_realtime,
                                           int current_frame) {
    total_transform_evaluations_++;

    if (body == null) {
      return false;
    }

    int index = body.flightGlobalsIndex;
    if (index >= 0) {
      EnsureCapacity(index);
    }

    CelestialSaliencyTier tier = ClassifyBody(body, camera_position);
    if (index >= 0) {
      body_tiers_[index] = tier;
    }

    // Tier 0 bodies always update every frame
    if (tier == CelestialSaliencyTier.Tier0_LocalPhysics) {
      if (index >= 0) {
        last_transform_update_time_[index] = current_realtime;
        last_transform_update_frame_[index] = current_frame;
      }
      transform_updates_performed_++;
      return true;
    }

    float interval;
    switch (tier) {
      case CelestialSaliencyTier.Tier1_CloseSystem:
        interval = tier1_interval;
        break;
      case CelestialSaliencyTier.Tier2_DistantMajor:
        interval = tier2_interval;
        break;
      default:
        interval = tier3_interval;
        break;
    }

    if (index >= 0) {
      // Golden ratio phase offset to prevent synchronized update spikes
      if (last_transform_update_time_[index] == 0f) {
        const double golden_ratio = 0.618033988749895;
        float initial_phase = (float)(((index * golden_ratio) % 1.0) * interval);
        last_transform_update_time_[index] = current_realtime - initial_phase;
      }

      float elapsed = current_realtime - last_transform_update_time_[index];
      if (elapsed >= interval) {
        last_transform_update_time_[index] = current_realtime;
        last_transform_update_frame_[index] = current_frame;
        transform_updates_performed_++;
        return true;
      }
    }

    // Throttled: scene graph transform write avoided!
    transform_updates_throttled_++;
    if (body.pqsController != null && body.pqsController.isAlive) {
      pqs_invalidation_sweeps_avoided_++;
    }
    return false;
  }

  public static void RecordPassTierCounts(IList<CelestialBody> bodies, Vector3d camera_position) {
    if (bodies == null) return;
    int t0 = 0, t1 = 0, t2 = 0, t3 = 0;
    for (int i = 0; i < bodies.Count; ++i) {
      var body = bodies[i];
      if (body == null) continue;
      switch (ClassifyBody(body, camera_position)) {
        case CelestialSaliencyTier.Tier0_LocalPhysics:
          t0++;
          break;
        case CelestialSaliencyTier.Tier1_CloseSystem:
          t1++;
          break;
        case CelestialSaliencyTier.Tier2_DistantMajor:
          t2++;
          break;
        case CelestialSaliencyTier.Tier3_BackgroundMinor:
          t3++;
          break;
      }
    }
    count_tier0_ = t0;
    count_tier1_ = t1;
    count_tier2_ = t2;
    count_tier3_ = t3;
  }

  // Telemetry getters
  public static long TotalEvaluations => total_transform_evaluations_;
  public static long UpdatesPerformed => transform_updates_performed_;
  public static long UpdatesThrottled => transform_updates_throttled_;
  public static long PqsSweepsAvoided => pqs_invalidation_sweeps_avoided_;
  public static double ThrottleRatio => total_transform_evaluations_ > 0
      ? (double)transform_updates_throttled_ / total_transform_evaluations_
      : 0.0;

  public static int Tier0Count => count_tier0_;
  public static int Tier1Count => count_tier1_;
  public static int Tier2Count => count_tier2_;
  public static int Tier3Count => count_tier3_;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
