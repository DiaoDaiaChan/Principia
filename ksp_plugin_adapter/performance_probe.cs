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
  TotalFrame = 6,
  Count = 7
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
  private static Rect hud_rect_ = new Rect(40, 100, 340, 360);
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

  public static void RecordCacheHit() => CacheHits++;
  public static void RecordCacheMiss() => CacheMisses++;
  public static void RecordVertices(int count) => TotalVerticesRendered = count;

  public static float GetSmoothedMs(ProbeCategory category) => smoothed_ms_[(int)category];
  public static float GetPeakMs(ProbeCategory category) => peak_ms_[(int)category];

  public static void UpdateFrameMetrics() {
    float dt = Time.unscaledDeltaTime;
    CurrentFps = dt > 0.0001f ? Mathf.Lerp(CurrentFps, 1.0f / dt, 0.1f) : 60.0f;
    FrameTimeMs = dt * 1000.0f;

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

  // Draw standalone floating Profiler HUD Window
  public static void DrawProfilingHud() {
    if (!ShowProfilingHud) return;
    GUI.skin = HighLogic.Skin;
    hud_rect_ = GUILayout.Window(
        WindowId,
        hud_rect_,
        RenderHudContents,
        "Principia 性能与遥测探针 (Performance Probe)");
  }

  private static void RenderHudContents(int window_id) {
    using (new GUILayout.VerticalScope()) {
      // 1. Frame & FPS Overview
      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label($"<b>FPS:</b> {CurrentFps:F1}", GUILayout.Width(100));
        GUILayout.Label($"<b>帧耗时:</b> {FrameTimeMs:F2} ms", GUILayout.Width(110));
        GUILayout.Label($"<b>GC:</b> {GcAllocRateKbPerFrame:F1} KB/f");
      }

      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

      // 2. Subsystem Latency Breakdown
      GUILayout.Label("<b>核心子系统延迟 (Smoothed / Peak):</b>");
      DrawMetricBar("物理推进 (Physics):", ProbeCategory.PhysicsAdvance, 10.0f);
      DrawMetricBar("轨迹绘制 (Plotting):", ProbeCategory.TrajectoryPlotting, 5.0f);
      DrawMetricBar("网格细分 (Mesh Gen):", ProbeCategory.MeshGeneration, 3.0f);
      DrawMetricBar("飞行寻优 (Optimizer):", ProbeCategory.FlightPlanEvaluation, 15.0f);
      DrawMetricBar("GPGPU 计算 (Compute):", ProbeCategory.GpuCompute, 5.0f);

      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));

      // 3. Cache & Geometry Telemetry
      GUILayout.Label("<b>缓存与几何统计 (Cache & Geometry):</b>");
      GUILayout.Label($"3D 网格命中率: <b>{CacheHitRatio:F1}%</b> (命中: {CacheHits}, 重构: {CacheMisses})");
      GUILayout.Label($"在屏顶点总数: <b>{TotalVerticesRendered:N0}</b> 顶点");

      // 4. GPGPU Heterogeneous Status
      GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
      GUILayout.Label("<b>GPGPU 异构调度总督:</b>");
      string mode_str = HeterogeneousDynamicGovernor.Affinity switch {
        HeterogeneousAffinity.AutoAdaptive => "Auto (动态自适应)",
        HeterogeneousAffinity.CpuDominant  => "CPU 优先 (节能/核显)",
        HeterogeneousAffinity.GpuDominant  => "GPU 优先 (高吞吐独显)",
        _ => "未知"
      };
      string bp_str = HeterogeneousDynamicGovernor.IsBackpressureActive
          ? "<color=orange>[回压熔断已激活]</color>"
          : "<color=lime>[负载正常]</color>";
      GUILayout.Label($"设备: {PrincipiaGPGPUContext.GpuDeviceName}");
      GUILayout.Label($"模式: {mode_str} {bp_str}");
      GUILayout.Label($"分流统计: GPU {HeterogeneousDynamicGovernor.GpuDispatchCount} 次 | CPU {HeterogeneousDynamicGovernor.CpuDispatchCount} 次");

      if (GUILayout.Button("关闭探针 HUD", GUILayout.Height(24))) {
        ShowProfilingHud = false;
      }
    }
    GUI.DragWindow();
  }

  private static void DrawMetricBar(string label, ProbeCategory cat, float warning_threshold_ms) {
    float ms = GetSmoothedMs(cat);
    float peak = GetPeakMs(cat);
    string color_tag = ms > warning_threshold_ms ? "<color=red>" : (ms > warning_threshold_ms * 0.5f ? "<color=yellow>" : "<color=lime>");
    using (new GUILayout.HorizontalScope()) {
      GUILayout.Label(label, GUILayout.Width(130));
      GUILayout.Label($"{color_tag}{ms:F2}</color> ms (峰值: {peak:F1}ms)");
    }
  }
}

}
