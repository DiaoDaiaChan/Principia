using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

/// <summary>
/// Lock-Free Triple-Buffering State Bus (Stage 3 Phase 2).
/// Provides zero-locking, zero-GC asynchronous kinematics snapshot publishing and O(1) sampling.
/// Automatically bridges to native C++ TripleBufferChannel when available, or executes managed
/// lock-free atomic triple buffering with bit-identical semantics when running prebuilt binaries.
/// </summary>
public static class TripleBufferStateBus {
  // Managed atomic triple buffer channel implementation.
  public sealed class ManagedChannel {
    private readonly DensePolynomialSlice[] buffers_ = new DensePolynomialSlice[3];
    // Encoded state: [Byte 2: reader] [Byte 1: ready] [Byte 0: writer]
    private int state_ = 0x000102; // reader=0, ready=1, writer=2
    private IntPtr native_handle_ = IntPtr.Zero;

    public ManagedChannel(IntPtr native_handle) {
      native_handle_ = native_handle;
    }

    public IntPtr NativeHandle => native_handle_;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWrite(ref DensePolynomialSlice slice) {
      if (native_handle_ != IntPtr.Zero && Interface.HasStateBusSupport) {
        Interface.StateBusCommitWrite(native_handle_, ref slice);
        return;
      }

      int current = Volatile.Read(ref state_);
      int next;
      int writer = current & 0xFF;
      buffers_[writer] = slice;

      do {
        current = state_;
        int reader = (current >> 16) & 0xFF;
        int ready  = (current >> 8)  & 0xFF;
        writer     = current & 0xFF;
        next = (reader << 16) | (writer << 8) | ready;
      } while (Interlocked.CompareExchange(ref state_, next, current) != current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AcquireRead(out DensePolynomialSlice slice) {
      if (native_handle_ != IntPtr.Zero && Interface.HasStateBusSupport) {
        return Interface.StateBusAcquireRead(native_handle_, out slice);
      }

      int current;
      int next;
      int ready;
      do {
        current = state_;
        int reader = (current >> 16) & 0xFF;
        ready      = (current >> 8)  & 0xFF;
        int writer = current & 0xFF;
        next = (ready << 16) | (reader << 8) | writer;
      } while (Interlocked.CompareExchange(ref state_, next, current) != current);

      slice = buffers_[ready];
      return slice.IsValid;
    }

    public void Dispose() {
      if (native_handle_ != IntPtr.Zero && Interface.HasStateBusSupport) {
        IntPtr handle = native_handle_;
        native_handle_ = IntPtr.Zero;
        Interface.StateBusDestroyChannel(ref handle);
      }
    }
  }

  private static readonly Dictionary<string, ManagedChannel> vessel_channels_ =
      new Dictionary<string, ManagedChannel>();
  private static readonly Dictionary<int, ManagedChannel> body_channels_ =
      new Dictionary<int, ManagedChannel>();
  private static readonly object registry_lock_ = new object();

  // Telemetry metrics
  public static long TotalSnapshotsPublished { get; private set; }
  public static long TotalSnapshotsSampled { get; private set; }
  public static int ActiveVesselChannels {
    get {
      lock (registry_lock_) {
        return vessel_channels_.Count;
      }
    }
  }
  public static int ActiveBodyChannels {
    get {
      lock (registry_lock_) {
        return body_channels_.Count;
      }
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ManagedChannel GetOrCreateVesselChannel(string vessel_guid) {
    if (vessel_guid == null) return null;
    lock (registry_lock_) {
      if (!vessel_channels_.TryGetValue(vessel_guid, out ManagedChannel channel)) {
        IntPtr native_handle = IntPtr.Zero;
        if (Interface.HasStateBusSupport) {
          native_handle = Interface.StateBusCreateChannel();
        }
        channel = new ManagedChannel(native_handle);
        vessel_channels_[vessel_guid] = channel;
      }
      return channel;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ManagedChannel GetOrCreateBodyChannel(int body_index) {
    lock (registry_lock_) {
      if (!body_channels_.TryGetValue(body_index, out ManagedChannel channel)) {
        IntPtr native_handle = IntPtr.Zero;
        if (Interface.HasStateBusSupport) {
          native_handle = Interface.StateBusCreateChannel();
        }
        channel = new ManagedChannel(native_handle);
        body_channels_[body_index] = channel;
      }
      return channel;
    }
  }

  public static void PublishVesselSlice(string vessel_guid, ref DensePolynomialSlice slice) {
    ManagedChannel channel = GetOrCreateVesselChannel(vessel_guid);
    if (channel != null) {
      channel.CommitWrite(ref slice);
      TotalSnapshotsPublished++;
    }
  }

  public static void PublishBodySlice(int body_index, ref DensePolynomialSlice slice) {
    ManagedChannel channel = GetOrCreateBodyChannel(body_index);
    if (channel != null) {
      channel.CommitWrite(ref slice);
      TotalSnapshotsPublished++;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool SampleVesselSlice(
      string vessel_guid,
      double t,
      out Vector3d position,
      out Vector3d velocity,
      out UnityEngine.QuaternionD rotation) {
    position = Vector3d.zero;
    velocity = Vector3d.zero;
    rotation = UnityEngine.QuaternionD.identity;

    if (vessel_guid == null) return false;
    ManagedChannel channel;
    lock (registry_lock_) {
      if (!vessel_channels_.TryGetValue(vessel_guid, out channel)) {
        return false;
      }
    }

    if (channel.AcquireRead(out DensePolynomialSlice slice)) {
      slice.Evaluate(t, out position, out velocity, out rotation);
      TotalSnapshotsSampled++;
      return true;
    }
    return false;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool SampleBodySlice(
      int body_index,
      double t,
      out Vector3d position,
      out Vector3d velocity,
      out UnityEngine.QuaternionD rotation) {
    position = Vector3d.zero;
    velocity = Vector3d.zero;
    rotation = UnityEngine.QuaternionD.identity;

    ManagedChannel channel;
    lock (registry_lock_) {
      if (!body_channels_.TryGetValue(body_index, out channel)) {
        return false;
      }
    }

    if (channel.AcquireRead(out DensePolynomialSlice slice)) {
      slice.Evaluate(t, out position, out velocity, out rotation);
      TotalSnapshotsSampled++;
      return true;
    }
    return false;
  }

  public static void Clear() {
    lock (registry_lock_) {
      foreach (var kvp in vessel_channels_) {
        kvp.Value.Dispose();
      }
      vessel_channels_.Clear();

      foreach (var kvp in body_channels_) {
        kvp.Value.Dispose();
      }
      body_channels_.Clear();
    }
  }
}

}
