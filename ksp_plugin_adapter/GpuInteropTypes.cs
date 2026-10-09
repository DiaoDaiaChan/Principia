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

public enum KspSceneType : int {
  FlightAtmospheric = 0,   // Atmospheric launch/flight (high rendering load & part count)
  FlightOrbital     = 1,   // Orbital vacuum cruise (stable)
  TrackingStation   = 2,   // Tracking station (low rendering, high background physics)
  SpaceCenter       = 3,   // Space center KSC view
  MapView           = 4,   // Map view (trajectory rendering dominant)
  Other             = 5
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct HardwareProfile {
  public double cpu_fp64_gflops_score;
  public double primary_gpu_compute_score;
  public double dma_bandwidth_gbps;
  public int adapter_count;
  public int recommended_compute_adapter;
  [MarshalAs(UnmanagedType.I1)]
  public bool has_integrated_gpu;
  [MarshalAs(UnmanagedType.I1)]
  public bool has_discrete_gpu;
  [MarshalAs(UnmanagedType.I1)]
  public bool is_calibrated;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
  private byte[] padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct WorkloadBalanceState {
  public double dynamic_gpu_ratio;
  public double smoothed_render_ms;
  public double smoothed_physics_ms;
  public int current_scene;
  [MarshalAs(UnmanagedType.I1)]
  public bool is_master_bypassed;
  [MarshalAs(UnmanagedType.I1)]
  public bool is_render_pressure_active;
  [MarshalAs(UnmanagedType.I1)]
  public bool is_manual_override;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
  private byte[] padding;
}

} // namespace ksp_plugin_adapter
} // namespace principia
