using System;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public enum SceneProfileType {
  AtmosphericLaunchProtection = 0, // 大气层发射/重度渲染 (优先保护独显 60/120 FPS，压制 GPU 物理)
  OrbitalCruise = 1,               // 太空轨道巡航 (真空稳态，算力均匀分配)
  TrackingStationThroughput = 2,   // 跟踪站宏观观测 (渲染极低，全速释放 GPU 批处理海量星历与飞船)
  SpaceCenterStandby = 3,          // 航天中心主界面 (轻量待机，后台延伸星历)
  MapAnalytics = 4,                // 航图轨道分析 (高频多项式细分与显存直通)
  StandardFlight = 5               // 常规通用飞行
}

/// <summary>
/// 场景感知与运行时动态异构负载均衡器 (Heterogeneous Workload Balancer).
/// 作为 Principia 异构计算的底层中枢，统筹 CPU、核显 (iGPU) 与独显 (dGPU) 的任务切片分配。
/// 支持零侵入 100% 原生双精度 Bypass、自动场景感知以及基于渲染帧时间的自适应闭环流转。
/// </summary>
public static class HeterogeneousWorkloadBalancer {
  // Master switch and bypass state
  public static bool EnableDynamicBalancing = true;
  public static bool ForceManualRatio = false;
  public static float ManualGpuRatio = 0.5f;

  // Realtime adaptive workload partition ratio (0.0 = 100% CPU, 1.0 = 100% GPU)
  private static float dynamic_gpu_ratio_ = 0.5f;
  public static float GpuWorkloadRatio => ForceManualRatio ? ManualGpuRatio : dynamic_gpu_ratio_;
  public static float CpuWorkloadRatio => 1.0f - GpuWorkloadRatio;

  // Scene state
  public static SceneProfileType CurrentSceneProfile { get; private set; } = SceneProfileType.StandardFlight;
  public static string SceneProfileBadge { get; private set; } = "<color=lime>[标准模式]</color>";
  public static string AllocationSummary { get; private set; } = "CPU: 50% | GPU: 50%";

  // Safety thresholds
  public const float RenderTimeSafeBudgetMs = 6.0f;   // Below this, GPU has plenty of headroom
  public const float RenderTimeRedlineMs = 13.0f;     // Above this, GPU is risking 60 FPS drop
  public const float AtmosphereAltitudeThreshold = 70000.0f; // Kerbin atmosphere line

  /// <summary>
  /// 完全硬件级 Bypass 状态。
  /// 若处于原生双精度模式 (Native FP64) 或全局禁用动态均衡，返回 true。
  /// </summary>
  public static bool IsMasterBypassActive =>
      HeterogeneousDynamicGovernor.IsNativeDoublePrecision || !EnableDynamicBalancing;

  /// <summary>
  /// 驱动每帧自适应场景检测与平滑负载比例收敛 (由 ksp_plugin_adapter 帧循环调用)。
  /// </summary>
  public static void Update(float delta_time, float render_time_ms) {
    if (IsMasterBypassActive) {
      dynamic_gpu_ratio_ = 0.0f;
      SceneProfileBadge = "<color=#55ff55>[● 原生双精度 100% Bypass]</color>";
      AllocationSummary = "CPU: 100% (经典核心) | GPU: 0% (完全旁路)";
      return;
    }

    // 1. Detect current game scene and physical state
    DetectSceneProfile(render_time_ms);

    // 2. Compute target GPU ratio based on scene profile & hardware baseline
    float target_ratio = ComputeTargetGpuRatio(render_time_ms);

    // 3. Smooth exponential moving average convergence (alpha = 0.08)
    if (!ForceManualRatio) {
      dynamic_gpu_ratio_ = Mathf.Lerp(dynamic_gpu_ratio_, target_ratio, 0.08f);
      dynamic_gpu_ratio_ = Mathf.Clamp(dynamic_gpu_ratio_, 0.0f, 1.0f);
    }

    AllocationSummary = $"CPU: <b>{CpuWorkloadRatio * 100.0f:F0}%</b> | GPU: <b>{GpuWorkloadRatio * 100.0f:F0}%</b>";
  }

  private static void DetectSceneProfile(float render_time_ms) {
    var scene = HighLogic.LoadedScene;

    if (scene == GameScenes.TRACKSTATION) {
      CurrentSceneProfile = SceneProfileType.TrackingStationThroughput;
      SceneProfileBadge = "<color=#55ffff>[🪐 跟踪站: GPU 算力全速输出]</color>";
      return;
    }

    if (scene == GameScenes.SPACECENTER) {
      CurrentSceneProfile = SceneProfileType.SpaceCenterStandby;
      SceneProfileBadge = "<color=#aaaaff>[🏛 航天中心: 轻量待机]</color>";
      return;
    }

    if (MapView.MapIsEnabled) {
      CurrentSceneProfile = SceneProfileType.MapAnalytics;
      SceneProfileBadge = "<color=#ffff55>[🗺 航图分析: 零拷贝直通优先]</color>";
      return;
    }

    if (scene == GameScenes.FLIGHT) {
      Vessel active = FlightGlobals.ActiveVessel;
      bool is_in_atmo = active != null && active.mainBody != null &&
                        active.mainBody.atmosphere && active.altitude < active.mainBody.atmosphereDepth;

      if (is_in_atmo || render_time_ms > RenderTimeRedlineMs) {
        CurrentSceneProfile = SceneProfileType.AtmosphericLaunchProtection;
        SceneProfileBadge = "<color=#ffaa55>[✈ 大气层发射: GPU 渲染保护生效]</color>";
        return;
      }

      CurrentSceneProfile = SceneProfileType.OrbitalCruise;
      SceneProfileBadge = "<color=#55ff55>[🚀 太空轨道巡航: 均衡分流]</color>";
      return;
    }

    CurrentSceneProfile = SceneProfileType.StandardFlight;
    SceneProfileBadge = "<color=lime>[常规通用模式]</color>";
  }

  private static float ComputeTargetGpuRatio(float render_time_ms) {
    float baseline_gpu_weight = HardwareStartupProfiler.IsCalibrated
        ? HardwareStartupProfiler.InitialGpuWeight
        : 0.5f;

    switch (CurrentSceneProfile) {
      case SceneProfileType.AtmosphericLaunchProtection:
        // 大气层剧烈渲染场景：若渲染耗时超过危险红线，迅速将 GPU 物理负载压至 0 ~ 15%
        if (render_time_ms > RenderTimeRedlineMs) {
          return 0.05f;
        } else if (render_time_ms > 10.0f) {
          return 0.20f;
        }
        return Mathf.Min(baseline_gpu_weight, 0.35f);

      case SceneProfileType.TrackingStationThroughput:
        // 跟踪站无复杂 3D 网格，渲染极低，充分调用 GPU 承接高达 85% 的批量非活动飞船
        return 0.85f;

      case SceneProfileType.SpaceCenterStandby:
        return 0.50f;

      case SceneProfileType.MapAnalytics:
        return Mathf.Clamp(baseline_gpu_weight + 0.15f, 0.4f, 0.8f);

      case SceneProfileType.OrbitalCruise:
      case SceneProfileType.StandardFlight:
      default:
        // 闭环动态调整：根据渲染时间余量微调
        if (render_time_ms > RenderTimeRedlineMs) {
          return Mathf.Max(0.1f, baseline_gpu_weight - 0.3f);
        } else if (render_time_ms < RenderTimeSafeBudgetMs) {
          return Mathf.Min(0.85f, baseline_gpu_weight + 0.2f);
        }
        return baseline_gpu_weight;
    }
  }

  /// <summary>
  /// 批量非活动飞船切片分配器 (Batch Vessel Partitioning).
  /// 根据实时算力比例将 N 艘飞船动态切分为 CPU 切片与 GPU 切片。
  /// </summary>
  public static void GetVesselBatchPartition(int total_vessels, out int cpu_count, out int gpu_count) {
    if (total_vessels <= 0 || IsMasterBypassActive) {
      cpu_count = total_vessels;
      gpu_count = 0;
      return;
    }

    if (!PrincipiaGPGPUContext.SupportsCompute) {
      cpu_count = total_vessels;
      gpu_count = 0;
      return;
    }

    gpu_count = Mathf.RoundToInt(total_vessels * GpuWorkloadRatio);
    gpu_count = Mathf.Clamp(gpu_count, 0, total_vessels);
    cpu_count = total_vessels - gpu_count;
  }

  /// <summary>
  /// 决定是否将 16K 并行打靶派发给 GPU。
  /// </summary>
  public static bool ShouldOffloadShootingToGpu() {
    if (IsMasterBypassActive) return false;
    if (!PrincipiaGPGPUContext.SupportsCompute) return false;

    // 大气层极高渲染负载时退回 CPU 多核，避免独显爆显存或掉帧
    if (CurrentSceneProfile == SceneProfileType.AtmosphericLaunchProtection &&
        HeterogeneousDynamicGovernor.SmoothedRenderTimeMs > RenderTimeRedlineMs) {
      return false;
    }

    return GpuWorkloadRatio > 0.15f;
  }
}

}
