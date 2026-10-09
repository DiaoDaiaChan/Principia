using System;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public enum ComputationPipelineMode {
  NativeDoublePrecision = 0, // 原生双精度模式 (CPU Native FP64 经典实现，100% 保持原有物理和求解逻辑，旧实现完全不动)
  GpuAccelerated        = 1  // GPGPU 异构加速新管线 (16K 并行打靶 / GPU 自适应细分 / 显存零拷贝)
}

public enum HeterogeneousAffinity {
  AutoAdaptive = 0,    // Intelligent dynamic balancing based on workload and frame-time backpressure
  CpuDominant  = 1,    // Strictly prioritize CPU thread pool (best for iGPUs or low-power laptops)
  GpuDominant  = 2     // Prioritize GPU Compute (maximum throughput on dedicated high-end GPUs)
}

public static class HeterogeneousDynamicGovernor {
  // Master Pipeline Mode: Default to NativeDoublePrecision to preserve 100% untouched classic behavior
  public static ComputationPipelineMode PipelineMode = ComputationPipelineMode.NativeDoublePrecision;

  public static bool IsNativeDoublePrecision => PipelineMode == ComputationPipelineMode.NativeDoublePrecision;
  public static bool IsGpuAccelerated => PipelineMode == ComputationPipelineMode.GpuAccelerated;

  // Strategy & Budget configuration
  public static HeterogeneousAffinity Affinity = HeterogeneousAffinity.AutoAdaptive;
  public static float GpuComputeBudgetMs = 3.0f;   // Max compute time budget allowed on GPU per frame
  public static float RenderTimeRedlineMs = 33.3f; // Render time redline (allows GPU dispatch without premature throttling)
  public static float PhysicsFrameBudgetMs = 30.0f; // Max physics frame duration before tripping circuit breaker (adaptive 30ms cap)
  public const int BatchGpuThreshold = 32;         // Minimum workload size to justify GPU dispatch overhead

  // Stage 3 Decoupling Configuration (Phase 1 & Phase 2)
  public static bool EnableStagedHarvesting = true;            // Sever Early (-99) blocking wait by harvesting at BetterLateThanNever (+8008)
  public static bool EnableZeroCopyTrajectoryPipeline = true;   // Bypass 4-tier CPU mesh copy and O(N) AABB loops
  public static bool EnableAutonomousPhysicsDecoupling = true; // Phase 2: Decouple physical clocks via lock-free state snapshots

  // Decoupling Governance Configuration (Pillars I - IV)
  public static bool EnableVacuumBypass {
    get => VacuumBypassGovernor.EnableVacuumBypass;
    set => VacuumBypassGovernor.EnableVacuumBypass = value;
  }
  public static bool EnableCircuitBreaker = true;           // Frame-time circuit breaker
  public static bool EnableRigidMotionBypass = false;       // Stable vacuum rigid motion P/Invoke & Transform bypass (disabled for joint stability)
  public static bool EnableSceneGraphThrottling = true;     // Celestial body Transform update throttling
  public static bool EnableAutonomousSnapshotSampling = false; // Lock-free snapshot sampling in rendering/LateUpdate

  // Additional Telemetry counters
  public static long RigidMotionBypassedPartCount { get; private set; }
  public static long RigidMotionExecutedPartCount { get; private set; }
  public static long CircuitBreakerTrips { get; private set; }

  public static void RecordRigidMotionBypass(int count) {
    RigidMotionBypassedPartCount += count;
  }

  public static void RecordRigidMotionExecution(int count) {
    RigidMotionExecutedPartCount += count;
  }

  public static void RecordCircuitBreakerTrip() {
    CircuitBreakerTrips++;
  }

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

  public static void RecordGpuDispatch() {
    GpuDispatchCount++;
  }

  public static void RecordCpuDispatch() {
    CpuDispatchCount++;
  }

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

    // Drive dynamic scene-aware balancer loop
    HeterogeneousWorkloadBalancer.Update(Time.unscaledDeltaTime, smoothed_gpu_render_time_ms_);
  }

  // Millisecond-level dynamic routing decision
  public static bool ShouldDispatchToGpu(int task_item_count) {
    // If in Master Bypass or Native Double Precision mode, strictly reject GPU dispatch.
    // Guarantees that the classic, native CPU FP64 pipeline is 100% preserved and untouched!
    if (IsNativeDoublePrecision || HeterogeneousWorkloadBalancer.IsMasterBypassActive) {
      CpuDispatchCount++;
      return false;
    }

    if (Affinity == HeterogeneousAffinity.CpuDominant) {
      CpuDispatchCount++;
      return false;
    }

    if (!PrincipiaGPGPUContext.SupportsCompute) {
      CpuDispatchCount++;
      return false;
    }

    // Dynamic Balancer throttle check
    if (HeterogeneousWorkloadBalancer.GpuWorkloadRatio <= 0.05f) {
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
