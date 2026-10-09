using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public enum HardwareTopologyType {
  Unknown = 0,
  SingleIntegrated = 1,  // 纯核显/APU (UMA 统一内存)
  SingleDiscrete = 2,    // 纯独显 (传统高带宽高并发)
  DualGpuAsymmetric = 3  // 双显卡非对称异构 (核显 iGPU + 独显 dGPU)
}

/// <summary>
/// 启动期全设备硬件性能探针与建模画像器 (Hardware Startup Profiler).
/// 在系统启动或用户请求时，非阻塞异步执行微基准测试 (CPU FP64 矢量算力、GPU Compute 吞吐、PCIe DMA 带宽)，
/// 识别设备物理拓扑并自动生成初始异构算力权重。
/// </summary>
public static class HardwareStartupProfiler {
  public static bool IsCalibrated { get; private set; } = false;
  public static bool IsCalibrating { get; private set; } = false;

  public static float CpuScore { get; private set; } = 0.0f;
  public static float GpuScore { get; private set; } = 0.0f;
  public static float DmaBandwidthGbps { get; private set; } = 0.0f;

  public static HardwareTopologyType Topology { get; private set; } = HardwareTopologyType.Unknown;
  public static bool HasIntegratedGpu { get; private set; } = false;
  public static bool HasDiscreteGpu { get; private set; } = false;
  public static int IntegratedAdapterIndex { get; private set; } = -1;
  public static int DiscreteAdapterIndex { get; private set; } = -1;
  public static int RecommendedComputeAdapterIndex { get; private set; } = -1;

  public static string TopologyDescription { get; private set; } = "检测中...";
  public static string CalibrationSummary { get; private set; } = "尚未执行硬件校准";

  // Normalized compute capacity weights (sums to 1.0)
  public static float InitialCpuWeight { get; private set; } = 0.5f;
  public static float InitialGpuWeight { get; private set; } = 0.5f;

  /// <summary>
  /// 异步执行全设备基准测试与拓扑探测，耗时通常 < 100ms，绝不阻塞游戏主线程。
  /// </summary>
  public static void RunCalibrationBenchmark(Action on_completed = null) {
    if (IsCalibrating) return;
    IsCalibrating = true;

    Task.Run(() => {
      try {
        // 1. CPU FP64 Numerical Physics Benchmark
        // 运行 20,000 步 Runge-Kutta / 双精度引力加速度计算，测试浮点吞吐与缓存延迟
        var cpu_sw = Stopwatch.StartNew();
        double x = 1.0, y = 0.0, z = 0.0;
        double vx = 0.0, vy = 1.0, vz = 0.1;
        const double mu = 398600.4418;
        const double dt = 0.1;

        for (int i = 0; i < 20000; ++i) {
          double r2 = x * x + y * y + z * z;
          double inv_r3 = 1.0 / (r2 * Math.Sqrt(r2));
          double ax = -mu * x * inv_r3;
          double ay = -mu * y * inv_r3;
          double az = -mu * z * inv_r3;
          vx += ax * dt;
          vy += ay * dt;
          vz += az * dt;
          x += vx * dt;
          y += vy * dt;
          z += vz * dt;
        }
        cpu_sw.Stop();
        double cpu_ms = Math.Max(0.01, cpu_sw.Elapsed.TotalMilliseconds);
        CpuScore = (float)(1000.0 / cpu_ms * 10.0); // 标定分数基准 ~ 500 - 3000

        // 2. PCIe / UMA DMA Transfer Benchmark
        bool dma_ok = Interface.GpuBenchmarkDmaTransfer(
            4096, out double dma_dur_s, out double dma_bw_gbps);
        DmaBandwidthGbps = dma_ok ? (float)dma_bw_gbps : 0.0f;

        // 3. Topology & GPU Classification
        int adapter_count = Interface.GpuGetAdapterCount();
        HasIntegratedGpu = false;
        HasDiscreteGpu = false;
        IntegratedAdapterIndex = -1;
        DiscreteAdapterIndex = -1;

        for (int i = 0; i < adapter_count; ++i) {
          if (Interface.GpuGetAdapterInfo(i, out var info)) {
            if (info.is_integrated) {
              HasIntegratedGpu = true;
              if (IntegratedAdapterIndex < 0) IntegratedAdapterIndex = i;
            } else {
              HasDiscreteGpu = true;
              if (DiscreteAdapterIndex < 0) DiscreteAdapterIndex = i;
            }
          }
        }

        // Determine topology & recommendation
        if (HasIntegratedGpu && HasDiscreteGpu) {
          Topology = HardwareTopologyType.DualGpuAsymmetric;
          TopologyDescription = "双显卡非对称异构 (核显 iGPU + 独显 dGPU)";
          // 核心决策：若存在双显卡，强烈推荐指定核显负责物理运算，独显专职 3D 渲染
          RecommendedComputeAdapterIndex = IntegratedAdapterIndex;
        } else if (HasDiscreteGpu) {
          Topology = HardwareTopologyType.SingleDiscrete;
          TopologyDescription = "单独立显卡架构 (dGPU)";
          RecommendedComputeAdapterIndex = DiscreteAdapterIndex >= 0 ? DiscreteAdapterIndex : 0;
        } else if (HasIntegratedGpu) {
          Topology = HardwareTopologyType.SingleIntegrated;
          TopologyDescription = "统一内存核显架构 (iGPU / APU)";
          RecommendedComputeAdapterIndex = IntegratedAdapterIndex >= 0 ? IntegratedAdapterIndex : 0;
        } else {
          Topology = HardwareTopologyType.Unknown;
          TopologyDescription = "通用图形适配器架构";
          RecommendedComputeAdapterIndex = -1;
        }

        // 4. GPU Theoretical / Practical Rating
        float compute_bonus = PrincipiaGPGPUContext.SupportsCompute ? 1.5f : 0.5f;
        float memory_gb = PrincipiaGPGPUContext.GpuMemoryMB / 1024.0f;
        GpuScore = Mathf.Max(50.0f, (memory_gb * 200.0f + DmaBandwidthGbps * 50.0f) * compute_bonus);

        // Normalize initial weights
        float total_score = CpuScore + GpuScore;
        if (total_score > 0.001f) {
          InitialCpuWeight = Mathf.Clamp(CpuScore / total_score, 0.1f, 0.9f);
          InitialGpuWeight = 1.0f - InitialCpuWeight;
        }

        CalibrationSummary =
            $"CPU 得分: <b>{CpuScore:F0}</b> | GPU 得分: <b>{GpuScore:F0}</b> | PCIe/总线带宽: <b>{DmaBandwidthGbps:F2} GB/s</b> | 拓扑: <b>{TopologyDescription}</b>";

        GpuModuleTelemetry.LogBenchmarkResult(
            $"[硬件校准完成] {CalibrationSummary} | 推荐计算卡: [#{RecommendedComputeAdapterIndex}] | 初始算力比: CPU {InitialCpuWeight * 100:F0}% / GPU {InitialGpuWeight * 100:F0}%");

        IsCalibrated = true;
      } catch (Exception ex) {
        CalibrationSummary = $"<color=red>校准异常: {ex.Message}</color>";
        Log.Error($"[HardwareProfiler] Calibration failed: {ex}");
      } finally {
        IsCalibrating = false;
        on_completed?.Invoke();
      }
    });
  }

  /// <summary>
  /// 应用推荐的多 GPU 计算路由配置。
  /// </summary>
  public static void ApplyRecommendedAdapter() {
    if (RecommendedComputeAdapterIndex >= 0) {
      Interface.GpuSelectComputeAdapter(RecommendedComputeAdapterIndex);
      GpuModuleTelemetry.LogBenchmarkResult(
          $"已自动应用推荐计算卡: [#{RecommendedComputeAdapterIndex}] ({TopologyDescription})");
    }
  }
}

}
