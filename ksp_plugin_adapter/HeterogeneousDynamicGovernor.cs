using System;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public enum HeterogeneousAffinity {
  AutoAdaptive = 0,    // Intelligent dynamic balancing based on workload and frame-time backpressure
  CpuDominant  = 1,    // Strictly prioritize CPU thread pool (best for iGPUs or low-power laptops)
  GpuDominant  = 2     // Prioritize GPU Compute (maximum throughput on dedicated high-end GPUs)
}

public static class HeterogeneousDynamicGovernor {
  // Strategy & Budget configuration
  public static HeterogeneousAffinity Affinity = HeterogeneousAffinity.AutoAdaptive;
  public static float GpuComputeBudgetMs = 1.5f;   // Max compute time budget allowed on GPU per frame
  public static float RenderTimeRedlineMs = 13.0f; // Render time redline (16.6ms - 3.6ms margin for 60fps)
  public const int BatchGpuThreshold = 32;         // Minimum workload size to justify GPU dispatch overhead

  // Stage 3 Decoupling Configuration (Phase 1 & Phase 2)
  public static bool EnableStagedHarvesting = true;            // Sever Early (-99) blocking wait by harvesting at BetterLateThanNever (+8008)
  public static bool EnableZeroCopyTrajectoryPipeline = true;   // Bypass 4-tier CPU mesh copy and O(N) AABB loops
  public static bool EnableAutonomousPhysicsDecoupling = true; // Phase 2: Decouple physical clocks via lock-free state snapshots

  // Smoothed telemetry metrics
  private static float smoothed_gpu_render_time_ms_ = 8.0f;
  private static float smoothed_gpu_compute_time_ms_ = 0.5f;
  private static bool is_backpressure_active_ = false;

  // Telemetry counters
  public static long GpuDispatchCount { get; private set; }
  public static long CpuDispatchCount { get; private set; }
  public static long StagedHarvestCount { get; private set; }
  public static long ZeroCopyDirectCount { get; private set; }
  public static long AutonomousYieldCount { get; private set; }
  public static long AutonomousSnapshotCount => NativeStateSnapshotChannel.SnapshotsConsumed;

  public static float SmoothedRenderTimeMs => smoothed_gpu_render_time_ms_;
  public static float SmoothedComputeTimeMs => smoothed_gpu_compute_time_ms_;
  public static bool IsBackpressureActive => is_backpressure_active_;

  public static void RecordStagedHarvest() {
    StagedHarvestCount++;
  }

  public static void RecordZeroCopyTransfer() {
    ZeroCopyDirectCount++;
  }

  public static void RecordAutonomousYield() {
    AutonomousYieldCount++;
  }

  public static void UpdateFrameMetrics(float render_time_ms, float compute_time_ms) {
    // Exponential moving average (alpha = 0.1)
    smoothed_gpu_render_time_ms_ = Mathf.Lerp(smoothed_gpu_render_time_ms_, render_time_ms, 0.1f);
    smoothed_gpu_compute_time_ms_ = Mathf.Lerp(smoothed_gpu_compute_time_ms_, compute_time_ms, 0.1f);

    // Evaluate backpressure status
    is_backpressure_active_ =
        smoothed_gpu_render_time_ms_ > RenderTimeRedlineMs ||
        smoothed_gpu_compute_time_ms_ > GpuComputeBudgetMs;
  }

  // Millisecond-level dynamic routing decision
  public static bool ShouldDispatchToGpu(int task_item_count) {
    if (Affinity == HeterogeneousAffinity.CpuDominant) {
      CpuDispatchCount++;
      return false;
    }

    if (!PrincipiaGPGPUContext.SupportsCompute) {
      CpuDispatchCount++;
      return false;
    }

    if (Affinity == HeterogeneousAffinity.GpuDominant) {
      if (task_item_count > 4) {
        GpuDispatchCount++;
        return true;
      }
      CpuDispatchCount++;
      return false;
    }

    // AutoAdaptive Policy:
    // 1. Task size must exceed threshold to amortize PCIe / command buffer dispatch latency (~5-20 us)
    if (task_item_count < BatchGpuThreshold) {
      CpuDispatchCount++;
      return false;
    }

    // 2. Render frame-time backpressure check:
    // If the GPU is already under heavy rendering load or compute budget is exhausted,
    // trigger dynamic fallback to CPU thread pool to protect framerate.
    if (is_backpressure_active_) {
      CpuDispatchCount++;
      return false; // Fallback to CPU to maintain 60/120 FPS
    }

    GpuDispatchCount++;
    return true;
  }
}

}
