using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace principia {
namespace ksp_plugin_adapter {

internal static class GpuVirtualProjection {
  // Cached Shader Property IDs for zero-string overhead (lazily initialized for offline safety)
  private static int prop_world_offset_ = 0;
  private static int prop_virtual_displacement_ = 0;
  private static int prop_scale_factor_ = 0;
  private static int prop_emission_multiplier_ = 0;
  private static bool properties_initialized_ = false;

  // Reusable MaterialPropertyBlock pool for zero-allocation rendering updates
  private static MaterialPropertyBlock shared_property_block_ = null;

  // Telemetry metrics
  private static long total_projections_applied_ = 0;
  private static long total_displacements_computed_ = 0;
  private static double max_displacement_magnitude_ = 0.0;
  private static Vector3d last_drift_offset_ = Vector3d.zero;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void EnsurePropertiesInitialized() {
    if (!properties_initialized_) {
      try {
        prop_world_offset_ = Shader.PropertyToID("_WorldOffset");
        prop_virtual_displacement_ = Shader.PropertyToID("_VirtualDisplacement");
        prop_scale_factor_ = Shader.PropertyToID("_ScaleFactor");
        prop_emission_multiplier_ = Shader.PropertyToID("_EmissionMultiplier");
        properties_initialized_ = true;
      } catch {
        // Fallback or uninitialized native graphics subsystem
      }
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static MaterialPropertyBlock GetSharedPropertyBlock() {
    if (shared_property_block_ == null) {
      try {
        shared_property_block_ = new MaterialPropertyBlock();
      } catch {
        return null;
      }
    }
    return shared_property_block_;
  }

  /// <summary>
  /// Computes the exact sub-frame virtual displacement between a celestial body's
  /// throttled Unity GameObject transform position and its continuous polynomial
  /// physical position sampled from the TripleBufferStateBus.
  /// </summary>
  public static Vector3d ComputeCelestialVirtualDisplacement(CelestialBody body, double universal_time) {
    if (body == null) {
      return Vector3d.zero;
    }

    int body_index = body.flightGlobalsIndex;
    if (TripleBufferStateBus.SampleBodySlice(
            body_index,
            universal_time,
            out Vector3d physical_pos,
            out _,
            out _)) {
      // Delta from Unity's throttled GameObject position to the true continuous position
      Vector3d delta = physical_pos - body.position;
      double mag = delta.magnitude;
      if (mag > max_displacement_magnitude_) {
        max_displacement_magnitude_ = mag;
      }
      total_displacements_computed_++;
      return delta;
    }

    return Vector3d.zero;
  }

  /// <summary>
  /// Applies GPU virtual projection parameters to a Material directly.
  /// Zero-allocation, thread-safe on main render thread.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void ApplyToMaterial(Material material,
                                     Vector3d world_offset,
                                     Vector3d virtual_displacement,
                                     float scale_factor = 1.0f,
                                     float emission_multiplier = 1.0f) {
    if (material == null) return;
    EnsurePropertiesInitialized();

    material.SetVector(prop_world_offset_, (Vector3)world_offset);
    material.SetVector(prop_virtual_displacement_, (Vector3)virtual_displacement);
    material.SetFloat(prop_scale_factor_, scale_factor);
    material.SetFloat(prop_emission_multiplier_, emission_multiplier);

    total_projections_applied_++;
  }

  /// <summary>
  /// Applies GPU virtual projection parameters to a Renderer via MaterialPropertyBlock
  /// without dirtying the shared material instance.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void ApplyToRenderer(Renderer renderer,
                                     Vector3d world_offset,
                                     Vector3d virtual_displacement,
                                     float scale_factor = 1.0f,
                                     float emission_multiplier = 1.0f) {
    if (renderer == null) return;
    EnsurePropertiesInitialized();

    var block = GetSharedPropertyBlock();
    if (block == null) return;
    renderer.GetPropertyBlock(block);

    block.SetVector(prop_world_offset_, (Vector3)world_offset);
    block.SetVector(prop_virtual_displacement_, (Vector3)virtual_displacement);
    block.SetFloat(prop_scale_factor_, scale_factor);
    block.SetFloat(prop_emission_multiplier_, emission_multiplier);

    renderer.SetPropertyBlock(block);
    total_projections_applied_++;
  }

  /// <summary>
  /// Synchronizes Krakensbane drift and FloatingOrigin offsets from DualClockBridge
  /// to the GPU trajectory rendering pipeline.
  /// </summary>
  public static void SynchronizeTrajectoryProjection(Material trajectory_material, Vector3d custom_offset) {
    if (trajectory_material == null) return;

    Vector3d drift = DualClockBridge.CumulativeDrift;
    last_drift_offset_ = drift;
    Vector3d combined_offset = custom_offset + drift;

    trajectory_material.SetVector(prop_world_offset_, (Vector3)combined_offset);
    trajectory_material.SetVector(prop_virtual_displacement_, Vector3.zero);
    trajectory_material.SetFloat(prop_emission_multiplier_, 1.2f);
    total_projections_applied_++;
  }

  // Telemetry getters
  public static long TotalProjectionsApplied => total_projections_applied_;
  public static long TotalDisplacementsComputed => total_displacements_computed_;
  public static double MaxDisplacementMagnitude => max_displacement_magnitude_;
  public static Vector3d LastDriftOffset => last_drift_offset_;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
