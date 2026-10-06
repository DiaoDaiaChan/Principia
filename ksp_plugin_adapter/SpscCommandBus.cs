using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace principia.ksp_plugin_adapter {

public enum CommandType : uint {
  None = 0,
  SetManœuvre = 1,
  DeleteManœuvre = 2,
  ApplyIntrinsicForce = 3,
  ApplyIntrinsicTorque = 4,
  SetBodyFrame = 5,
  VesselTeleport = 6,
  ResetSimulation = 7,
  SetPlottingFrame = 8,
  TriggerPrediction = 9
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CommandPacket {
  public CommandType type;
  public uint vessel_id_high;
  public ulong vessel_id_low;
  public double timestamp;
  public double param0;
  public double param1;
  public double param2;
  public double param3;
  public ulong flags;

  public static CommandPacket Create(CommandType type, double t = 0.0) {
    return new CommandPacket {
      type = type,
      timestamp = t
    };
  }

  public static CommandPacket ForVessel(CommandType type, Guid guid, double t = 0.0) {
    byte[] bytes = guid.ToByteArray();
    uint high = BitConverter.ToUInt32(bytes, 0);
    ulong low = BitConverter.ToUInt64(bytes, 8);
    return new CommandPacket {
      type = type,
      vessel_id_high = high,
      vessel_id_low = low,
      timestamp = t
    };
  }

  public static CommandPacket ForCelestial(CommandType type, int celestial_index, double t = 0.0) {
    return new CommandPacket {
      type = type,
      vessel_id_high = (uint)celestial_index,
      timestamp = t
    };
  }
}

/// <summary>
/// Single-Producer Single-Consumer (SPSC) asynchronous engine command bus (Stage 3 Phase 2).
/// Bridges user-interface and gameplay control commands to the physics execution core.
/// Zero lock contention, zero GC allocation.
/// </summary>
public static class SpscCommandBus {
  public const int RingCapacity = 1024;
  private const int RingMask = RingCapacity - 1;

  // Managed lock-free fallback SPSC queue
  private static readonly CommandPacket[] ring_buffer_ = new CommandPacket[RingCapacity];
  private static long head_ = 0;
  private static long tail_ = 0;

  private static IntPtr native_queue_handle_ = IntPtr.Zero;
  private static readonly object init_lock_ = new object();

  public static long CommandsPostedCount { get; private set; }
  public static long CommandsExecutedCount { get; private set; }

  private static void EnsureInitialized() {
    if (native_queue_handle_ == IntPtr.Zero && Interface.HasCommandBusSupport) {
      lock (init_lock_) {
        if (native_queue_handle_ == IntPtr.Zero && Interface.HasCommandBusSupport) {
          native_queue_handle_ = Interface.CommandBusCreate();
        }
      }
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool TryPostCommand(CommandPacket packet) {
    EnsureInitialized();
    if (native_queue_handle_ != IntPtr.Zero && Interface.HasCommandBusSupport) {
      if (Interface.CommandBusTryPush(native_queue_handle_, ref packet)) {
        CommandsPostedCount++;
        return true;
      }
      return false;
    }

    // Managed SPSC push
    long current_head = Interlocked.Read(ref head_);
    long current_tail = Interlocked.Read(ref tail_);

    if ((current_head - current_tail) >= RingCapacity) {
      return false; // Backpressure / Queue full
    }

    ring_buffer_[current_head & RingMask] = packet;
    Interlocked.Increment(ref head_);
    CommandsPostedCount++;
    return true;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool TryPollCommand(out CommandPacket packet) {
    EnsureInitialized();
    if (native_queue_handle_ != IntPtr.Zero && Interface.HasCommandBusSupport) {
      if (Interface.CommandBusTryPop(native_queue_handle_, out packet)) {
        CommandsExecutedCount++;
        return true;
      }
      packet = default;
      return false;
    }

    // Managed SPSC pop
    long current_tail = Interlocked.Read(ref tail_);
    long current_head = Interlocked.Read(ref head_);

    if (current_tail >= current_head) {
      packet = default;
      return false; // Queue empty
    }

    packet = ring_buffer_[current_tail & RingMask];
    Interlocked.Increment(ref tail_);
    CommandsExecutedCount++;
    return true;
  }

  public static void Reset() {
    lock (init_lock_) {
      if (native_queue_handle_ != IntPtr.Zero && Interface.HasCommandBusSupport) {
        IntPtr handle = native_queue_handle_;
        native_queue_handle_ = IntPtr.Zero;
        Interface.CommandBusDestroy(ref handle);
      }
      Interlocked.Exchange(ref head_, 0);
      Interlocked.Exchange(ref tail_, 0);
    }
  }
}

}
