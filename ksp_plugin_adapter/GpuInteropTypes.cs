using System;
using System.Runtime.InteropServices;

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

public enum GpuDeviceRole : int {
  Disabled = 0,
  PrimaryRender = 1,     // Primary dGPU / display adapter; throttles compute under render pressure
  DedicatedCompute = 2,  // Dedicated compute accelerator (secondary dGPU / eGPU); never throttles on render pressure
  UmaCompute = 3,        // Integrated GPU (iGPU) with UMA zero-copy shared memory
  Auxiliary = 4          // Auxiliary compute device
}

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
public struct GpuDeviceInfo {
  public int adapter_index;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
  public string description;
  private byte pad0, pad1, pad2, pad3;
  public ulong dedicated_video_memory;
  public ulong shared_system_memory;
  public uint vendor_id;
  public uint device_id;
  [MarshalAs(UnmanagedType.I1)]
  public bool is_integrated;
  [MarshalAs(UnmanagedType.I1)]
  public bool supports_compute;
  private byte pad4, pad5;
  public GpuDeviceRole role;
  public double compute_score;
  public double dynamic_workload_ratio;
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
  // Multi-GPU Extension:
  public double igpu_compute_score;
  public double total_gpu_compute_score;
  public int active_gpu_count;
  [MarshalAs(UnmanagedType.I1)]
  public bool is_heterogeneous_tri_engine;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
  private byte[] padding2;
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
  // Multi-Engine Asymmetric distribution:
  public double dynamic_cpu_ratio;
  public double dynamic_igpu_ratio;
  public double dynamic_dgpu_ratio;
  public double dynamic_aux_ratio;
  public int active_device_count;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
  private byte[] padding2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CpuModuleThreadingStatus {
  public int hardware_concurrency;
  public int vessel_pool_total_threads;
  public int vessel_pool_active_threads;
  public int vessel_pool_pending_tasks;

  public int ephemeris_pacer_active;
  public int prognosticator_active_count;
  public int orbit_analyser_active;
  public int flight_plan_optimizer_active;

  public double current_frame_physics_time_ms;
  public double frame_budget_limit_ms;

  public int tier1_vessel_count;
  public int tier2_vessel_count;
  public int tier3_vessel_count;
  public int deferred_slices_yielded;

  public int l1_celestial_cache_hit_count;
  public int l2_chebyshev_tile_hit_count;

  public double effective_tier2_hz;
  public double effective_tier3_hz;
  public int tier2_stride;
  public int tier3_stride;
  public double budget_headroom_ratio;
  public int l3_ephemeris_ring_hit_count;
  public int padding;

  // Hardware Topology & Safe Quota Telemetry:
  public int detected_l1d_kb;
  public int detected_l2_kb;
  public int detected_l3_mb;
  public int allocated_l2_kb;
  public int allocated_l3_mb;

  // Multi-Level Eviction Spill & DRAM Cascade Telemetry:
  public int l2_to_l3_spill_count;
  public int l3_to_dram_spill_count;
  public int dram_hit_count;

  // Pillar 2: Work-Stealing & Core Affinity Telemetry:
  public int p_core_worker_count;
  public int e_core_worker_count;
  public long work_stolen_count;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct CelestialBodyGpuState {
  public float pos_x;
  public float pos_y;
  public float pos_z;
  public float gravitational_parameter;
  public float vel_x;
  public float vel_y;
  public float vel_z;
  public float padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct ShootingContextGpu {
  public float initial_pos_x;
  public float initial_pos_y;
  public float initial_pos_z;
  public float initial_time;

  public float initial_vel_x;
  public float initial_vel_y;
  public float initial_vel_z;
  public float step_size_seconds;

  public float base_delta_v_x;
  public float base_delta_v_y;
  public float base_delta_v_z;
  public float span_delta_t;

  public float span_delta_v_x;
  public float span_delta_v_y;
  public float span_delta_v_z;
  public float target_periapsis_altitude;

  public uint target_celestial_index;
  public uint celestial_count;
  public uint step_count;
  public uint candidate_count;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
  public CelestialBodyGpuState[] celestials;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OptimalCandidateBurn {
  public uint candidate_index;
  public float delta_t;
  public float delta_v_tangent;
  public float delta_v_normal;
  public float delta_v_binormal;
  public float periapsis_distance;
  public float cost_metric;
  public uint flags;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
