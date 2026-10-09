using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace principia {
namespace ksp_plugin_adapter {

public enum DmaTransferMode : int {
  DirectDma = 0,       // ReBAR + streaming non-temporal stores + Async DMA readback
  StagingFallback = 1, // Traditional staging host copy + synchronous GetData
  PureCpu = 2          // Disabled / CPU native
}

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
public struct GpuAdapterInfo {
  public int adapter_index;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
  public string description;
  public ulong dedicated_video_memory;
  public ulong shared_system_memory;
  public uint vendor_id;
  public uint device_id;
  [MarshalAs(UnmanagedType.I1)]
  public bool is_integrated;
  [MarshalAs(UnmanagedType.I1)]
  public bool supports_compute;
}

[StructLayout(LayoutKind.Sequential, Size = 256)]
public struct ChebyshevSegmentData {
  public float t_min;
  public float t_max;
  public uint degree;
  public float padding;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 51)]
  public float[] coeffs;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
  public float[] reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DoubleSingle {
  public float hi;
  public float lo;

  public static DoubleSingle FromDouble(double v) {
    float hi = (float)v;
    float lo = (float)(v - (double)hi);
    return new DoubleSingle { hi = hi, lo = lo };
  }

  public double ToDouble() => (double)hi + (double)lo;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DsVec3 {
  public DoubleSingle x;
  public DoubleSingle y;
  public DoubleSingle z;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OrbitalState64 {
  public DsVec3 position;
  public DsVec3 velocity;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct ShootingContextGpu {
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
  public float[] initial_pos;
  public float initial_time;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
  public float[] initial_vel;
  public float step_size_seconds;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
  public float[] base_delta_v;
  public float span_delta_t;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
  public float[] span_delta_v;
  public float target_periapsis_altitude;
  public uint target_celestial_index;
  public uint celestial_count;
  public uint step_count;
  public uint candidate_count;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
  public CelestialBodyGpuState[] celestials;
}

} // namespace ksp_plugin_adapter
} // namespace principia
