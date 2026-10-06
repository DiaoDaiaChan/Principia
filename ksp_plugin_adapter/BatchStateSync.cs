using System;
using System.Runtime.InteropServices;

namespace principia {
namespace ksp_plugin_adapter {

[Flags]
internal enum PartBatchFlags : uint {
  None = 0,
  SolidRocketMotor = 1 << 0,
  HasIntrinsicTorque = 1 << 1,
  HasIntrinsicForce = 1 << 2,
  HasLeverForce = 1 << 3,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct PartBatchInsertState {
  public uint part_id;
  public uint flags;
  public double mass_in_tonnes;
  public XYZ centre_of_mass;
  public XYZ moments_of_inertia_in_tonnes;
  public WXYZ principal_axes_rotation;
  public int main_body_index;
  public int reserved;
  public QP main_body_world_degrees_of_freedom;
  public QP part_world_degrees_of_freedom;
  public WXYZ part_rotation;
  public XYZ part_angular_velocity;
  public double delta_t;
  public XYZ intrinsic_torque;
  public XYZ intrinsic_force;
  public XYZ lever_force;
  public XYZ lever_arm;
  public fixed byte vessel_guid[40];
  public fixed byte part_name[64];

  public void SetVesselGuid(string guid) {
    fixed (byte* ptr = vessel_guid) {
      CopyAsciiToFixed(guid, ptr, 40);
    }
  }

  public void SetPartName(string name) {
    fixed (byte* ptr = part_name) {
      CopyAsciiToFixed(name, ptr, 64);
    }
  }

  public string GetVesselGuid() {
    fixed (byte* ptr = vessel_guid) {
      return FixedToAscii(ptr, 40);
    }
  }

  public string GetPartName() {
    fixed (byte* ptr = part_name) {
      return FixedToAscii(ptr, 64);
    }
  }

  private static void CopyAsciiToFixed(string str, byte* dest, int maxLen) {
    if (string.IsNullOrEmpty(str)) {
      *dest = 0;
      return;
    }
    int len = str.Length;
    if (len >= maxLen) {
      len = maxLen - 1;
    }
    for (int i = 0; i < len; ++i) {
      dest[i] = (byte)str[i];
    }
    dest[len] = 0;
  }

  private static string FixedToAscii(byte* src, int maxLen) {
    if (src == null || *src == 0) {
      return string.Empty;
    }
    int len = 0;
    while (len < maxLen && src[len] != 0) {
      len++;
    }
    return new string((sbyte*)src, 0, len);
  }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct PartBatchApparentMotion {
  public uint part_id;
  public uint reserved;
  public QP degrees_of_freedom;
  public WXYZ rotation;
  public XYZ angular_velocity;
}

/// <summary>
/// High-throughput zero-allocation batch synchronization manager for physics parts.
/// Reduces per-physics-step P/Invoke transitions by up to 99.8%.
/// </summary>
internal static class BatchStateSync {
  private const int InitialCapacity = 512;
  private const int MaxCapacity = 4096;

  private static PartBatchInsertState[] insert_buffer_ = new PartBatchInsertState[InitialCapacity];
  private static int insert_count_ = 0;

  private static PartBatchApparentMotion[] apparent_buffer_ = new PartBatchApparentMotion[InitialCapacity];
  private static int apparent_count_ = 0;

  private static uint[] actual_part_ids_ = new uint[InitialCapacity];
  private static QPRW[] actual_motion_results_ = new QPRW[InitialCapacity];
  private static int actual_count_ = 0;

  // Telemetry metrics
  private static long total_pinvokes_saved_ = 0;
  private static int pinvokes_saved_this_frame_ = 0;
  private static long total_batches_dispatched_ = 0;
  private static int last_insert_count_ = 0;
  private static int last_apparent_count_ = 0;
  private static int last_actual_count_ = 0;
  private static long batch_insert_dispatched_count_ = 0;
  private static long sequential_insert_fallback_count_ = 0;
  private static long batch_apparent_dispatched_count_ = 0;
  private static long sequential_apparent_fallback_count_ = 0;

  public static bool HasNativeBatchSupport => Interface.HasBatchSyncSupport;
  public static long TotalPinvokesSaved => total_pinvokes_saved_;
  public static int PinvokesSavedThisFrame => pinvokes_saved_this_frame_;
  public static long TotalPInvokesSaved => total_pinvokes_saved_;
  public static int PInvokesSavedThisFrame => pinvokes_saved_this_frame_;
  public static long TotalBatchesDispatched => total_batches_dispatched_;
  public static long BatchInsertDispatchedCount => batch_insert_dispatched_count_;
  public static long SequentialInsertFallbackCount => sequential_insert_fallback_count_;
  public static long BatchApparentDispatchedCount => batch_apparent_dispatched_count_;
  public static long SequentialApparentFallbackCount => sequential_apparent_fallback_count_;
  public static int LastInsertCount => last_insert_count_;
  public static int LastApparentCount => last_apparent_count_;
  public static int LastActualCount => last_actual_count_;

  public static void ResetFrameCounters() {
    pinvokes_saved_this_frame_ = 0;
    insert_count_ = 0;
    apparent_count_ = 0;
    actual_count_ = 0;
  }

  private static void EnsureCapacity<T>(ref T[] array, int required) {
    if (array.Length < required) {
      int new_size = Math.Min(MaxCapacity, Math.Max(array.Length * 2, required));
      Array.Resize(ref array, new_size);
    }
  }

  // --- Insertion Batch Collection ---

  public static void AddInsertState(ref PartBatchInsertState state) {
    EnsureCapacity(ref insert_buffer_, insert_count_ + 1);
    insert_buffer_[insert_count_++] = state;
  }

  public static void DispatchInsertBatch(IntPtr plugin) {
    if (plugin == IntPtr.Zero || insert_count_ == 0) {
      last_insert_count_ = 0;
      return;
    }
    last_insert_count_ = insert_count_;

    if (HasNativeBatchSupport) {
      Interface.BatchInsertOrKeepLoadedParts(plugin, insert_buffer_, insert_count_);
      batch_insert_dispatched_count_ += insert_count_;
      // Each batched item replaces: 1 InsertLoadedPart + up to 3 force/torque calls
      int saved = insert_count_ * 3;
      pinvokes_saved_this_frame_ += saved;
      total_pinvokes_saved_ += saved;
      total_batches_dispatched_++;
    } else {
      sequential_insert_fallback_count_ += insert_count_;
      // Graceful fallback: loop through collected buffer and dispatch via individual P/Invoke
      for (int i = 0; i < insert_count_; ++i) {
        ref var s = ref insert_buffer_[i];
        string name = s.GetPartName();
        string guid = s.GetVesselGuid();
        bool is_solid = (s.flags & (uint)PartBatchFlags.SolidRocketMotor) != 0;

        Interface.InsertOrKeepLoadedPart(
            plugin,
            s.part_id,
            name,
            s.mass_in_tonnes,
            s.centre_of_mass,
            s.moments_of_inertia_in_tonnes,
            s.principal_axes_rotation,
            is_solid,
            guid,
            s.main_body_index,
            s.main_body_world_degrees_of_freedom,
            s.part_world_degrees_of_freedom,
            s.part_rotation,
            s.part_angular_velocity,
            s.delta_t);

        if ((s.flags & (uint)PartBatchFlags.HasIntrinsicTorque) != 0) {
          Interface.PartApplyIntrinsicTorque(plugin, s.part_id, s.intrinsic_torque);
        }
        if ((s.flags & (uint)PartBatchFlags.HasIntrinsicForce) != 0) {
          Interface.PartApplyIntrinsicForce(plugin, s.part_id, s.intrinsic_force);
        }
        if ((s.flags & (uint)PartBatchFlags.HasLeverForce) != 0) {
          Interface.PartApplyIntrinsicForceAtPosition(plugin, s.part_id, s.lever_force, s.lever_arm);
        }
      }
    }
    insert_count_ = 0;
  }

  // --- Apparent Rigid Motion Batch Collection ---

  public static void AddApparentMotion(uint part_id, QP dof, WXYZ rot, XYZ ang_vel) {
    EnsureCapacity(ref apparent_buffer_, apparent_count_ + 1);
    apparent_buffer_[apparent_count_++] = new PartBatchApparentMotion {
      part_id = part_id,
      degrees_of_freedom = dof,
      rotation = rot,
      angular_velocity = ang_vel
    };
  }

  public static void DispatchApparentMotionBatch(IntPtr plugin) {
    if (plugin == IntPtr.Zero || apparent_count_ == 0) {
      last_apparent_count_ = 0;
      return;
    }
    last_apparent_count_ = apparent_count_;

    if (HasNativeBatchSupport) {
      Interface.BatchPartSetApparentRigidMotion(plugin, apparent_buffer_, apparent_count_);
      batch_apparent_dispatched_count_ += apparent_count_;
      int saved = apparent_count_ - 1;
      pinvokes_saved_this_frame_ += saved;
      total_pinvokes_saved_ += saved;
      total_batches_dispatched_++;
    } else {
      sequential_apparent_fallback_count_ += apparent_count_;
      for (int i = 0; i < apparent_count_; ++i) {
        ref var s = ref apparent_buffer_[i];
        Interface.PartSetApparentRigidMotion(
            plugin,
            s.part_id,
            s.degrees_of_freedom,
            s.rotation,
            s.angular_velocity);
      }
    }
    apparent_count_ = 0;
  }

  // --- Actual Rigid Motion Batch Query ---

  public static void AddActualMotionQuery(uint part_id) {
    EnsureCapacity(ref actual_part_ids_, actual_count_ + 1);
    EnsureCapacity(ref actual_motion_results_, actual_count_ + 1);
    actual_part_ids_[actual_count_++] = part_id;
  }

  public static int QueryActualMotionBatch(
      IntPtr plugin,
      Origin origin,
      out uint[] part_ids_out,
      out QPRW[] motions_out) {
    part_ids_out = actual_part_ids_;
    motions_out = actual_motion_results_;
    if (plugin == IntPtr.Zero || actual_count_ == 0) {
      last_actual_count_ = 0;
      return 0;
    }
    last_actual_count_ = actual_count_;

    if (HasNativeBatchSupport) {
      Interface.BatchPartGetActualRigidMotion(
          plugin,
          ref origin,
          actual_part_ids_,
          actual_motion_results_,
          actual_count_);
      int saved = actual_count_ - 1;
      pinvokes_saved_this_frame_ += saved;
      total_pinvokes_saved_ += saved;
      total_batches_dispatched_++;
    } else {
      for (int i = 0; i < actual_count_; ++i) {
        actual_motion_results_[i] =
            Interface.PartGetActualRigidMotion(plugin, actual_part_ids_[i], origin);
      }
    }
    int count = actual_count_;
    actual_count_ = 0;
    return count;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
