using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace principia {
namespace ksp_plugin_adapter {

/// <summary>
/// Bit-for-bit blittable struct representing a force applied at a lever arm offset
/// relative to the part centre of mass. Zero heap allocation, packed sequentially.
/// </summary>
internal struct ForceEntry {
  public Vector3d force;
  public Vector3d lever_arm;

  public ForceEntry(Vector3d force, Vector3d lever_arm) {
    this.force = force;
    this.lever_arm = lever_arm;
  }
}

/// <summary>
/// Phase 5 Zero-Allocation Vacuum Force and Aerodynamic Bypass Cache.
/// Eliminates managed heap allocations (List<PartCentredForceHolder>, Dictionary bucket thrashing)
/// and bypasses redundant force collection & P/Invoke queries for stable vacuum vessels.
/// </summary>
internal static class ZeroAllocationForceCache {
  // Pre-allocated flat buffer for multi-point forces (up to 4096 simultaneous forces per frame)
  private const int MaxForcesCapacity = 4096;
  private static readonly ForceEntry[] force_buffer_ = new ForceEntry[MaxForcesCapacity];
  private static int force_count_ = 0;

  // Compact lookup: Maps part_id to (offset, count) within force_buffer_
  // Reusable dictionary with pre-allocated capacity
  private static readonly Dictionary<uint, int> part_force_offsets_ =
      new Dictionary<uint, int>(1024);
  private static readonly Dictionary<uint, int> part_force_counts_ =
      new Dictionary<uint, int>(1024);

  // Cached intrinsic torque and force vectors (reusable, pre-sized)
  private static readonly Dictionary<uint, Vector3d> part_torques_ =
      new Dictionary<uint, Vector3d>(1024);
  private static readonly Dictionary<uint, Vector3d> part_forces_ =
      new Dictionary<uint, Vector3d>(1024);

  // Telemetry metrics
  private static long total_allocations_saved_ = 0;
  private static long total_bytes_saved_ = 0;
  private static long total_vacuum_bypasses_ = 0;
  private static long total_pinvokes_saved_ = 0;

  public static long AllocationsSaved => total_allocations_saved_;
  public static long EstimatedBytesSaved => total_bytes_saved_;
  public static long VacuumBypasses => total_vacuum_bypasses_;
  public static long PInvokesSaved => total_pinvokes_saved_;

  /// <summary>
  /// Resets per-physics-frame force storage without releasing underlying memory.
  /// Zero GC allocation.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void ResetFrame() {
    force_count_ = 0;
    part_force_offsets_.Clear();
    part_force_counts_.Clear();
    part_torques_.Clear();
    part_forces_.Clear();
  }

  /// <summary>
  /// Records that a vessel's forces were completely bypassed due to stable vacuum conditions.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void RecordVacuumBypass(int part_count) {
    total_vacuum_bypasses_++;
    total_allocations_saved_ += part_count;
    // Each part force list typically incurs ~64-128 bytes in List & array backing
    total_bytes_saved_ += part_count * 96L;
  }

  /// <summary>
  /// Records bypassed aerodynamic angular drag P/Invokes.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void RecordAeroPInvokeBypass(int count = 1) {
    total_pinvokes_saved_ += count;
  }

  /// <summary>
  /// Adds an intrinsic torque vector for a part.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void AddTorque(uint part_id, Vector3d torque) {
    if (part_torques_.TryGetValue(part_id, out Vector3d existing)) {
      part_torques_[part_id] = existing + torque;
    } else {
      part_torques_[part_id] = torque;
    }
  }

  /// <summary>
  /// Adds an intrinsic force vector for a part.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void AddForce(uint part_id, Vector3d force) {
    if (part_forces_.TryGetValue(part_id, out Vector3d existing)) {
      part_forces_[part_id] = existing + force;
    } else {
      part_forces_[part_id] = force;
    }
  }

  /// <summary>
  /// Appends a multi-point force at lever arm offset to the flat buffer.
  /// Returns false if buffer capacity is exceeded.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool AppendPointForce(uint part_id, Vector3d force, Vector3d lever_arm) {
    if (force_count_ >= MaxForcesCapacity) {
      return false; // Buffer full fallback
    }

    int entry_idx = force_count_++;
    force_buffer_[entry_idx] = new ForceEntry(force, lever_arm);

    if (part_force_counts_.TryGetValue(part_id, out int count)) {
      part_force_counts_[part_id] = count + 1;
    } else {
      part_force_offsets_[part_id] = entry_idx;
      part_force_counts_[part_id] = 1;
    }

    total_allocations_saved_++;
    total_bytes_saved_ += 48L; // Size of ForceEntry struct + reference overhead
    return true;
  }

  /// <summary>
  /// Tries to query intrinsic torque for a part.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool TryGetTorque(uint part_id, out Vector3d torque) {
    return part_torques_.TryGetValue(part_id, out torque);
  }

  /// <summary>
  /// Tries to query intrinsic force for a part.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool TryGetForce(uint part_id, out Vector3d force) {
    return part_forces_.TryGetValue(part_id, out force);
  }

  /// <summary>
  /// Tries to query point forces range for a part in the flat buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool TryGetPointForces(uint part_id, out int offset, out int count) {
    if (part_force_counts_.TryGetValue(part_id, out count) &&
        part_force_offsets_.TryGetValue(part_id, out offset)) {
      return true;
    }
    offset = 0;
    count = 0;
    return false;
  }

  /// <summary>
  /// Direct accessor for force entry by index.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ref readonly ForceEntry GetForceEntry(int index) {
    return ref force_buffer_[index];
  }

  public static void ResetTelemetry() {
    total_allocations_saved_ = 0;
    total_bytes_saved_ = 0;
    total_vacuum_bypasses_ = 0;
    total_pinvokes_saved_ = 0;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
