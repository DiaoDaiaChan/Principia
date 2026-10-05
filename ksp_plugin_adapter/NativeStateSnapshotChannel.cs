using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

/// <summary>
/// Continuous State Motion Snapshot (Dense Output slice)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MotionSnapshot {
  public double time_ut;
  public Vector3d position;
  public Vector3d velocity;
  public QuaternionD rotation;
  public Vector3d angular_velocity;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Evaluate(double query_ut, out Vector3d pos, out QuaternionD rot) {
    double dt = query_ut - time_ut;
    // O(1) instantaneous extrapolation (< 5 ns)
    pos = position + dt * velocity;
    if (angular_velocity.sqrMagnitude > 1e-12) {
      double angle_rad = angular_velocity.magnitude * dt;
      rot = rotation * QuaternionD.AngleAxis((float)(angle_rad * 180.0 / Math.PI), angular_velocity.normalized);
    } else {
      rot = rotation;
    }
  }
}

/// <summary>
/// Lock-Free Triple-Buffering State Channel (Phase 2 Decoupling)
/// Decouples Unity's rendering/Precalc timeline from C++ physics integration.
/// Buffer 0/1/2 with atomic CAS exchange.
/// </summary>
public static class NativeStateSnapshotChannel {
  private static readonly Dictionary<string, EntityTripleBuffer> entity_buffers_ =
      new Dictionary<string, EntityTripleBuffer>();
  private static readonly object lock_ = new object();

  public static long SnapshotsPublished { get; private set; }
  public static long SnapshotsConsumed { get; private set; }
  public static int TrackedEntityCount {
    get {
      lock (lock_) {
        return entity_buffers_.Count;
      }
    }
  }

  private class EntityTripleBuffer {
    public readonly MotionSnapshot[] buffers = new MotionSnapshot[3];
    // Atomic state: packed integer [reader, ready, writer]
    private int state_ = 0x000102; // reader=0, ready=1, writer=2

    public void Publish(in MotionSnapshot snapshot) {
      int cur_state = state_;
      int writer = cur_state & 0xFF;
      buffers[writer] = snapshot;

      // Atomic swap: writer becomes ready, old ready becomes next writer
      while (true) {
        int reader = (cur_state >> 16) & 0xFF;
        int ready = (cur_state >> 8) & 0xFF;
        int next_state = (reader << 16) | (writer << 8) | ready;
        int prev = Interlocked.CompareExchange(ref state_, next_state, cur_state);
        if (prev == cur_state) break;
        cur_state = prev;
        writer = cur_state & 0xFF;
      }
      SnapshotsPublished++;
    }

    public bool Sample(double query_ut, out Vector3d pos, out QuaternionD rot) {
      int cur_state = state_;
      while (true) {
        int reader = (cur_state >> 16) & 0xFF;
        int ready = (cur_state >> 8) & 0xFF;
        int writer = cur_state & 0xFF;
        // Swap reader and ready so reader gets newest ready buffer
        int next_state = (ready << 16) | (reader << 8) | writer;
        int prev = Interlocked.CompareExchange(ref state_, next_state, cur_state);
        if (prev == cur_state) {
          buffers[ready].Evaluate(query_ut, out pos, out rot);
          SnapshotsConsumed++;
          return true;
        }
        cur_state = prev;
      }
    }
  }

  public static void PublishSnapshot(string entity_id, in MotionSnapshot snapshot) {
    if (string.IsNullOrEmpty(entity_id)) return;
    EntityTripleBuffer buf;
    lock (lock_) {
      if (!entity_buffers_.TryGetValue(entity_id, out buf)) {
        buf = new EntityTripleBuffer();
        entity_buffers_[entity_id] = buf;
      }
    }
    buf.Publish(snapshot);
  }

  public static bool TrySampleSnapshot(string entity_id, double query_ut, out Vector3d pos, out QuaternionD rot) {
    pos = Vector3d.zero;
    rot = QuaternionD.identity;
    if (string.IsNullOrEmpty(entity_id)) return false;

    EntityTripleBuffer buf;
    lock (lock_) {
      if (!entity_buffers_.TryGetValue(entity_id, out buf)) {
        return false;
      }
    }
    return buf.Sample(query_ut, out pos, out rot);
  }

  public static void Clear() {
    lock (lock_) {
      entity_buffers_.Clear();
    }
  }
}

}
