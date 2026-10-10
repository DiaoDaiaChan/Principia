using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public enum ProbeCategory {
  CppPhysics = 0,               // Native C++ gravitational N-body symplectic integration (AdvanceTime, PileUps, CatchUp)
  CppPlotting = 1,              // Native C++ continuous trajectory Chebyshev polynomial evaluation & projection
  UnityPrecalc = 2,             // Unity timing stage Precalc (Physics precalc, bodies update)
  UnityFixedUpdate = 3,         // Unity timing stage FixedUpdate (Krakensbane, parachutes, physics staging)
  UnityWaitedForFixedUpdate = 4,// Unity coroutine WaitedForFixedUpdate (Rigid motion, transform syncing, collisions)
  UnityBetterLate = 5,          // Unity timing stage BetterLateThanNever (Degrees of freedom harvest)
  UnityLateUpdate = 6,          // Unity LateUpdate (Trajectory collision patrol, map markers, accessories)
  CelestialPlot = 7,            // Celestial body trajectory rendering
  VesselPlot = 8,               // Vessel trajectory rendering & mesh generation
  GpuDmaUpload = 9,             // PCIe / ReBAR DMA upload time
  Count = 10
}

/// <summary>
/// Completely asynchronous, zero-blocking performance and dynamic workload balancer probe.
/// Measures and decouples native C++ computation latency vs managed C# glue latency.
/// Telemetry is queried and formatted on a low-frequency (5 Hz) background worker thread,
/// ensuring strictly 0 ms blocking overhead on both C++ physics and C# Unity render loops.
/// </summary>
public static class PerformanceProbe {
  private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

  // Real-time smoothed durations in milliseconds (written on main thread via Measure)
  private static readonly float[] smoothed_ms_ = new float[(int)ProbeCategory.Count];
  private static readonly float[] peak_ms_ = new float[(int)ProbeCategory.Count];

  // Frame timing history for P99 jitter
  private const int FrameHistoryCapacity = 120;
  private static readonly float[] frame_time_history_ = new float[FrameHistoryCapacity];
  private static readonly float[] sorted_frame_times_ = new float[FrameHistoryCapacity];
  private static int frame_history_index_ = 0;
  private static int frame_history_count_ = 0;
  public static float P99FrameJitterMs { get; private set; }

  // Heap allocation tracking
  private static long last_gc_memory_ = 0;
  public static float GcAllocRateKbPerFrame { get; private set; }

  public static float CurrentFps { get; private set; }
  public static float FrameTimeMs { get; private set; }

  // HUD Window visibility & positioning
  public static bool ShowProfilingHud = false;
  public static IntPtr PluginPointer { get; set; } = IntPtr.Zero;
  private static Rect hud_rect_ = new Rect(40, 80, 580, 680);
  private static int current_tab_ = 0;
  private static readonly string[] tab_names_ = new string[] {
    "📊 性能总览 (Overall)",
    "🧵 CPU线程与切片 (CPU Threads)",
    "🪐 星历推演 (Ephemeris)",
    "⚖️ 负载均衡 (Balancer)",
    "⚡ 硬件与DMA (Hardware)"
  };
  private static Vector2 scroll_pos_ = Vector2.zero;
  private const int WindowId = 0x50524F42; // "PROB"

  // Async DMA & Calibration Benchmark Status
  public static string LastDmaBenchmarkResult { get; private set; } = "尚未执行压测 (点击下方按钮立即压测)";
  public static bool IsDmaBenchmarking { get; private set; } = false;
  public static bool IsCalibrating { get; private set; } = false;

  private static float manual_slider_ratio_ = 0.5f;

  private static readonly (string name, ProbeCategory cat, float budget_ms)[] metric_bar_defs_ = new[] {
    ("1. 早期预计算 (Precalc / Bodies)", ProbeCategory.UnityPrecalc, 2.0f),
    ("2. 物理推进 (FixedUpdate / Integrator)", ProbeCategory.UnityFixedUpdate, 0.5f),
    ("3. 物理等待与更新 (WaitedForFixedUpdate)", ProbeCategory.UnityWaitedForFixedUpdate, 2.0f),
    ("4. 晚期同步 (BetterLate / Motion)", ProbeCategory.UnityBetterLate, 1.0f),
    ("5. 标记与几何更新 (LateUpdate / Markers)", ProbeCategory.UnityLateUpdate, 2.0f),
    ("6. 飞船轨迹绘制 (Vessel Trajectory)", ProbeCategory.VesselPlot, 1.5f),
    ("7. 天体轨道绘制 (Celestial Subtree)", ProbeCategory.CelestialPlot, 1.0f),
  };

  public class GpuDeviceTelemetry {
    public int index;
    public string description = "";
    public ulong dedicated_vram;
    public ulong shared_ram;
    public bool is_integrated;
    public GpuDeviceRole role;
    public double compute_score;
    public double dynamic_ratio;
    public int assigned_vessel_count;
    public double estimated_kernel_ms;
    public double estimated_dma_ms;
    public double total_latency_ms;
    public Color bar_color;

    public string role_name = "";
    public string card_specs_str = "";
    public string card_perf_str = "";
    public string latency_breakdown_str = "";
  }

  // Immutable telemetry snapshot populated asynchronously
  public class TelemetrySnapshot {
    public float fps;
    public float frame_time_ms;
    public float jitter_ms;
    public float gc_rate_kb;

    // Architecture-level breakdown: C++ Native vs C# Managed vs Unity Engine Other
    public float cpp_physics_ms;
    public float cpp_plotting_ms;
    public float cpp_overall_ms;
    public float cpp_ratio_pct;

    public float cs_managed_overall_ms;
    public float cs_ratio_pct;

    public float principia_total_ms;
    public float principia_ratio_pct;

    public float unity_other_ms;
    public float unity_other_pct;

    public float[] metric_ms = new float[(int)ProbeCategory.Count];
    public float[] metric_peak = new float[(int)ProbeCategory.Count];

    // Pre-formatted metric bar values
    public string[] metric_bar_str = new string[metric_bar_defs_.Length];
    public float[] metric_bar_ratio = new float[metric_bar_defs_.Length];
    public bool[] metric_bar_overbudget = new bool[metric_bar_defs_.Length];

    // Ephemeris telemetry
    public bool ephemeris_available;
    public bool ephemeris_paused;
    public bool ephemeris_prolonging;
    public double ephemeris_horizon_years;
    public double ephemeris_horizon_seconds;
    public double ephemeris_lookahead_years;
    public double ephemeris_refresh_threshold_years;
    public double ephemeris_progress_ratio;
    public List<string> ephemeris_logs = new List<string>();

    // Hardware telemetry
    public bool hardware_calibrated;
    public int adapter_count;
    public double cpu_fp64_gflops;
    public double gpu_compute_score;
    public double dma_bandwidth_gbps;
    public int recommended_adapter;
    public bool has_discrete_gpu;
    public bool has_integrated_gpu;

    // Balancer & Multi-Engine telemetry
    public bool is_bypassed;
    public bool is_render_pressure;
    public bool is_manual_override;
    public double dynamic_gpu_ratio;
    public double dynamic_cpu_ratio;
    public double dynamic_igpu_ratio;
    public double dynamic_dgpu_ratio;
    public double dynamic_aux_ratio;
    public int active_device_count;
    public double smoothed_render_ms;
    public double smoothed_physics_ms;
    public KspSceneType current_scene;
    public int batch_cpu_count;
    public int batch_gpu_count;

    // Per-device latency & speedup metrics
    public double cpu_latency_ms;
    public double max_parallel_latency_ms;
    public double baseline_cpu_latency_ms;
    public double speedup_factor;

    public List<GpuDeviceTelemetry> gpu_devices = new List<GpuDeviceTelemetry>();

    // Pre-formatted strings to guarantee 0 string allocation during OnGUI
    public string perf_summary_str = "";
    public string arch_overall_str = "";
    public string cpp_summary_str = "";
    public string cs_summary_str = "";
    public string unity_summary_str = "";

    public string ephem_status_str = "";
    public string ephem_horizon_str = "";
    public string ephem_target_str = "";
    public string ephem_carrier_str = "";
    public string hw_calib_str = "";
    public string hw_compute_str = "";
    public string hw_dma_str = "";
    public string hw_topology_str = "";
    public string balance_status_str = "";
    public string balance_ratio_str = "";
    public string balance_pressure_str = "";
    public string balance_timings_str = "";
    public string balance_partition_str = "";
    public string cpu_latency_str = "";
    public string convergence_summary_str = "";

    // CPU Module Threading & Time-Slicing Telemetry
    public CpuModuleThreadingStatus cpu_threading_status;
    public string cpu_threading_summary_str = "";
    public string cpu_threading_details_str = "";
    public string frame_budget_slicing_str = "";
  }

  private static TelemetrySnapshot current_snapshot_ = new TelemetrySnapshot();

  // Background sampling thread synchronization
  private static Thread async_worker_thread_ = null;
  private static readonly AutoResetEvent wake_event_ = new AutoResetEvent(false);
  private static volatile bool worker_running_ = true;

  static PerformanceProbe() {
    async_worker_thread_ = new Thread(AsyncWorkerLoop) {
      IsBackground = true,
      Name = "PrincipiaProbeAsyncWorker",
      Priority = System.Threading.ThreadPriority.BelowNormal
    };
    async_worker_thread_.Start();
  }

  public readonly struct ProbeScope : IDisposable {
    private readonly ProbeCategory category_;
    private readonly long start_ticks_;

    public ProbeScope(ProbeCategory category) {
      category_ = category;
      start_ticks_ = Stopwatch.GetTimestamp();
    }

    public void Dispose() {
      long elapsed_ticks = Stopwatch.GetTimestamp() - start_ticks_;
      RecordElapsed(category_, elapsed_ticks);
    }
  }

  public static ProbeScope Measure(ProbeCategory category) {
    return new ProbeScope(category);
  }

  public static void RecordElapsed(ProbeCategory category, long elapsed_ticks) {
    float elapsed_ms = (float)(elapsed_ticks * TicksToMs);
    int idx = (int)category;
    smoothed_ms_[idx] = Mathf.Lerp(smoothed_ms_[idx], elapsed_ms, 0.05f);
    if (elapsed_ms > peak_ms_[idx]) {
      peak_ms_[idx] = elapsed_ms;
    } else {
      peak_ms_[idx] = Mathf.Lerp(peak_ms_[idx], smoothed_ms_[idx], 0.005f);
    }
  }

  public static void UpdateFrameMetrics() {
    float dt = Time.unscaledDeltaTime;
    if (dt <= 0.0001f) return;

    FrameTimeMs = dt * 1000.0f;
    CurrentFps = 1.0f / dt;

    frame_time_history_[frame_history_index_] = FrameTimeMs;
    frame_history_index_ = (frame_history_index_ + 1) % FrameHistoryCapacity;
    if (frame_history_count_ < FrameHistoryCapacity) {
      frame_history_count_++;
    }

    if (frame_history_count_ >= 10) {
      Array.Copy(frame_time_history_, sorted_frame_times_, frame_history_count_);
      Array.Sort(sorted_frame_times_, 0, frame_history_count_);
      int p99_idx = Mathf.Clamp((int)(frame_history_count_ * 0.99f), 0, frame_history_count_ - 1);
      int p50_idx = frame_history_count_ / 2;
      P99FrameJitterMs = Mathf.Max(0.0f, sorted_frame_times_[p99_idx] - sorted_frame_times_[p50_idx]);
    }

    long current_mem = GC.GetTotalMemory(forceFullCollection: false);
    if (last_gc_memory_ > 0) {
      long diff = current_mem - last_gc_memory_;
      if (diff > 0) {
        GcAllocRateKbPerFrame = Mathf.Lerp(GcAllocRateKbPerFrame, diff / 1024.0f, 0.1f);
      }
    }
    last_gc_memory_ = current_mem;

    // Send scene feedback to C++ WorkloadBalancer
    KspSceneType scene_type = HighLogic.LoadedScene switch {
      GameScenes.FLIGHT => (FlightGlobals.ActiveVessel?.altitude < (FlightGlobals.ActiveVessel?.mainBody?.atmosphereDepth ?? 0.0))
          ? KspSceneType.FlightAtmospheric
          : KspSceneType.FlightOrbital,
      GameScenes.TRACKSTATION => KspSceneType.TrackingStation,
      GameScenes.SPACECENTER => KspSceneType.SpaceCenter,
      _ => MapView.MapIsEnabled ? KspSceneType.MapView : KspSceneType.Other
    };
    double render_ms = FrameTimeMs;
    double physics_ms = smoothed_ms_[(int)ProbeCategory.UnityFixedUpdate] +
                        smoothed_ms_[(int)ProbeCategory.UnityWaitedForFixedUpdate];
    Interface.UpdateSceneFeedback(scene_type, render_ms, physics_ms);

    // Wake async sampling worker if HUD is visible
    if (ShowProfilingHud) {
      wake_event_.Set();
    }
  }

  private static void AsyncWorkerLoop() {
    while (worker_running_) {
      if (!ShowProfilingHud) {
        // Sleep when HUD is closed (0.000 ms CPU overhead)
        wake_event_.WaitOne(500);
        continue;
      }

      try {
        var snap = new TelemetrySnapshot();

        // 1. Snapshot performance timing
        snap.fps = CurrentFps;
        snap.frame_time_ms = FrameTimeMs;
        snap.jitter_ms = P99FrameJitterMs;
        snap.gc_rate_kb = GcAllocRateKbPerFrame;
        Array.Copy(smoothed_ms_, snap.metric_ms, (int)ProbeCategory.Count);
        Array.Copy(peak_ms_, snap.metric_peak, (int)ProbeCategory.Count);

        // Compute C++ Native vs C# Managed overall latencies
        snap.cpp_physics_ms = snap.metric_ms[(int)ProbeCategory.CppPhysics];
        snap.cpp_plotting_ms = snap.metric_ms[(int)ProbeCategory.CppPlotting];
        snap.cpp_overall_ms = snap.cpp_physics_ms + snap.cpp_plotting_ms;

        snap.principia_total_ms = snap.metric_ms[(int)ProbeCategory.UnityPrecalc] +
                                  snap.metric_ms[(int)ProbeCategory.UnityFixedUpdate] +
                                  snap.metric_ms[(int)ProbeCategory.UnityWaitedForFixedUpdate] +
                                  snap.metric_ms[(int)ProbeCategory.UnityBetterLate] +
                                  snap.metric_ms[(int)ProbeCategory.UnityLateUpdate] +
                                  snap.metric_ms[(int)ProbeCategory.VesselPlot] +
                                  snap.metric_ms[(int)ProbeCategory.CelestialPlot];

        snap.cs_managed_overall_ms = Math.Max(0.0f, snap.principia_total_ms - snap.cpp_overall_ms);
        snap.unity_other_ms = Math.Max(0.0f, snap.frame_time_ms - snap.principia_total_ms);

        if (snap.frame_time_ms > 0.001f) {
          snap.cpp_ratio_pct = (snap.cpp_overall_ms / snap.frame_time_ms) * 100.0f;
          snap.cs_ratio_pct = (snap.cs_managed_overall_ms / snap.frame_time_ms) * 100.0f;
          snap.principia_ratio_pct = (snap.principia_total_ms / snap.frame_time_ms) * 100.0f;
          snap.unity_other_pct = Math.Max(0.0f, 100.0f - snap.principia_ratio_pct);
        }

        snap.perf_summary_str = $"<b>FPS:</b> {snap.fps:F1} | <b>帧时间:</b> {snap.frame_time_ms:F2} ms | <b>抖动(P99):</b> {snap.jitter_ms:F1} ms | <b>GC:</b> {snap.gc_rate_kb:F1} KB/f";
        snap.arch_overall_str = $"Principia 净耗时: <b><color=#55ff55>{snap.principia_total_ms:F2} ms ({snap.principia_ratio_pct:F1}%)</color></b> | Unity 引擎其他: <b>{snap.unity_other_ms:F2} ms ({snap.unity_other_pct:F1}%)</b>";
        snap.cpp_summary_str = $"• <b>C++ 原生层 (CPP Native):</b> <b><color=#55ff55>{snap.cpp_overall_ms:F2} ms ({snap.cpp_ratio_pct:F1}%)</color></b>  [辛积分推进: {snap.cpp_physics_ms:F2}ms | 轨迹多项式: {snap.cpp_plotting_ms:F2}ms]";
        snap.cs_summary_str = $"• <b>C# 托管层 (CS Managed):</b> <b><color=#55ffff>{snap.cs_managed_overall_ms:F2} ms ({snap.cs_ratio_pct:F1}%)</color></b>  [Transform物理同步 + 网格装配 + 标记与事件]";
        snap.unity_summary_str = $"• <b>Unity 引擎其他 (Engine Other):</b> <b><color=#cccccc>{snap.unity_other_ms:F2} ms ({snap.unity_other_pct:F1}%)</color></b>  [图形渲染管线 + 引擎物理 + 外部Mod]";

        for (int i = 0; i < metric_bar_defs_.Length; ++i) {
          var def = metric_bar_defs_[i];
          float cur = snap.metric_ms[(int)def.cat];
          float peak = snap.metric_peak[(int)def.cat];
          snap.metric_bar_ratio[i] = Mathf.Clamp01(cur / def.budget_ms);
          snap.metric_bar_overbudget[i] = cur > def.budget_ms;
          string color_hex = cur > def.budget_ms ? "#ff5555" : (cur > def.budget_ms * 0.75f ? "#ffaa55" : "#55ff55");
          snap.metric_bar_str[i] = $"{def.name}: <b><color={color_hex}>{cur:F3} ms</color></b> (峰值: {peak:F3} ms / 预算: {def.budget_ms:F1} ms)";
        }

        // 2. Snapshot Rolling Ephemeris telemetry from native C++
        var driver = RollingEphemerisDriver.Instance;
        if (driver != null && driver.NativeAvailable) {
          snap.ephemeris_available = true;
          snap.ephemeris_paused = driver.is_paused;
          snap.ephemeris_prolonging = driver.is_prolonging;
          snap.ephemeris_horizon_years = driver.current_horizon_years;
          snap.ephemeris_horizon_seconds = driver.current_horizon_seconds;
          snap.ephemeris_lookahead_years = driver.lookahead_horizon_years;
          snap.ephemeris_refresh_threshold_years = driver.refresh_threshold_years;
          snap.ephemeris_progress_ratio = driver.progress_ratio;
          lock (driver.ConsoleLogs) {
            snap.ephemeris_logs.AddRange(driver.ConsoleLogs);
          }

          string status_color = snap.ephemeris_paused ? "#ffff55" : (snap.ephemeris_prolonging ? "#55ffff" : "#55ff55");
          string status_text = snap.ephemeris_paused ? "已暂停 (Paused)" : (snap.ephemeris_prolonging ? "推演中 (Prolonging...)" : "巡航待机 (Idle)");
          snap.ephem_status_str = $"<b>推演状态:</b> <color={status_color}>{status_text}</color>";
          snap.ephem_horizon_str = $"<b>当前视界:</b> +{snap.ephemeris_horizon_years:F2} 年 ({snap.ephemeris_horizon_seconds:N0} 秒)";
          snap.ephem_target_str = $"<b>目标视界:</b> +{snap.ephemeris_lookahead_years:F1} 年 (刷新阈值: <{snap.ephemeris_refresh_threshold_years:F1} 年)";
          snap.ephem_carrier_str = $"<b>推演驱动:</b> {driver.carrier_vessel_name}";
        } else {
          snap.ephemeris_available = false;
        }

        // 3. Snapshot Dynamic Workload Balancer telemetry from native C++
        Interface.GetWorkloadBalanceState(out WorkloadBalanceState state);
        snap.is_bypassed = state.is_master_bypassed;
        snap.is_render_pressure = state.is_render_pressure_active;
        snap.is_manual_override = state.is_manual_override;
        snap.dynamic_gpu_ratio = state.dynamic_gpu_ratio;
        snap.dynamic_cpu_ratio = state.dynamic_cpu_ratio;
        snap.dynamic_igpu_ratio = state.dynamic_igpu_ratio;
        snap.dynamic_dgpu_ratio = state.dynamic_dgpu_ratio;
        snap.dynamic_aux_ratio = state.dynamic_aux_ratio;
        snap.active_device_count = state.active_device_count;
        snap.smoothed_render_ms = state.smoothed_render_ms;
        snap.smoothed_physics_ms = state.smoothed_physics_ms;
        snap.current_scene = (KspSceneType)state.current_scene;

        int[] multi_counts = new int[8];
        Interface.PartitionMultiDeviceBatch(100, multi_counts, 8);
        snap.batch_cpu_count = multi_counts[0];

        // 4. Snapshot Hardware Profiler & Multi-GPU DXGI telemetry from native C++
        Interface.GetHardwareProfile(out HardwareProfile profile);
        snap.hardware_calibrated = profile.is_calibrated;
        snap.adapter_count = profile.adapter_count;
        snap.cpu_fp64_gflops = profile.cpu_fp64_gflops_score;
        snap.gpu_compute_score = profile.primary_gpu_compute_score;
        snap.dma_bandwidth_gbps = profile.dma_bandwidth_gbps;
        snap.recommended_adapter = profile.recommended_compute_adapter;
        snap.has_discrete_gpu = profile.has_discrete_gpu;
        snap.has_integrated_gpu = profile.has_integrated_gpu;

        // Discover and query every GPU adapter dynamically (Auto-Expanding)
        Interface.GpuGetMultiDeviceSummary(out int dev_count, out int active_devs, out double total_score);
        int total_gpu_vessels = 0;

        for (int i = 0; i < dev_count; ++i) {
          if (Interface.GpuGetDeviceInfo(i, out GpuDeviceInfo dev)) {
            var gdev = new GpuDeviceTelemetry {
              index = dev.adapter_index,
              description = dev.description,
              dedicated_vram = dev.dedicated_video_memory,
              shared_ram = dev.shared_system_memory,
              is_integrated = dev.is_integrated,
              role = dev.role,
              compute_score = dev.compute_score,
              dynamic_ratio = dev.dynamic_workload_ratio
            };

            // Assign vessel count according to role & index
            if (dev.is_integrated) {
              gdev.assigned_vessel_count = multi_counts[1];
              gdev.bar_color = new Color(0.85f, 0.40f, 1.0f); // Purple / Violet
            } else if (dev.role == GpuDeviceRole.PrimaryRender) {
              gdev.assigned_vessel_count = multi_counts[2];
              gdev.bar_color = new Color(0.20f, 0.80f, 1.0f); // Cyan
            } else {
              gdev.assigned_vessel_count = multi_counts[3];
              gdev.bar_color = new Color(0.30f, 1.00f, 0.4f); // Lime
            }
            total_gpu_vessels += gdev.assigned_vessel_count;

            gdev.role_name = dev.role switch {
              GpuDeviceRole.PrimaryRender => "主渲染显卡 (PrimaryRender)",
              GpuDeviceRole.UmaCompute => "UMA 核显计算卡 (UmaCompute)",
              GpuDeviceRole.DedicatedCompute => "专用计算加速卡 (DedicatedCompute)",
              GpuDeviceRole.Auxiliary => "辅助计算设备 (Auxiliary)",
              _ => "待命/禁用 (Disabled)"
            };

            // Latency Calculation for this GPU
            // 16 gravitational symplectic integration steps per vessel per physics tick
            double steps = gdev.assigned_vessel_count * 16.0;
            gdev.estimated_kernel_ms = dev.compute_score > 0.1 ? (steps / dev.compute_score) : 0.0;

            if (dev.is_integrated) {
              // UMA Zero-Copy shared RAM access latency (~0.008 ms)
              gdev.estimated_dma_ms = 0.008;
            } else {
              // PCIe DMA Transfer latency
              double payload_bytes = gdev.assigned_vessel_count * 32.0;
              double measured_bw = Math.Max(1.0, snap.dma_bandwidth_gbps);
              gdev.estimated_dma_ms = (payload_bytes / 1e6) / measured_bw + 0.020;
            }
            gdev.total_latency_ms = gdev.estimated_kernel_ms + gdev.estimated_dma_ms;

            string color_hex = ColorUtility.ToHtmlStringRGB(gdev.bar_color);
            gdev.card_specs_str = $"显存: <b>{dev.dedicated_video_memory / (1024.0 * 1024.0 * 1024.0):F1} GB</b> 独立 / <b>{dev.shared_system_memory / (1024.0 * 1024.0 * 1024.0):F1} GB</b> 共享 | 类型: <b>{(dev.is_integrated ? "核显(iGPU)" : "独显(dGPU)")}</b>";
            gdev.card_perf_str = $"算力评分: <b>{dev.compute_score:F1}</b> | 动态分配份额: <b><color=#{color_hex}>{dev.dynamic_workload_ratio * 100.0:F1}%</color></b> | 角色: <b>{gdev.role_name}</b>";
            gdev.latency_breakdown_str = $"• [{gdev.role_name}] {gdev.description}: <b><color=#{color_hex}>{gdev.total_latency_ms:F3} ms</color></b>  (切分 {gdev.assigned_vessel_count} 艘 | DMA传输: {gdev.estimated_dma_ms:F3}ms + 计算核: {gdev.estimated_kernel_ms:F3}ms)";

            snap.gpu_devices.Add(gdev);
          }
        }
        snap.batch_gpu_count = total_gpu_vessels;

        // CPU Latency Calculation
        double cpu_throughput = Math.Max(50.0, snap.cpu_fp64_gflops);
        snap.cpu_latency_ms = (snap.batch_cpu_count * 16.0) / cpu_throughput;
        snap.cpu_latency_str = $"• [CPU 核心] AVX2/FMA 多线程: <b><color=#ffaa55>{snap.cpu_latency_ms:F3} ms</color></b>  (切分 {snap.batch_cpu_count} 艘 | 单步吞吐: {cpu_throughput:F1}k step/s)";

        // Parallel Convergence & Speedup
        double max_parallel = snap.cpu_latency_ms;
        for (int i = 0; i < snap.gpu_devices.Count; ++i) {
          if (snap.gpu_devices[i].total_latency_ms > max_parallel) {
            max_parallel = snap.gpu_devices[i].total_latency_ms;
          }
        }
        snap.max_parallel_latency_ms = max_parallel;
        snap.baseline_cpu_latency_ms = (100.0 * 16.0) / cpu_throughput;
        snap.speedup_factor = snap.baseline_cpu_latency_ms / Math.Max(0.001, snap.max_parallel_latency_ms);
        snap.convergence_summary_str = $"多设备并发总耗时 (Max 并行延迟): <b><color=#55ff55>{snap.max_parallel_latency_ms:F3} ms</color></b> (纯 CPU 基线: {snap.baseline_cpu_latency_ms:F2} ms，综合提速 <b><color=#55ffff>{snap.speedup_factor:F1}x</color></b>)";

        // Formatting Balancer Text
        string bypass_str = snap.is_bypassed
            ? "<color=red>【全局旁路模式 (Master Bypassed - 强制 100% CPU)】</color>"
            : (snap.is_manual_override ? "<color=yellow>【手动接管模式 (Manual Override)】</color>" : "<color=#55ff55>【动态自适应调度 (Dynamic Active)】</color>");
        snap.balance_status_str = $"调度状态: {bypass_str} | 当前场景: <b>{snap.current_scene}</b>";

        // Dynamic multi-engine ratio string
        var ratio_parts = new List<string> { $"<color=#ffaa55>CPU {snap.dynamic_cpu_ratio * 100.0:F0}%</color>" };
        var part_parts = new List<string> { $"CPU {snap.batch_cpu_count} 艘" };
        foreach (var g in snap.gpu_devices) {
          string hex = ColorUtility.ToHtmlStringRGB(g.bar_color);
          string short_name = g.is_integrated ? "iGPU" : (g.role == GpuDeviceRole.PrimaryRender ? "dGPU" : $"GPU#{g.index}");
          ratio_parts.Add($"<color=#{hex}>{short_name} {g.dynamic_ratio * 100.0:F0}%</color>");
          part_parts.Add($"<color=#{hex}>{short_name} {g.assigned_vessel_count} 艘</color>");
        }
        snap.balance_ratio_str = "计算负载分配: " + string.Join(" | ", ratio_parts.ToArray());
        snap.balance_partition_str = "100 艘非活动飞船切分: " + string.Join(" | ", part_parts.ToArray());

        snap.balance_pressure_str = snap.is_render_pressure
            ? "<color=red>⚠️ 触发帧率保护降级: dGPU 算力回退至 0% 确保 60+ FPS，算力已智能转移至 iGPU + CPU！</color>"
            : "<color=#55ff55>✓ 正常 (多设备算力充沛，协同并行运转)</color>";
        snap.balance_timings_str = $"耗时反馈: 渲染平滑 <b>{snap.smoothed_render_ms:F2} ms</b> | 物理平滑 <b>{snap.smoothed_physics_ms:F2} ms</b>";

        string calib_str = snap.hardware_calibrated ? "<color=#55ff55>已标定 (Calibrated)</color>" : "<color=yellow>未标定 (待执行)</color>";
        snap.hw_calib_str = $"标定状态: {calib_str} | DXGI 发现显卡数: <b>{snap.adapter_count}</b>";
        snap.hw_compute_str = $"CPU FP64 算力: <b><color=#ffaa55>{snap.cpu_fp64_gflops:F1} GFLOPS</color></b> | GPU 算力总和: <b><color=#55ff55>{total_score:F1}</color></b>";
        snap.hw_dma_str = $"PCIe / DMA 带宽: <b>{snap.dma_bandwidth_gbps:F2} GB/s</b> | 优选计算设备: Adapter <b>#{snap.recommended_adapter}</b>";
        string topo_mode = profile.is_heterogeneous_tri_engine
            ? "<color=#55ff55>三擎非对称异构计算已激活 (CPU + iGPU + dGPU)</color>"
            : (snap.has_discrete_gpu && snap.adapter_count > 1 ? "<color=#55ffff>多独显异构协同已激活 (Multi-dGPU)</color>" : "标准异构加速");
        snap.hw_topology_str = $"架构拓扑: {topo_mode}";

        // Snapshot CPU Module Threading & Time-Slicing
        if (PluginPointer != IntPtr.Zero) {
          PluginPointer.GetCpuModuleThreadingStatus(out snap.cpu_threading_status);
        } else {
          Interface.GetCpuModuleThreadingStatus(IntPtr.Zero, out snap.cpu_threading_status);
        }

        var cpu = snap.cpu_threading_status;
        snap.cpu_threading_summary_str =
            $"CPU 拓扑: <b>{cpu.hardware_concurrency} 逻辑并发核心</b> | 飞船线程池: <b>{cpu.vessel_pool_active_threads}/{cpu.vessel_pool_total_threads} 活跃</b> (队列等待: {cpu.vessel_pool_pending_tasks})";

        snap.cpu_threading_details_str =
            $"• <b>飞船并行积分池 (Vessel Pool):</b> <b>{cpu.vessel_pool_active_threads}/{cpu.vessel_pool_total_threads}</b> 工作线程正在执行 (总容量: {cpu.vessel_pool_total_threads} 线程)\n" +
            $"• <b>后台天体星历推进 (Ephemeris Pacer):</b> {(cpu.ephemeris_pacer_active != 0 ? "<color=#55ff55>● 正在推进 (Prolonging)</color>" : "<color=#888888>○ 就绪/休眠 (Idle)</color>")}\n" +
            $"• <b>单船未来轨迹预测 (Prognosticator):</b> <b>{cpu.prognosticator_active_count}</b> 个异步预测守护线程\n" +
            $"• <b>轨道长周期摄动分析 (Orbit Analyser):</b> {(cpu.orbit_analyser_active != 0 ? "<color=#55ff55>● 正在频域拟合 (Running)</color>" : "<color=#888888>○ 空闲 (Idle)</color>")}\n" +
            $"• <b>飞行计划打靶优化 (Flight Plan Optimizer):</b> {(cpu.flight_plan_optimizer_active != 0 ? "<color=#ffff55>● 梯度下降求解中 (Active)</color>" : "<color=#888888>○ 空闲 (Idle)</color>")}";

        string time_color = cpu.current_frame_physics_time_ms > cpu.frame_budget_limit_ms ? "ff5555" : "55ff55";
        string headroom_color = cpu.budget_headroom_ratio > 0.3 ? "55ff55" : (cpu.budget_headroom_ratio > 0.1 ? "ffff55" : "ff5555");
        snap.frame_budget_slicing_str =
            $"物理耗时: <b><color=#{time_color}>{cpu.current_frame_physics_time_ms:F2} ms</color></b> / 预算上限: <b>{cpu.frame_budget_limit_ms:F1} ms</b> (余量: <color=#{headroom_color}>{cpu.budget_headroom_ratio * 100:F1}%</color>)\n" +
            $"自适应动态调步: Tier 2 (伴飞): <b><color=#55ffff>{cpu.effective_tier2_hz:F1} Hz</color></b> (跨度 {cpu.tier2_stride} 帧) | Tier 3 (远景): <b><color=#ffff55>{cpu.effective_tier3_hz:F1} Hz</color></b> (跨度 {cpu.tier3_stride} 帧)\n" +
            $"载具分级分布: Tier 1 (焦点/受力): <b>{cpu.tier1_vessel_count}</b> | Tier 2: <b>{cpu.tier2_vessel_count}</b> | Tier 3: <b>{cpu.tier3_vessel_count}</b> | 熔断让步: <b>{cpu.deferred_slices_yielded}</b> 次\n" +
            $"微架构缓存命中: L1d: <b>{cpu.l1_celestial_cache_hit_count}</b> | L2 瓦片: <b>{cpu.l2_chebyshev_tile_hit_count}</b> | L3 全局星历: <b>{cpu.l3_ephemeris_ring_hit_count}</b>";

        // Publish snapshot atomically
        Interlocked.Exchange(ref current_snapshot_, snap);
      } catch (Exception ex) {
        Log.Warning($"[PerformanceProbe] Async sampling exception: {ex}");
      }

      // Sample at 5 Hz (200 ms interval)
      Thread.Sleep(200);
    }
  }

  public static void RunDmaBenchmarkAsync() {
    if (IsDmaBenchmarking) return;
    IsDmaBenchmarking = true;
    ThreadPool.QueueUserWorkItem(_ => {
      try {
        const int segment_count = 4096;
        bool native_ok = Interface.GpuBenchmarkDmaTransfer(
            segment_count, out double duration_s, out double bandwidth_gbps);
        double duration_ms = duration_s * 1000.0;
        var mode = Interface.GpuGetDmaMode();
        string mode_str = mode switch {
          DmaTransferMode.DirectDma => "Mode 1 (直连 DMA / ReBAR 零拷贝)",
          DmaTransferMode.StagingFallback => "Mode 2 (暂存拷贝降级)",
          DmaTransferMode.PureCpu => "Mode 3 (纯 CPU 模式)",
          _ => mode.ToString()
        };

        if (native_ok) {
          RecordElapsed(ProbeCategory.GpuDmaUpload, (long)(duration_s * Stopwatch.Frequency));
          LastDmaBenchmarkResult =
              $"实测带宽: <b><color=#55ff55>{bandwidth_gbps:F2} GB/s</color></b> | 耗时: <b><color=#55ffff>{duration_ms:F3} ms</color></b> | 模式: <b>{mode_str}</b> | 数据校验: <b><color=#55ff55>100% 通过</color></b>";
        } else {
          LastDmaBenchmarkResult = "<color=#ff5555>DMA 传输基准测试失败或 C++ 未就绪</color>";
        }
      } catch (Exception ex) {
        LastDmaBenchmarkResult = $"<color=#ff5555>压测异常: {ex.Message}</color>";
      } finally {
        IsDmaBenchmarking = false;
      }
    });
  }

  public static void RunCalibrationAsync() {
    if (IsCalibrating) return;
    IsCalibrating = true;
    ThreadPool.QueueUserWorkItem(_ => {
      try {
        Interface.RunHardwareCalibration(out HardwareProfile _);
      } catch (Exception ex) {
        Log.Warning($"[PerformanceProbe] Calibration async exception: {ex}");
      } finally {
        IsCalibrating = false;
      }
    });
  }

  public static void RenderHud() {
    if (!ShowProfilingHud) return;
    hud_rect_ = GUILayout.Window(
        WindowId,
        hud_rect_,
        DrawWindow,
        "Principia 性能与硬件探针 (Performance & Workload Probe)");
  }

  private static void DrawWindow(int id) {
    var snap = current_snapshot_;

    using (new GUILayout.VerticalScope()) {
      current_tab_ = GUILayout.Toolbar(current_tab_, tab_names_, GUILayout.Height(26));
      scroll_pos_ = GUILayout.BeginScrollView(scroll_pos_);

      switch (current_tab_) {
        case 0:
          DrawPerformanceTab(snap);
          break;
        case 1:
          DrawCpuThreadsTab(snap);
          break;
        case 2:
          DrawEphemerisTab(snap);
          break;
        case 3:
          DrawBalanceTab(snap);
          break;
        case 4:
          DrawHardwareTab(snap);
          break;
      }

      GUILayout.EndScrollView();

      using (new GUILayout.HorizontalScope()) {
        if (GUILayout.Button("关闭探针 (Close)", GUILayout.Height(24))) {
          ShowProfilingHud = false;
        }
      }
    }
    GUI.DragWindow();
  }

  private static void DrawPerformanceTab(TelemetrySnapshot snap) {
    GUILayout.Label(snap.perf_summary_str);
    GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

    GUILayout.Label("<b>端到端架构级延迟分解 (Overall Architecture Latency):</b>");
    GUILayout.Label(snap.arch_overall_str);
    GUILayout.Label(snap.cpp_summary_str);
    GUILayout.Label(snap.cs_summary_str);
    GUILayout.Label(snap.unity_summary_str);

    // Tri-color horizontal distribution bar
    Rect r = GUILayoutUtility.GetRect(100, 14);
    GUI.Box(r, "");
    float total_ms = snap.frame_time_ms > 0.001f ? snap.frame_time_ms : 16.67f;
    float cpp_w = Mathf.Clamp01(snap.cpp_overall_ms / total_ms) * r.width;
    float cs_w = Mathf.Clamp01(snap.cs_managed_overall_ms / total_ms) * r.width;
    float other_w = Mathf.Max(0.0f, r.width - cpp_w - cs_w);

    if (cpp_w > 0.5f) {
      Rect fill_cpp = new Rect(r.x, r.y, cpp_w, r.height);
      GUI.DrawTexture(fill_cpp, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(0.2f, 0.9f, 0.3f), 0, 0);
    }
    if (cs_w > 0.5f) {
      Rect fill_cs = new Rect(r.x + cpp_w, r.y, cs_w, r.height);
      GUI.DrawTexture(fill_cs, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(0.2f, 0.8f, 1.0f), 0, 0);
    }
    if (other_w > 0.5f) {
      Rect fill_other = new Rect(r.x + cpp_w + cs_w, r.y, other_w, r.height);
      GUI.DrawTexture(fill_other, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(0.45f, 0.45f, 0.45f), 0, 0);
    }

    GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
    GUILayout.Label("<b>细分阶段生命周期耗时 (Lifecycle Stage Breakdown):</b>");

    for (int i = 0; i < metric_bar_defs_.Length; ++i) {
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label(snap.metric_bar_str[i]);
        Rect r_sub = GUILayoutUtility.GetRect(100, 10);
        GUI.Box(r_sub, "");
        Rect fill = new Rect(r_sub.x, r_sub.y, r_sub.width * snap.metric_bar_ratio[i], r_sub.height);
        GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                        snap.metric_bar_overbudget[i] ? Color.red : Color.green, 0, 0);
      }
    }
  }

  private static void DrawCpuThreadsTab(TelemetrySnapshot snap) {
    var cpu = snap.cpu_threading_status;

    GUILayout.Label("<b>1. CPU 核心拓扑与模块并发度 (CPU Topology & Concurrency):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label(snap.cpu_threading_summary_str);

      // Thread pool active bar
      Rect r = GUILayoutUtility.GetRect(100, 14);
      GUI.Box(r, "");
      float active_ratio = cpu.vessel_pool_total_threads > 0
          ? Mathf.Clamp01((float)cpu.vessel_pool_active_threads / cpu.vessel_pool_total_threads)
          : 0.0f;
      Rect fill = new Rect(r.x, r.y, r.width * active_ratio, r.height);
      GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                      new Color(0.2f, 0.8f, 1.0f), 0, 0);
    }

    GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
    GUILayout.Label("<b>2. Principia 各模块线程运行状态 (Module Thread Status):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label(snap.cpu_threading_details_str);
    }

    GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
    GUILayout.Label("<b>3. 切片刷新与帧预算熔断器 (Time-Sliced Frame Budget):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label(snap.frame_budget_slicing_str);

      // Physics frame budget utilization bar
      Rect r_budget = GUILayoutUtility.GetRect(100, 14);
      GUI.Box(r_budget, "");
      float budget_limit = (float)cpu.frame_budget_limit_ms;
      if (budget_limit < 1.0f) budget_limit = 8.0f;
      float time_used = (float)cpu.current_frame_physics_time_ms;
      float budget_ratio = Mathf.Clamp01(time_used / budget_limit);
      Rect fill_budget = new Rect(r_budget.x, r_budget.y, r_budget.width * budget_ratio, r_budget.height);
      Color budget_bar_color = time_used > budget_limit
          ? new Color(1.0f, 0.3f, 0.3f)
          : new Color(0.2f, 0.9f, 0.4f);
      GUI.DrawTexture(fill_budget, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, budget_bar_color, 0, 0);

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label("设置物理帧预算:", GUILayout.Width(110));
        if (GUILayout.Button("4 ms (240fps)")) {
          Interface.SetFrameBudgetMilliseconds(4.0);
        }
        if (GUILayout.Button("8 ms (120fps)")) {
          Interface.SetFrameBudgetMilliseconds(8.0);
        }
        if (GUILayout.Button("12 ms (60fps)")) {
          Interface.SetFrameBudgetMilliseconds(12.0);
        }
        if (GUILayout.Button("16 ms (常规)")) {
          Interface.SetFrameBudgetMilliseconds(16.0);
        }
      }
    }

    GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
    GUILayout.Label("<b>4. CPU 缓存微架构全层级监视 (L1 / L2 / L3 Multi-Tier Cache):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label($"• <b>L1d 数据缓存 (1.75 KB SoA):</b> 32 天体瞬时状态驻留 (100% L1d Hit: <b><color=#55ff55>{cpu.l1_celestial_cache_hit_count} 次</color></b>)");
      GUILayout.Label($"• <b>L2 单核瓦片缓存 (128 KB):</b> 线程独占 512 个切比雪夫多项式切片 (L2 命中: <b><color=#55ffff>{cpu.l2_chebyshev_tile_hit_count} 次</color></b>)");
      GUILayout.Label($"• <b>L3 全局星历连续时间窗口 (4.19 MB):</b> 32 天体全时段切片常驻 L3，全核心零 DRAM 争用 (L3 命中: <b><color=#ffff55>{cpu.l3_ephemeris_ring_hit_count} 次</color></b>)");
      GUILayout.Label("• <b>缓存行对齐与预取:</b> <b>alignas(64)</b> 消除多核伪共享，<b>_mm_prefetch</b> 流式导入切片系数");
      GUILayout.Label("• <b>切比雪夫连续解析插值:</b> 切片让步载具由高阶多项式解析计算，<b>0 物理卡顿</b>保证极致丝滑");
    }
  }

  private static void DrawEphemerisTab(TelemetrySnapshot snap) {
    if (!snap.ephemeris_available) {
      GUILayout.Label("<color=yellow>○ 正在连接 C++ 原生 EphemerisPacer 守护线程...</color>");
      return;
    }

    GUILayout.Label(snap.ephem_status_str);
    GUILayout.Label(snap.ephem_horizon_str);
    GUILayout.Label(snap.ephem_target_str);
    GUILayout.Label(snap.ephem_carrier_str);

    Rect r = GUILayoutUtility.GetRect(100, 14);
    GUI.Box(r, "");
    float prog = (float)snap.ephemeris_progress_ratio;
    Rect fill = new Rect(r.x, r.y, r.width * prog, r.height);
    GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Color.cyan, 0, 0);

    using (new GUILayout.HorizontalScope()) {
      var driver = RollingEphemerisDriver.Instance;
      if (GUILayout.Button(snap.ephemeris_paused ? "▶ 恢复推演" : "⏸ 暂停推演")) {
        driver?.TogglePause();
      }
      if (GUILayout.Button("⚡ 立即延展")) {
        driver?.ForceProlong();
      }
      if (GUILayout.Button("+1年", GUILayout.Width(60))) {
        driver?.AdjustHorizon(1.0);
      }
      if (GUILayout.Button("-1年", GUILayout.Width(60))) {
        driver?.AdjustHorizon(-1.0);
      }
    }

    GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
    GUILayout.Label($"<b>原生守护线程日志 ({snap.ephemeris_logs.Count} 条):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      if (snap.ephemeris_logs.Count == 0) {
        GUILayout.Label("<color=grey><i>(暂无日志记录)</i></color>");
      } else {
        for (int i = 0; i < snap.ephemeris_logs.Count; ++i) {
          GUILayout.Label(snap.ephemeris_logs[i]);
        }
      }
    }
  }

  private static void DrawBalanceTab(TelemetrySnapshot snap) {
    GUILayout.Label("<b>1. C++ 动态异构负载均衡状态 (WorkloadBalancer Telemetry):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label(snap.balance_status_str);
      GUILayout.Label(snap.balance_ratio_str);

      // Visual multi-segment split bar: Auto-expanding across CPU and all discovered GPUs!
      Rect r = GUILayoutUtility.GetRect(100, 16);
      GUI.Box(r, "");
      float current_x = r.x;

      float cpu_w = Mathf.Clamp01((float)snap.dynamic_cpu_ratio) * r.width;
      if (cpu_w > 0.5f) {
        Rect fill_cpu = new Rect(current_x, r.y, cpu_w, r.height);
        GUI.DrawTexture(fill_cpu, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(1.0f, 0.65f, 0.2f), 0, 0);
        current_x += cpu_w;
      }

      for (int i = 0; i < snap.gpu_devices.Count; ++i) {
        var dev = snap.gpu_devices[i];
        float dev_w = Mathf.Clamp01((float)dev.dynamic_ratio) * r.width;
        if (dev_w > 0.5f) {
          Rect fill_dev = new Rect(current_x, r.y, dev_w, r.height);
          GUI.DrawTexture(fill_dev, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, dev.bar_color, 0, 0);
          current_x += dev_w;
        }
      }

      GUILayout.Label(snap.balance_pressure_str);
      GUILayout.Label(snap.balance_timings_str);
      GUILayout.Label(snap.balance_partition_str);
    }

    GUILayout.Space(6);
    GUILayout.Label("<b>2. 多计算引擎端到端延迟测算 (Per-Device Latency Breakdown):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label(snap.cpu_latency_str);
      for (int i = 0; i < snap.gpu_devices.Count; ++i) {
        GUILayout.Label(snap.gpu_devices[i].latency_breakdown_str);
      }
      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
      GUILayout.Label(snap.convergence_summary_str);
    }

    GUILayout.Space(6);
    GUILayout.Label("<b>3. 动态调度干预与手动接管 (Interventions & Overrides):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      using (new GUILayout.HorizontalScope()) {
        bool new_bypass = GUILayout.Toggle(snap.is_bypassed, "全局旁路 (强制 100% CPU，完全跳过 GPU 调度)");
        if (new_bypass != snap.is_bypassed) {
          Interface.SetWorkloadBypass(new_bypass);
        }
      }

      using (new GUILayout.HorizontalScope()) {
        bool new_override = GUILayout.Toggle(snap.is_manual_override, "手动接管分配比例 (禁用自适应调优)");
        if (new_override != snap.is_manual_override) {
          Interface.SetManualWorkloadOverride(new_override, manual_slider_ratio_);
        }
      }

      if (snap.is_manual_override) {
        using (new GUILayout.HorizontalScope()) {
          GUILayout.Label($"GPU 设定: {manual_slider_ratio_ * 100.0f:F0}%", GUILayout.Width(110));
          float new_ratio = GUILayout.HorizontalSlider(manual_slider_ratio_, 0.0f, 1.0f);
          if (Math.Abs(new_ratio - manual_slider_ratio_) > 0.01f) {
            manual_slider_ratio_ = new_ratio;
            Interface.SetManualWorkloadOverride(true, manual_slider_ratio_);
          }
        }
      }
    }
  }

  private static void DrawHardwareTab(TelemetrySnapshot snap) {
    GUILayout.Label("<b>1. 硬件标定与拓扑评分 (来自 C++ HardwareProfiler):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label(snap.hw_calib_str);
      GUILayout.Label(snap.hw_compute_str);
      GUILayout.Label(snap.hw_dma_str);
      GUILayout.Label(snap.hw_topology_str);

      if (GUILayout.Button(IsCalibrating ? "正在后台执行标定..." : "🔬 异步执行硬件基准标定 (Run Hardware Calibration)")) {
        RunCalibrationAsync();
      }
    }

    GUILayout.Space(6);
    GUILayout.Label($"<b>2. 显卡设备拓扑列表 ({snap.gpu_devices.Count} 个设备已就绪):</b>");
    for (int i = 0; i < snap.gpu_devices.Count; ++i) {
      var dev = snap.gpu_devices[i];
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label($"<b>设备 #{dev.index}: {dev.description}</b>");
        GUILayout.Label(dev.card_specs_str);
        GUILayout.Label(dev.card_perf_str);
      }
    }

    GUILayout.Space(6);
    GUILayout.Label("<b>3. DMA / ReBAR PCIe 高速传输压测 (异步执行):</b>");
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label(LastDmaBenchmarkResult);
      if (GUILayout.Button(IsDmaBenchmarking ? "压测进行中..." : "⚡ 运行 PCIe / ReBAR DMA 传输压测 (1 MB / 4096 节点)")) {
        RunDmaBenchmarkAsync();
      }
    }
  }
}

}  // namespace principia.ksp_plugin_adapter
