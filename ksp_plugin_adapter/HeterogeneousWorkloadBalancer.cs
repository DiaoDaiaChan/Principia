using System;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

/// <summary>
/// Managed adapter and scene coordinator for C++ native HardwareProfiler and WorkloadBalancer.
/// Automatically feeds scene state & render metrics to C++ at native runtime,
/// and provides balanced workload partition decisions across CPU, iGPU, and dGPU.
/// </summary>
public static class HeterogeneousWorkloadBalancer {
  public static HardwareProfile CurrentProfile { get; private set; }
  public static WorkloadBalanceState CurrentState { get; private set; }
  public static bool IsInitialized { get; private set; } = false;

  public static bool IsCalibrated => CurrentProfile.is_calibrated;
  public static double GpuWorkloadRatio => CurrentState.dynamic_gpu_ratio;
  public static double CpuWorkloadRatio => Math.Max(0.0, 1.0 - CurrentState.dynamic_gpu_ratio);
  public static bool IsMasterBypass => CurrentState.is_master_bypassed;
  public static bool IsRenderPressureActive => CurrentState.is_render_pressure_active;
  public static bool IsManualOverride => CurrentState.is_manual_override;
  public static KspSceneType CurrentScene => (KspSceneType)CurrentState.current_scene;

  public static void EnsureInitialized() {
    if (!IsInitialized) {
      RunCalibration();
      IsInitialized = true;
    }
  }

  public static void RunCalibration() {
    try {
      bool ok = Interface.RunHardwareCalibration(out var prof);
      if (ok) {
        CurrentProfile = prof;
        Log.Info(
            $"[Balancer] C++ Hardware calibrated: CPU FP64 Score: {prof.cpu_fp64_gflops_score:F0}, " +
            $"GPU Score: {prof.primary_gpu_compute_score:F0}, DMA Bandwidth: {prof.dma_bandwidth_gbps:F2} GB/s, " +
            $"Adapters: {prof.adapter_count}, iGPU: {prof.has_integrated_gpu}, dGPU: {prof.has_discrete_gpu}, " +
            $"Recommended Adapter: {prof.recommended_compute_adapter}");

        // If system has asymmetric dual GPU (iGPU + dGPU), auto-route to iGPU to preserve dGPU for 3D graphics
        if (prof.has_integrated_gpu && prof.has_discrete_gpu && prof.recommended_compute_adapter >= 0) {
          int current_selected = Interface.GpuGetSelectedAdapter();
          if (current_selected < 0) {
            Interface.GpuSelectComputeAdapter(prof.recommended_compute_adapter);
            Log.Info($"[Balancer] Asymmetric dual GPU detected. Auto-routed compute to iGPU adapter [{prof.recommended_compute_adapter}].");
          }
        }
      } else {
        Interface.GetHardwareProfile(out prof);
        CurrentProfile = prof;
      }
    } catch (Exception ex) {
      Log.Warning($"[Balancer] Calibration exception: {ex.Message}");
    }

    RefreshState();
  }

  public static void RefreshState() {
    try {
      Interface.GetWorkloadBalanceState(out var state);
      CurrentState = state;
    } catch (Exception ex) {
      Log.Warning($"[Balancer] GetState exception: {ex.Message}");
    }
  }

  public static void UpdateFrame(float render_time_ms, float physics_time_ms) {
    EnsureInitialized();

    KspSceneType scene = DetectCurrentScene();

    try {
      Interface.UpdateSceneFeedback(scene, render_time_ms, physics_time_ms);
      Interface.GetWorkloadBalanceState(out var state);
      CurrentState = state;

      // Sync master bypass with HeterogeneousDynamicGovernor
      if (HeterogeneousDynamicGovernor.IsNativeDoublePrecision != state.is_master_bypassed) {
        Interface.SetWorkloadBypass(HeterogeneousDynamicGovernor.IsNativeDoublePrecision);
        Interface.GetWorkloadBalanceState(out state);
        CurrentState = state;
      }
    } catch (Exception ex) {
      Log.Warning($"[Balancer] UpdateFrame exception: {ex.Message}");
    }
  }

  public static void SetMasterBypass(bool bypassed) {
    try {
      Interface.SetWorkloadBypass(bypassed);
      HeterogeneousDynamicGovernor.PipelineMode = bypassed
          ? ComputationPipelineMode.NativeDoublePrecision
          : ComputationPipelineMode.GpuAccelerated;
      RefreshState();
    } catch (Exception ex) {
      Log.Warning($"[Balancer] SetMasterBypass exception: {ex.Message}");
    }
  }

  public static void SetManualOverride(bool enable_manual, double manual_ratio) {
    try {
      Interface.SetManualWorkloadOverride(enable_manual, manual_ratio);
      RefreshState();
    } catch (Exception ex) {
      Log.Warning($"[Balancer] SetManualOverride exception: {ex.Message}");
    }
  }

  public static void PartitionVessels(int total_vessels, out int cpu_count, out int gpu_count) {
    try {
      Interface.PartitionVesselBatch(total_vessels, out cpu_count, out gpu_count);
    } catch {
      cpu_count = total_vessels;
      gpu_count = 0;
    }
  }

  private static KspSceneType DetectCurrentScene() {
    if (MapView.MapIsEnabled) {
      return KspSceneType.MapView;
    }
    if (HighLogic.LoadedScene == GameScenes.TRACKSTATION) {
      return KspSceneType.TrackingStation;
    }
    if (HighLogic.LoadedScene == GameScenes.SPACECENTER) {
      return KspSceneType.SpaceCenter;
    }
    if (HighLogic.LoadedScene == GameScenes.FLIGHT) {
      Vessel v = FlightGlobals.ActiveVessel;
      if (v != null && v.mainBody?.atmosphere == true && v.altitude < 70000.0) {
        return KspSceneType.FlightAtmospheric;
      }
      return KspSceneType.FlightOrbital;
    }
    return KspSceneType.Other;
  }

  public static string SceneDescription => CurrentScene switch {
    KspSceneType.FlightAtmospheric => "✈ 大气层发射/重型载具 (独显渲染保护生效中)",
    KspSceneType.FlightOrbital     => "🌌 轨道真空巡航 (常规平稳自适应)",
    KspSceneType.TrackingStation   => "🪐 跟踪站宏观视角 (GPU 算力全速输出)",
    KspSceneType.SpaceCenter       => "🏢 航天中心主界面 (前瞻星历延伸就绪)",
    KspSceneType.MapView           => "🗺 航图分析模式 (显存直通渲染优先)",
    _                              => "⚪ 待机"
  };

  public static string TopologyDescription {
    get {
      if (CurrentProfile.has_integrated_gpu && CurrentProfile.has_discrete_gpu) {
        return "<color=#55ffff>异构双显卡 (核显 iGPU + 独显 dGPU 隔离互不干扰)</color>";
      }
      if (CurrentProfile.has_discrete_gpu) {
        return "<color=#ffaa55>单独显 (dGPU 动态时间片自适应避让)</color>";
      }
      if (CurrentProfile.has_integrated_gpu) {
        return "<color=#55ff55>单核显/APU (UMA 统一物理内存直连)</color>";
      }
      return "<color=grey>标准计算设备</color>";
    }
  }
}

}
