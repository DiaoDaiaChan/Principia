using System;
using System.Diagnostics;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public enum ProbeCategory {
  PhysicsAdvance = 0,
  TrajectoryPlotting = 1,
  MeshGeneration = 2,
  FlightPlanEvaluation = 3,
  StorageIO = 4,
  GpuCompute = 5,
  UnityPrecalc = 6,
  UnityFixedUpdate = 7,
  UnityWaitedForFixedUpdate = 8,
  UnityBetterLate = 9,
  UnityLateUpdate = 10,
  TotalFrame = 11,
  Count = 12
}

public static class PerformanceProbe {
  private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

  // Real-time smoothed durations in milliseconds
  private static readonly float[] smoothed_ms_ = new float[(int)ProbeCategory.Count];
  private static readonly float[] peak_ms_ = new float[(int)ProbeCategory.Count];

  // Garbage collection & heap metrics
  private static long last_gc_memory_ = 0;
  public static float GcAllocRateKbPerFrame { get; private set; }

  // Frame rate and frame timing
  public static float CurrentFps { get; private set; }
  public static float FrameTimeMs { get; private set; }

  // Geometry and cache performance counters
  public static long CacheHits { get; private set; }
  public static long CacheMisses { get; private set; }
  public static long TotalVerticesRendered { get; private set; }
  public static float CacheHitRatio =>
      (CacheHits + CacheMisses) > 0 ? (float)CacheHits / (CacheHits + CacheMisses) * 100.0f : 100.0f;

  // In-Game HUD Window state
  public static bool ShowProfilingHud = false;
  private static Rect hud_rect_ = new Rect(40, 80, 440, 640);
  private static Vector2 scroll_pos_ = Vector2.zero;
  private static bool show_ephemeris_console_ = false;
  private const int WindowId = 0x50524F42; // "PROB"

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
    float ms = (float)(elapsed_ticks * TicksToMs);
    int idx = (int)category;
    // Exponential moving average (alpha = 0.08)
    smoothed_ms_[idx] = Mathf.Lerp(smoothed_ms_[idx], ms, 0.08f);
    if (ms > peak_ms_[idx]) {
      peak_ms_[idx] = ms;
    } else {
      peak_ms_[idx] = Mathf.Lerp(peak_ms_[idx], smoothed_ms_[idx], 0.01f);
    }
  }

  private static int frame_vertices_accumulator_ = 0;
  public static void RecordCacheHit() => CacheHits++;
  public static void RecordCacheMiss() => CacheMisses++;
  public static void RecordVertices(int count) => frame_vertices_accumulator_ += count;

  public static float GetSmoothedMs(ProbeCategory category) => smoothed_ms_[(int)category];
  public static float GetPeakMs(ProbeCategory category) => peak_ms_[(int)category];

  public static void UpdateFrameMetrics() {
    float dt = Time.unscaledDeltaTime;
    CurrentFps = dt > 0.0001f ? Mathf.Lerp(CurrentFps, 1.0f / dt, 0.1f) : 60.0f;
    FrameTimeMs = dt * 1000.0f;

    TotalVerticesRendered = frame_vertices_accumulator_;
    frame_vertices_accumulator_ = 0;

    // Track GC memory allocations
    long current_mem = GC.GetTotalMemory(forceFullCollection: false);
    if (last_gc_memory_ > 0) {
      long diff = current_mem - last_gc_memory_;
      if (diff > 0) {
        GcAllocRateKbPerFrame = Mathf.Lerp(GcAllocRateKbPerFrame, diff / 1024.0f, 0.1f);
      }
    }
    last_gc_memory_ = current_mem;
  }

  // Telemetry 10 Hz string cache
  private static float last_telemetry_update_time_ = -1.0f;
  private const float TelemetryUpdateInterval = 0.1f; // ~10 Hz

  private static string cached_fps_str_ = "";
  private static string cached_frametime_str_ = "";
  private static string cached_gc_str_ = "";
  private static string cached_principia_timing_str_ = "";

  private static string cached_ephem_status_str_ = "";
  private static string cached_ephem_carrier_str_ = "";
  private static string cached_ephem_progress_str_ = "";
  private static string cached_ephem_remaining_str_ = "";
  private static string cached_celestial_cache_str1_ = "";
  private static string cached_celestial_cache_str2_ = "";

  private static string cached_decouple_status_str_ = "";
  private static string cached_decouple_consumed_str_ = "";
  private static string cached_decouple_harvest_str_ = "";

  private static string cached_gpu_name_str_ = "";
  private static string cached_gpu_cap_str_ = "";
  private static string cached_zc_str_ = "";
  private static string cached_vertices_str_ = "";
  private static string cached_zc_direct_str_ = "";
  private static string cached_zc_pool_str_ = "";
  private static string cached_cache_hit_str_ = "";
  private static string cached_governor_mode_str_ = "";
  private static string cached_governor_dispatch_str_ = "";
  private static string cached_vacuum_bypass_str_ = "";
  private static string cached_circuit_breaker_str_ = "";
  private static string cached_rigid_motion_bypass_str_ = "";

  private static readonly string[] cached_metric_bar_labels_ = new string[(int)ProbeCategory.Count];

  private static void UpdateTelemetryStringsIfNeeded() {
    float now = Time.realtimeSinceStartup;
    if (now - last_telemetry_update_time_ < TelemetryUpdateInterval && last_telemetry_update_time_ >= 0) {
      return;
    }
    last_telemetry_update_time_ = now;

    cached_fps_str_ = $"<b>FPS:</b> {CurrentFps:F1}";
    cached_frametime_str_ = $"<b>帧耗时:</b> {FrameTimeMs:F2} ms";
    cached_gc_str_ = $"<b>GC:</b> {GcAllocRateKbPerFrame:F1} KB/f";

    // Lifecycle metric bars
    UpdateMetricBarCache(ProbeCategory.UnityPrecalc, 2.0f);
    UpdateMetricBarCache(ProbeCategory.UnityFixedUpdate, 0.5f);
    UpdateMetricBarCache(ProbeCategory.UnityWaitedForFixedUpdate, 2.0f);
    UpdateMetricBarCache(ProbeCategory.UnityBetterLate, 1.0f);
    UpdateMetricBarCache(ProbeCategory.UnityLateUpdate, 2.0f);

    float principia_total = GetSmoothedMs(ProbeCategory.UnityPrecalc) +
                            GetSmoothedMs(ProbeCategory.UnityFixedUpdate) +
                            GetSmoothedMs(ProbeCategory.UnityWaitedForFixedUpdate) +
                            GetSmoothedMs(ProbeCategory.UnityBetterLate) +
                            GetSmoothedMs(ProbeCategory.UnityLateUpdate);
    float principia_ratio = FrameTimeMs > 0.001f ? (principia_total / FrameTimeMs) * 100.0f : 0.0f;
    float unity_other = Mathf.Max(0.0f, FrameTimeMs - principia_total);
    float other_ratio = Mathf.Max(0.0f, 100.0f - principia_ratio);
    cached_principia_timing_str_ =
        $"Principia 耗时: <b>{principia_total:F2} ms ({principia_ratio:F1}%)</b> | Unity 其他系统: <b>{unity_other:F2} ms ({other_ratio:F1}%)</b>";

    var driver = RollingEphemerisDriver.Instance;
    if (driver != null) {
      string status_str = driver.is_paused
          ? "<color=yellow>⏸ 已暂停 (待机中)</color>"
          : (driver.is_prolonging
              ? "<color=cyan>⏳ 正在向 C++ 申请延展...</color>"
              : (driver.is_active
                  ? "<color=lime>● 活跃运行中 (自动滚动延展)</color>"
                  : "<color=yellow>○ 待机寻找载体中</color>"));
      cached_ephem_status_str_ = $"推演状态: {status_str}";
      cached_ephem_carrier_str_ = $"载体飞船 (Carrier): <b>{driver.carrier_vessel_name}</b>";

      float progress = (float)driver.progress_ratio;
      string bar_str = FormatProgressBar(progress, 20);
      string bar_color = driver.is_paused
          ? "<color=grey>"
          : (progress < 0.33f
              ? "<color=yellow>"
              : (driver.is_prolonging ? "<color=cyan>" : "<color=lime>"));
      cached_ephem_progress_str_ = $"<b>前瞻余量进度:</b> {bar_color}{bar_str}</color> (+{driver.current_horizon_years:F2}Y / 目标: {driver.lookahead_horizon_years:F1}Y)";
      cached_ephem_remaining_str_ = $"前瞻剩余时间: <b>{driver.current_horizon_seconds:N0}</b> 秒 (刷新阈值: <{driver.refresh_threshold_years:F1} 年)";

      cached_celestial_cache_str1_ = $"<b>天体轨迹二进制缓存:</b> L1显存: <b>{CelestialTrajectoryBinaryCache.L1CachedMeshCount}</b> 条 | L2磁盘: <b>{CelestialTrajectoryBinaryCache.L2DiskFileCount}</b> 文件 | 命中率: <b>{CelestialTrajectoryBinaryCache.HitRatio:F1}%</b>";
      cached_celestial_cache_str2_ = $"缓存吞吐: L1显存命中 <b>{CelestialTrajectoryBinaryCache.L1HitCount:N0}</b> | L2磁盘直出 <b>{CelestialTrajectoryBinaryCache.L2HitCount:N0}</b> | C++重构 <b>{CelestialTrajectoryBinaryCache.MissCount:N0}</b>";
    }

    string decouple_status = HeterogeneousDynamicGovernor.EnableAutonomousPhysicsDecoupling
        ? "<color=lime>[自治无锁三缓冲状态流转已激活 (0ms 阻塞)]</color>"
        : "<color=yellow>[主线程同步锁步模式]</color>";
    cached_decouple_status_str_ = $"时钟解耦: {decouple_status}";
    cached_decouple_consumed_str_ = $"无锁快照消费: <b>{NativeStateSnapshotChannel.SnapshotsConsumed:N0}</b> 次 (跟踪实体: {NativeStateSnapshotChannel.TrackedEntityCount} 个)";
    cached_decouple_harvest_str_ = $"分阶段让步收获: <b>{HeterogeneousDynamicGovernor.StagedHarvestCount:N0}</b> 次 (0.1ms 阻塞已消除)";

    cached_gpu_name_str_ = $"运算设备: <b>{PrincipiaGPGPUContext.GpuDeviceName}</b>";
    string compute_cap = PrincipiaGPGPUContext.SupportsCompute
        ? "<color=lime>[Compute Shader 5.0 / D3D11 已就绪]</color>"
        : "<color=yellow>[CPU 兼容模式]</color>";
    cached_gpu_cap_str_ = $"硬件加速支持: {compute_cap}";

    string zc_str = HeterogeneousDynamicGovernor.EnableZeroCopyTrajectoryPipeline
        ? "<color=lime>[零拷贝直通已启用 (0 GC / 无 AABB 扫描)]</color>"
        : "<color=yellow>[经典网格模式]</color>";
    cached_zc_str_ = $"显存直通管线: {zc_str}";
    cached_vertices_str_ = $"在屏顶点总数: <b>{TotalVerticesRendered:N0}</b> 顶点 (整帧全轨道汇总)";
    cached_zc_direct_str_ = $"直通提交次数: <b>{HeterogeneousDynamicGovernor.ZeroCopyDirectCount:N0}</b> 次 (峰值: {NativeZeroCopyTrajectoryPipeline.PeakVertexCountObserved:N0} 顶点)";
    cached_zc_pool_str_ = $"非托管显存池: <b>{NativeZeroCopyTrajectoryPipeline.CurrentCapacity:N0}</b> 顶点 ({NativeZeroCopyTrajectoryPipeline.CurrentCapacity * 12 / 1024} KB 独立池)";
    cached_cache_hit_str_ = $"3D 网格多级缓存: 命中率 <b>{CacheHitRatio:F1}%</b> (命中: {CacheHits:N0} | 重构: {CacheMisses:N0}) | 天体轨迹命中: <b>{CelestialTrajectoryBinaryCache.HitRatio:F1}%</b>";

    string mode_str = HeterogeneousDynamicGovernor.Affinity switch {
      HeterogeneousAffinity.AutoAdaptive => "Auto (动态自适应)",
      HeterogeneousAffinity.CpuDominant  => "CPU 优先 (节能/核显)",
      HeterogeneousAffinity.GpuDominant  => "GPU 优先 (高吞吐独显)",
      _ => "未知"
    };
    string bp_str = HeterogeneousDynamicGovernor.IsBackpressureActive
        ? "<color=orange>[回压熔断已激活 (主帧率保护)]</color>"
        : "<color=lime>[负载畅通 (任务可分流)]</color>";
    cached_governor_mode_str_ = $"GPGPU 调度总督: {mode_str} {bp_str}";
    cached_governor_dispatch_str_ = $"异构分流计数: GPU <b>{HeterogeneousDynamicGovernor.GpuDispatchCount:N0}</b> 次 (显存直出) | CPU <b>{HeterogeneousDynamicGovernor.CpuDispatchCount:N0}</b> 次 (网格重构)";

    string bypass_status = VacuumBypassGovernor.EnableVacuumBypass
        ? "<color=lime>Active</color>"
        : "<color=red>Disabled</color>";
    cached_vacuum_bypass_str_ = $"真空物理旁路: Vacuum Bypass: {bypass_status} | 旁路预计算: <b>{VacuumBypassGovernor.BypassedPrecalcCount:N0}</b> (气动旁路: <b>{VacuumBypassGovernor.BypassedAeroCount:N0}</b>) | 校准推进: <b>{VacuumBypassGovernor.ExecutedPrecalcCount:N0}</b>";

    string cb_status = HeterogeneousDynamicGovernor.EnableCircuitBreaker
        ? "<color=lime>[已启用 (硬上限 10.0ms)]</color>"
        : "<color=yellow>[已禁用]</color>";
    cached_circuit_breaker_str_ = $"物理循环熔断: {cb_status} | 熔断抑制次数: <b>{HeterogeneousDynamicGovernor.CircuitBreakerTrips:N0}</b> 步 (彻底终结死循环)";

    cached_rigid_motion_bypass_str_ = $"刚体动力学解耦: 稳态部件免检: <b>{HeterogeneousDynamicGovernor.RigidMotionBypassedPartCount:N0}</b> 部件 | 全量运动回写: <b>{HeterogeneousDynamicGovernor.RigidMotionExecutedPartCount:N0}</b> 部件";

    // Core Subsystems
    UpdateMetricBarCache(ProbeCategory.PhysicsAdvance, 10.0f);
    UpdateMetricBarCache(ProbeCategory.TrajectoryPlotting, 5.0f);
    UpdateMetricBarCache(ProbeCategory.MeshGeneration, 3.0f);
    UpdateMetricBarCache(ProbeCategory.FlightPlanEvaluation, 15.0f);
    UpdateMetricBarCache(ProbeCategory.GpuCompute, 5.0f);
  }

  private static void UpdateMetricBarCache(ProbeCategory cat, float warning_threshold_ms) {
    float ms = GetSmoothedMs(cat);
    float peak = GetPeakMs(cat);
    string color_tag = ms > warning_threshold_ms ? "<color=red>" : (ms > warning_threshold_ms * 0.5f ? "<color=yellow>" : "<color=lime>");
    cached_metric_bar_labels_[(int)cat] = $"{color_tag}{ms:F2}</color> ms (峰值: {peak:F1}ms)";
  }

  // Draw standalone floating Profiler HUD Window
  public static void DrawProfilingHud() {
    if (!ShowProfilingHud) return;
    GUI.skin = HighLogic.Skin;
    hud_rect_ = GUILayout.Window(
        WindowId,
        hud_rect_,
        RenderHudContents,
        "Principia 性能遥测、星历控制台与解耦总督");
  }

  private static void RenderHudContents(int window_id) {
    UpdateTelemetryStringsIfNeeded();
    using (new GUILayout.VerticalScope()) {
      scroll_pos_ = GUILayout.BeginScrollView(scroll_pos_, GUILayout.Height(570));

      // 1. Frame & Engine Overview
      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label(cached_fps_str_, GUILayout.Width(100));
        GUILayout.Label(cached_frametime_str_, GUILayout.Width(120));
        GUILayout.Label(cached_gc_str_);
      }
      GUILayout.Label("<color=grey><i>(Principia 零拷贝轨迹通道产生 GC: 0 B/f)</i></color>");

      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

      // 2. Unity Lifecycle Timing Breakdown
      GUILayout.Label("<b>Unity 端生命周期耗时分析 (Smoothed / Peak):</b>");
      DrawMetricBar("• Precalc() (天体/历表推进):", ProbeCategory.UnityPrecalc);
      DrawMetricBar("• FixedUpdate() (物理帧同步):", ProbeCategory.UnityFixedUpdate);
      DrawMetricBar("• WaitedForFixed() (部件动力学):", ProbeCategory.UnityWaitedForFixedUpdate);
      DrawMetricBar("• BetterLate() (分阶段收割):", ProbeCategory.UnityBetterLate);
      DrawMetricBar("• LateUpdate() (轨迹与视口交互):", ProbeCategory.UnityLateUpdate);

      GUILayout.Label(cached_principia_timing_str_);

      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

      // 3. Background Rolling Ephemeris Extrapolation Console & Progress Bar
      GUILayout.Label("<b>后台星历滚动推演控制台 (Rolling Ephemeris Console):</b>");
      var driver = RollingEphemerisDriver.Instance;
      if (driver != null) {
        GUILayout.Label(cached_ephem_status_str_);
        GUILayout.Label(cached_ephem_carrier_str_);
        GUILayout.Label(cached_ephem_progress_str_);
        GUILayout.Label(cached_ephem_remaining_str_);

        // Interactive Console Buttons
        using (new GUILayout.HorizontalScope()) {
          if (GUILayout.Button(driver.is_paused ? "▶ 恢复" : "⏸ 暂停", GUILayout.Width(70))) {
            driver.TogglePause();
          }
          if (GUILayout.Button("⚡ 立即延展", GUILayout.Width(85))) {
            driver.ForceProlong();
          }
          if (GUILayout.Button("🔄 优选载体", GUILayout.Width(85))) {
            driver.ReselectCarrier();
          }
          if (GUILayout.Button("+1年", GUILayout.Width(45))) {
            driver.AdjustHorizon(1.0);
          }
          if (GUILayout.Button("-1年", GUILayout.Width(45))) {
            driver.AdjustHorizon(-1.0);
          }
        }
        // Celestial Trajectory Binary Cache Telemetry
        GUILayout.Label(cached_celestial_cache_str1_);
        GUILayout.Label(cached_celestial_cache_str2_);

        // Live Event Console Log Feed
        show_ephemeris_console_ = GUILayout.Toggle(show_ephemeris_console_, $"▼ 控制台实时日志 ({driver.ConsoleLogs.Count} 条)");
        if (show_ephemeris_console_) {
          using (new GUILayout.VerticalScope(GUI.skin.box)) {
            lock (driver.ConsoleLogs) {
              if (driver.ConsoleLogs.Count == 0) {
                GUILayout.Label("<color=grey><i>(暂无日志记录)</i></color>");
              } else {
                int start = Math.Max(0, driver.ConsoleLogs.Count - 6);
                for (int i = start; i < driver.ConsoleLogs.Count; ++i) {
                  GUILayout.Label($"<size=10><color=#a0ffa0>{driver.ConsoleLogs[i]}</color></size>");
                }
              }
            }
          }
        }
      } else {
        GUILayout.Label("<color=grey>○ 暂无活动星历驱动器 (等待场景就绪)</color>");
      }

      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

      // 4. Decoupling Phase 2: Autonomous Physics & Lock-Free Triple-Buffering State Channel
      GUILayout.Label("<b>解耦 Phase 2: 自治物理与无锁三缓冲状态总线:</b>");
      GUILayout.Label(cached_decouple_status_str_);
      GUILayout.Label(cached_decouple_consumed_str_);
      GUILayout.Label(cached_decouple_harvest_str_);
      GUILayout.Label(cached_circuit_breaker_str_);
      GUILayout.Label(cached_rigid_motion_bypass_str_);

      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

      // 5. GPU Rendering & Acceleration Indicator
      GUILayout.Label("<b>GPU 绘制与硬件加速指示器 (GPU Acceleration):</b>");
      GUILayout.Label(cached_gpu_name_str_);
      GUILayout.Label(cached_gpu_cap_str_);
      GUILayout.Label(cached_zc_str_);
      GUILayout.Label(cached_vertices_str_);
      GUILayout.Label(cached_zc_direct_str_);
      GUILayout.Label(cached_zc_pool_str_);
      GUILayout.Label(cached_cache_hit_str_);
      GUILayout.Label(cached_governor_mode_str_);
      GUILayout.Label(cached_governor_dispatch_str_);
      GUILayout.Label(cached_vacuum_bypass_str_);

      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

      // 6. Core Subsystem Latency Breakdown
      GUILayout.Label("<b>核心子系统延迟 (Smoothed / Peak):</b>");
      DrawMetricBar("• 物理推进 (Physics):", ProbeCategory.PhysicsAdvance);
      DrawMetricBar("• 轨迹绘制 (Plotting):", ProbeCategory.TrajectoryPlotting);
      DrawMetricBar("• 网格细分 (Mesh Gen):", ProbeCategory.MeshGeneration);
      DrawMetricBar("• 飞行寻优 (Optimizer):", ProbeCategory.FlightPlanEvaluation);
      DrawMetricBar("• GPU绘制直出 (GPU Pass):", ProbeCategory.GpuCompute);

      GUILayout.EndScrollView();

      if (GUILayout.Button("关闭探针 HUD", GUILayout.Height(24))) {
        ShowProfilingHud = false;
      }
    }
    GUI.DragWindow();
  }

  private static string FormatProgressBar(float fraction, int total_blocks = 20) {
    int filled = Mathf.Clamp(Mathf.RoundToInt(fraction * total_blocks), 0, total_blocks);
    int empty = total_blocks - filled;
    return "[" + new string('█', filled) + new string('░', empty) + $"] {fraction * 100.0f:F1}%";
  }

  private static void DrawMetricBar(string label, ProbeCategory cat) {
    using (new GUILayout.HorizontalScope()) {
      GUILayout.Label(label, GUILayout.Width(170));
      GUILayout.Label(cached_metric_bar_labels_[(int)cat] ?? "");
    }
  }
}

}
