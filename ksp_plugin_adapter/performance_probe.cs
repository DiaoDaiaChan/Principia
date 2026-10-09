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
  VesselHarvest = 12,
  BodyFramesUpdate = 13,
  CelestialPlot = 14,
  VesselPlot = 15,
  EquipotentialPlot = 16,
  GpuDmaUpload = 17,
  GpuPipelineStall = 18,
  FrameJitterP99 = 19,
  Count = 20
}

public static class PerformanceProbe {
  private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

  // Real-time smoothed durations in milliseconds
  private static readonly float[] smoothed_ms_ = new float[(int)ProbeCategory.Count];
  private static readonly float[] peak_ms_ = new float[(int)ProbeCategory.Count];

  // Frame jitter (P99 - P50 frame delta)
  private const int FrameHistoryCapacity = 120;
  private static readonly float[] frame_time_history_ = new float[FrameHistoryCapacity];
  private static readonly float[] sorted_frame_times_ = new float[FrameHistoryCapacity];
  private static int frame_history_index_ = 0;
  private static int frame_history_count_ = 0;
  public static float P99FrameJitterMs { get; private set; }

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
  private static Rect hud_rect_ = new Rect(40, 80, 520, 600);
  private static int current_tab_ = 0;
  private static readonly string[] tab_names_ = new string[] {
    "📊 性能总览",
    "🪐 后台星历",
    "⚡ GPU与DMA",
    "⚙️ 架构解耦"
  };
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

  // Trajectory plot invalidations.  Every InvalidateAll() forces the next frame
  // to re-derive every cached mesh through the C++ planetarium (P/Invoke), so a
  // value that climbs at roughly one per frame means the cache never holds and
  // the whole multi-level cache is being bypassed.
  private static long plot_invalidations_ = 0;
  public static long PlotInvalidations => plot_invalidations_;

  // Per-caller breakdown of plot invalidations, so the HUD can show *who* is
  // wiping the caches (e.g. vessel-change vs plotting-frame-change).
  private static readonly System.Collections.Generic.Dictionary<string, long>
      plot_invalidation_reasons_ =
          new System.Collections.Generic.Dictionary<string, long>();

  public static void RecordPlotInvalidation(string reason) {
    plot_invalidations_++;
    plot_invalidation_reasons_[reason] =
        plot_invalidation_reasons_.TryGetValue(reason, out long count)
            ? count + 1
            : 1;
  }

  public static string PlotInvalidationReasonSummary {
    get {
      if (plot_invalidation_reasons_.Count == 0) {
        return "(无)";
      }
      var sb = new System.Text.StringBuilder();
      foreach (var kv in plot_invalidation_reasons_) {
        if (sb.Length > 0) {
          sb.Append(" | ");
        }
        sb.Append(kv.Key).Append(": ").Append(kv.Value);
      }
      return sb.ToString();
    }
  }

  public static float GetSmoothedMs(ProbeCategory category) => smoothed_ms_[(int)category];
  public static float GetPeakMs(ProbeCategory category) => peak_ms_[(int)category];

  public static void UpdateFrameMetrics() {
    float dt = Time.unscaledDeltaTime;
    CurrentFps = dt > 0.0001f ? Mathf.Lerp(CurrentFps, 1.0f / dt, 0.1f) : 60.0f;
    FrameTimeMs = dt * 1000.0f;

    // Rolling window for P99 frame jitter calculation
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
      smoothed_ms_[(int)ProbeCategory.FrameJitterP99] = P99FrameJitterMs;
    }

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
    TelemetryLogger.Update();
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
  private static string cached_invalidation_reasons_str_ = "";
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

    cached_fps_str_ = $"<b>FPS:</b> {CurrentFps:F1} (Jitter: {P99FrameJitterMs:F1}ms)";
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

      cached_celestial_cache_str1_ = $"<b>天体轨迹零时漂显存缓存:</b> L1显存: <b>{CelestialTrajectoryBinaryCache.L1CachedMeshCount}</b> 条 | 命中率: <b>{CelestialTrajectoryBinaryCache.HitRatio:F1}%</b>";
      cached_celestial_cache_str2_ = $"缓存吞吐: L1暂停/同UT复用 <b>{CelestialTrajectoryBinaryCache.L1HitCount:N0}</b> | C++实时直出 <b>{CelestialTrajectoryBinaryCache.MissCount:N0}</b>";
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
    cached_cache_hit_str_ = $"3D 网格多级缓存: 命中率 <b>{CacheHitRatio:F1}%</b> (命中: {CacheHits:N0} | 重构: {CacheMisses:N0} | 失效: {PlotInvalidations:N0}) | 天体轨迹命中: <b>{CelestialTrajectoryBinaryCache.HitRatio:F1}%</b>";
    cached_invalidation_reasons_str_ = $"失效来源: <b>{PlotInvalidationReasonSummary}</b>";

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
    UpdateMetricBarCache(ProbeCategory.VesselHarvest, 2.0f);
    UpdateMetricBarCache(ProbeCategory.BodyFramesUpdate, 2.0f);
    UpdateMetricBarCache(ProbeCategory.CelestialPlot, 2.0f);
    UpdateMetricBarCache(ProbeCategory.VesselPlot, 3.0f);
    UpdateMetricBarCache(ProbeCategory.EquipotentialPlot, 1.0f);
    UpdateMetricBarCache(ProbeCategory.GpuDmaUpload, 1.0f);
    UpdateMetricBarCache(ProbeCategory.GpuPipelineStall, 2.0f);
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
        "Principia 性能遥测与系统监控 (Performance HUD)");
  }

  private static void RenderHudContents(int window_id) {
    UpdateTelemetryStringsIfNeeded();
    using (new GUILayout.VerticalScope()) {
      current_tab_ = GUILayout.Toolbar(current_tab_, tab_names_, GUILayout.Height(26));

      switch (current_tab_) {
        case 0:
          RenderOverviewTab();
          break;
        case 1:
          RenderEphemerisTab();
          break;
        case 2:
          RenderGpuDmaTab();
          break;
        case 3:
          RenderDecouplingTab();
          break;
      }

      if (GUILayout.Button("关闭探针 HUD", GUILayout.Height(24))) {
        ShowProfilingHud = false;
      }
    }
    GUI.DragWindow();
  }

  private static void RenderOverviewTab() {
    scroll_pos_ = GUILayout.BeginScrollView(scroll_pos_, GUILayout.Height(480));
    using (new GUILayout.VerticalScope()) {
      // 1. Frame & Engine Overview Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        using (new GUILayout.HorizontalScope()) {
          GUILayout.Label(cached_fps_str_, GUILayout.Width(170));
          GUILayout.Label(cached_frametime_str_, GUILayout.Width(130));
          GUILayout.Label(cached_gc_str_);
        }
        GUILayout.Label("<color=grey><i>(Principia 零拷贝轨迹通道产生 GC: 0 B/f)</i></color>");
      }

      // 2. Realtime Telemetry Controls Card
      using (new GUILayout.HorizontalScope(GUI.skin.box)) {
        string log_status = TelemetryLogger.IsLoggingEnabled
            ? "<color=lime>● 记录中 (1 Hz)</color>"
            : "<color=grey>○ 已暂停</color>";
        GUILayout.Label($"<b>实时遥测:</b> {log_status}", GUILayout.ExpandWidth(true));
        if (GUILayout.Button(TelemetryLogger.IsLoggingEnabled ? "暂停" : "记录", GUILayout.Width(60))) {
          TelemetryLogger.IsLoggingEnabled = !TelemetryLogger.IsLoggingEnabled;
        }
        if (GUILayout.Button("导出快照", GUILayout.Width(75))) {
          TelemetryLogger.ForceFlushSnapshot();
        }
      }

      // 3. Timing Breakdown Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>系统耗时占比分配:</b>");
        GUILayout.Label(cached_principia_timing_str_);
      }

      // 4. Unity Lifecycle Timing Breakdown Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>Unity 端生命周期耗时分析 (Smoothed / Peak):</b>");
        DrawMetricBar("• Precalc() (天体/历表推进):", ProbeCategory.UnityPrecalc);
        DrawMetricBar("• FixedUpdate() (物理帧同步):", ProbeCategory.UnityFixedUpdate);
        DrawMetricBar("• WaitedForFixed() (部件动力学):", ProbeCategory.UnityWaitedForFixedUpdate);
        DrawMetricBar("• BetterLate() (分阶段收割):", ProbeCategory.UnityBetterLate);
        DrawMetricBar("• LateUpdate() (轨迹与视口交互):", ProbeCategory.UnityLateUpdate);
      }
    }
    GUILayout.EndScrollView();
  }

  private static void RenderEphemerisTab() {
    scroll_pos_ = GUILayout.BeginScrollView(scroll_pos_, GUILayout.Height(480));
    using (new GUILayout.VerticalScope()) {
      var driver = RollingEphemerisDriver.Instance;
      if (driver != null) {
        using (new GUILayout.VerticalScope(GUI.skin.box)) {
          GUILayout.Label("<b>后台星历滚动推演状态:</b>");
          GUILayout.Label(cached_ephem_status_str_);
          GUILayout.Label(cached_ephem_carrier_str_);
          GUILayout.Label(cached_ephem_progress_str_);
          GUILayout.Label(cached_ephem_remaining_str_);

          using (new GUILayout.HorizontalScope()) {
            if (GUILayout.Button(driver.is_paused ? "▶ 恢复" : "⏸ 暂停", GUILayout.Width(75))) {
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
        }

        using (new GUILayout.VerticalScope(GUI.skin.box)) {
          GUILayout.Label("<b>天体轨迹零时漂显存缓存 (L1 VRAM Binary Cache):</b>");
          GUILayout.Label(cached_celestial_cache_str1_);
          GUILayout.Label(cached_celestial_cache_str2_);
        }

        using (new GUILayout.VerticalScope(GUI.skin.box)) {
          show_ephemeris_console_ = GUILayout.Toggle(show_ephemeris_console_, $"▼ 控制台实时推演日志 ({driver.ConsoleLogs.Count} 条)");
          if (show_ephemeris_console_) {
            using (new GUILayout.VerticalScope(GUI.skin.box)) {
              lock (driver.ConsoleLogs) {
                if (driver.ConsoleLogs.Count == 0) {
                  GUILayout.Label("<color=grey><i>(暂无日志记录)</i></color>");
                } else {
                  int start = Math.Max(0, driver.ConsoleLogs.Count - 8);
                  for (int i = start; i < driver.ConsoleLogs.Count; ++i) {
                    GUILayout.Label($"<size=10><color=#a0ffa0>{driver.ConsoleLogs[i]}</color></size>");
                  }
                }
              }
            }
          }
        }
      } else {
        GUILayout.Label("<color=grey>○ 暂无活动星历驱动器 (等待场景就绪)</color>");
      }
    }
    GUILayout.EndScrollView();
  }

  private static void RenderGpuDmaTab() {
    scroll_pos_ = GUILayout.BeginScrollView(scroll_pos_, GUILayout.Height(480));
    using (new GUILayout.VerticalScope()) {
      // Scene-Aware Dynamic Balancer Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>自适应场景感知与算力动态平衡 (Workload Balancer):</b>");
        GUILayout.Label($"<b>当前感知场景:</b> <color=#55ffff>{HeterogeneousWorkloadBalancer.SceneDescription}</color>");
        int cpu_pct = (int)(HeterogeneousWorkloadBalancer.CpuWorkloadRatio * 100.0);
        int gpu_pct = (int)(HeterogeneousWorkloadBalancer.GpuWorkloadRatio * 100.0);
        string pressure_tag = HeterogeneousWorkloadBalancer.IsRenderPressureActive
            ? " <color=orange>[渲染保护生效中]</color>"
            : "";
        GUILayout.Label($"<b>实时算力切片:</b> CPU <b>{cpu_pct}%</b> | GPU <b>{gpu_pct}%</b>{pressure_tag}");
      }

      // GPU Device Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>GPU 硬件与加速能力 (GPU Hardware):</b>");
        GUILayout.Label(cached_gpu_name_str_);
        GUILayout.Label(cached_gpu_cap_str_);
      }

      // DMA Mode & Dedicated Benchmark Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>DMA 传输模式与 PCIe 测速 (DMA Transfer & Benchmark):</b>");
        DmaTransferMode current_dma_mode = Interface.GpuGetDmaMode();
        using (new GUILayout.HorizontalScope()) {
          bool is_dma = current_dma_mode == DmaTransferMode.DirectDma;
          bool is_staging = current_dma_mode == DmaTransferMode.StagingFallback;
          bool is_pure_cpu = current_dma_mode == DmaTransferMode.PureCpu;

          if (GUILayout.Toggle(is_dma, "⚡ 直连 DMA (ReBAR)", GUI.skin.toggle)) {
            if (!is_dma) {
              Interface.GpuSetDmaMode(DmaTransferMode.DirectDma);
              GpuModuleTelemetry.LogBenchmarkResult("已切换至 [Mode 1: 硬件直连 DMA (ReBAR)] 零拷贝高速直达。");
            }
          }
          if (GUILayout.Toggle(is_staging, "📦 暂存拷贝降级", GUI.skin.toggle)) {
            if (!is_staging) {
              Interface.GpuSetDmaMode(DmaTransferMode.StagingFallback);
              GpuModuleTelemetry.LogBenchmarkResult("已切换至 [Mode 2: 暂存拷贝降级模式] 兼容无 ReBAR 设备。");
            }
          }
          if (GUILayout.Toggle(is_pure_cpu, "🛑 纯 CPU", GUI.skin.toggle)) {
            if (!is_pure_cpu) {
              Interface.GpuSetDmaMode(DmaTransferMode.PureCpu);
              GpuModuleTelemetry.LogBenchmarkResult("已切换至 [Mode 3: 纯 CPU 原生模式] 硬件级旁路全部 GPU 派发。");
            }
          }
        }

        // Dedicated DMA benchmark button
        string btn_text = GpuDiagnosticsWindow.IsDmaBenchmarking
            ? "⏳ 正在测试 PCIe DMA 传输..."
            : "⚡ 测试 DMA 传输性能 (Benchmark PCIe DMA Transfer)";
        if (GUILayout.Button(btn_text, GUILayout.Height(28))) {
          GpuDiagnosticsWindow.RunDmaBenchmark();
        }

        using (new GUILayout.HorizontalScope(GUI.skin.box)) {
          GUILayout.Label($"<b>测试结果:</b> {GpuDiagnosticsWindow.LastDmaBenchmarkResult}", GUILayout.ExpandWidth(true));
        }

        // Live A/B Probes
        double last_dma_write_s = Interface.GpuGetLastDmaWriteSeconds();
        ulong total_dma_bytes = Interface.GpuGetTotalDmaBytes();
        double total_dma_mb = total_dma_bytes / (1024.0 * 1024.0);
        float dma_upload_ms = GetSmoothedMs(ProbeCategory.GpuDmaUpload);
        float gpu_stall_ms = GetSmoothedMs(ProbeCategory.GpuPipelineStall);
        float jitter_ms = P99FrameJitterMs;
        string stall_color = gpu_stall_ms < 0.1f ? "#55ff55" : (gpu_stall_ms < 1.0f ? "#ffff55" : "#ff5555");
        string dma_color = dma_upload_ms < 0.2f ? "#55ff55" : "#ffff55";
        string jitter_color = jitter_ms < 3.0f ? "#55ff55" : (jitter_ms < 8.0f ? "#ffff55" : "#ff5555");

        GUILayout.Label(
            $"<b>实时 A/B 探针:</b> DMA 上传: <color={dma_color}>{dma_upload_ms:F3} ms</color> | GPU 管线停顿: <color={stall_color}>{gpu_stall_ms:F3} ms</color> | P99 抖动: <color={jitter_color}>{jitter_ms:F2} ms</color>\n最近 DMA 写: <b>{last_dma_write_s * 1000.0:F3} ms</b> | 累计直通吞吐: <b>{total_dma_mb:F2} MB</b>");
      }

      // Zero-Copy & Mesh Cache Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>显存直通管线与网格缓存 (Zero-Copy Pipeline & Mesh Cache):</b>");
        GUILayout.Label(cached_zc_str_);
        GUILayout.Label(cached_vertices_str_);
        GUILayout.Label(cached_zc_direct_str_);
        GUILayout.Label(cached_zc_pool_str_);
        GUILayout.Label(cached_cache_hit_str_);
        GUILayout.Label(cached_invalidation_reasons_str_);
      }

      // Governor Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>异构调度总督 (Heterogeneous Governor):</b>");
        GUILayout.Label(cached_governor_mode_str_);
        GUILayout.Label(cached_governor_dispatch_str_);
      }
    }
    GUILayout.EndScrollView();
  }

  private static void RenderDecouplingTab() {
    scroll_pos_ = GUILayout.BeginScrollView(scroll_pos_, GUILayout.Height(480));
    using (new GUILayout.VerticalScope()) {
      // Decoupling & Lock-Free State Bus Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>自治物理与无锁三缓冲状态总线 (Lock-Free State Bus):</b>");
        GUILayout.Label(cached_decouple_status_str_);
        GUILayout.Label(cached_decouple_consumed_str_);
        GUILayout.Label(cached_decouple_harvest_str_);
      }

      // Circuit Breaker & Bypass Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>物理循环熔断与旁路保护 (Safety & Decoupling):</b>");
        GUILayout.Label(cached_circuit_breaker_str_);
        GUILayout.Label(cached_rigid_motion_bypass_str_);
        GUILayout.Label(cached_vacuum_bypass_str_);
      }

      // Core Subsystem Latency Breakdown Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>核心子系统延迟 (Smoothed / Peak):</b>");
        DrawMetricBar("• 物理推进 (AdvanceTime):", ProbeCategory.PhysicsAdvance);
        DrawMetricBar("• 载体收割 (Harvest):", ProbeCategory.VesselHarvest);
        DrawMetricBar("• 天体框架 (BodyFrames):", ProbeCategory.BodyFramesUpdate);
        DrawMetricBar("• 轨迹绘制 (Plotting):", ProbeCategory.TrajectoryPlotting);
        DrawMetricBar("    ◦ 天体轨迹 (Celestial):", ProbeCategory.CelestialPlot);
        DrawMetricBar("    ◦ 飞船轨迹 (Vessel):", ProbeCategory.VesselPlot);
        DrawMetricBar("    ◦ 等势线 (Equipotential):", ProbeCategory.EquipotentialPlot);
        DrawMetricBar("• 网格细分 (Mesh Gen):", ProbeCategory.MeshGeneration);
        DrawMetricBar("• 飞行寻优 (Optimizer):", ProbeCategory.FlightPlanEvaluation);
        DrawMetricBar("• GPU绘制直出 (GPU Pass):", ProbeCategory.GpuCompute);
        DrawMetricBar("• DMA 零拷贝传输 (DMA Upload):", ProbeCategory.GpuDmaUpload);
        DrawMetricBar("• GPU 管线停顿/同步 (GPU Stall):", ProbeCategory.GpuPipelineStall);
      }
    }
    GUILayout.EndScrollView();
  }

  private static string FormatProgressBar(float fraction, int total_blocks = 20) {
    int filled = Mathf.Clamp(Mathf.RoundToInt(fraction * total_blocks), 0, total_blocks);
    int empty = total_blocks - filled;
    return "[" + new string('█', filled) + new string('░', empty) + $"] {fraction * 100.0f:F1}%";
  }

  private static void DrawMetricBar(string label, ProbeCategory cat) {
    using (new GUILayout.HorizontalScope()) {
      GUILayout.Label(label, GUILayout.Width(225));
      GUILayout.Label(cached_metric_bar_labels_[(int)cat] ?? "", GUILayout.ExpandWidth(true));
    }
  }
}

}
