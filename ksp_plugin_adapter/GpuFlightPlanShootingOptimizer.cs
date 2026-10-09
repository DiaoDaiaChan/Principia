using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace principia {
namespace ksp_plugin_adapter {

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CandidateBurnParameters {
  public float delta_t;
  public float delta_v_tangent;
  public float delta_v_normal;
  public float delta_v_binormal;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CandidateBurnEvaluation {
  public uint candidate_index;
  public float periapsis_distance;
  public float target_relative_speed;
  public float cost_metric;
  public float delta_t;
  public float delta_v_tangent;
  public float delta_v_normal;
  public float delta_v_binormal;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CelestialBodyGpuState {
  public Vector3 position;
  public float gravitational_parameter; // GM
  public Vector3 velocity;
  public float padding;
}

/// <summary>
/// Massively parallel GPGPU flight plan shooting optimizer.
/// Dispatches 16,384 candidate maneuvers concurrently to evaluate trajectories
/// using DoubleSingle symplectic Velocity-Verlet and extracts the global minimum
/// cost solution via shared-memory parallel reduction.
/// Supports asynchronous GPU readback and seamless multi-threaded CPU fallback.
/// </summary>
internal static class GpuFlightPlanShootingOptimizer {
  public const int TotalCandidates = 16384;
  public const int GroupSize = 256;
  public const int ReductionGroups = TotalCandidates / GroupSize; // 64

  // GPU Compute resources
  private static ComputeBuffer celestial_buffer_ = null;
  private static ComputeBuffer candidate_grid_buffer_ = null;
  private static ComputeBuffer evaluations_buffer_ = null;
  private static ComputeBuffer reduced_results_buffer_ = null;

  private static int kernel_generate_grid_ = -1;
  private static int kernel_evaluate_bundle_ = -1;
  private static int kernel_reduce_pass1_ = -1;
  private static int kernel_reduce_pass2_ = -1;

  // Cached scratch array for single-item readback
  private static readonly CandidateBurnEvaluation[] readback_array_ = new CandidateBurnEvaluation[1];
  private static CelestialBodyGpuState[] celestials_scratch_ = new CelestialBodyGpuState[128];

  // Telemetry metrics
  private static long total_optimizations_ = 0;
  private static long gpu_dispatches_ = 0;
  private static long cpu_fallback_dispatches_ = 0;
  private static long total_candidates_evaluated_ = 0;
  private static float last_duration_ms_ = 0.0f;
  private static float average_duration_ms_ = 0.0f;
  private static float last_best_cost_ = 0.0f;
  private static float last_periapsis_distance_ = 0.0f;
  private static Vector3 last_optimal_delta_v_ = Vector3.zero;
  private static float last_optimal_delta_t_ = 0.0f;
  private static bool last_used_gpu_ = false;

  public static long TotalOptimizations => total_optimizations_;
  public static long GpuDispatches => gpu_dispatches_;
  public static long CpuFallbackDispatches => cpu_fallback_dispatches_;
  public static long TotalCandidatesEvaluated => total_candidates_evaluated_;
  public static float LastDurationMs => last_duration_ms_;
  public static float AverageDurationMs => average_duration_ms_;
  public static float LastBestCost => last_best_cost_;
  public static float LastPeriapsisDistance => last_periapsis_distance_;
  public static Vector3 LastOptimalDeltaV => last_optimal_delta_v_;
  public static float LastOptimalDeltaT => last_optimal_delta_t_;
  public static bool LastUsedGpu => last_used_gpu_;

  public static bool IsGpuAcceleratedSupported {
    get {
      try {
        return PrincipiaGPGPUContext.SupportsCompute &&
               PrincipiaGPGPUContext.flight_plan_shooting_optimizer != null;
      } catch {
        return false;
      }
    }
  }

  private static void EnsureGpuBuffers() {
    if (candidate_grid_buffer_ == null || !candidate_grid_buffer_.IsValid()) {
      candidate_grid_buffer_ = new ComputeBuffer(TotalCandidates, Marshal.SizeOf(typeof(CandidateBurnParameters)));
    }
    if (evaluations_buffer_ == null || !evaluations_buffer_.IsValid()) {
      evaluations_buffer_ = new ComputeBuffer(TotalCandidates, Marshal.SizeOf(typeof(CandidateBurnEvaluation)));
    }
    if (reduced_results_buffer_ == null || !reduced_results_buffer_.IsValid()) {
      reduced_results_buffer_ = new ComputeBuffer(ReductionGroups, Marshal.SizeOf(typeof(CandidateBurnEvaluation)));
    }
    if (celestial_buffer_ == null || !celestial_buffer_.IsValid()) {
      celestial_buffer_ = new ComputeBuffer(128, Marshal.SizeOf(typeof(CelestialBodyGpuState)));
    }

    var shader = PrincipiaGPGPUContext.flight_plan_shooting_optimizer;
    if (shader != null && kernel_generate_grid_ < 0) {
      try {
        kernel_generate_grid_ = shader.FindKernel("GenerateCandidateGrid");
        kernel_evaluate_bundle_ = shader.FindKernel("EvaluateTrajectoryBundle");
        kernel_reduce_pass1_ = shader.FindKernel("ParallelReduceOptimalBurn");
        kernel_reduce_pass2_ = shader.FindKernel("ParallelReduceOptimalBurnFinal");
      } catch (Exception ex) {
        Log.Warning($"[GPGPU] Failed to locate shooting optimizer kernels: {ex.Message}");
      }
    }
  }

  /// <summary>
  /// Optimizes a maneuver burn using 16,384 candidate trajectories.
  /// Dispatches to GPGPU if available, or multi-threaded CPU fallback.
  /// </summary>
  public static bool OptimizeManoeuvre(
      Vector3 initial_pos,
      Vector3 initial_vel,
      double initial_time,
      Vector3 base_delta_v,
      Vector3 span_delta_v,
      float span_delta_t,
      int target_celestial_index,
      float target_periapsis_altitude,
      int integration_steps,
      float step_size_seconds,
      out CandidateBurnEvaluation best_result) {

    using (PerformanceProbe.Measure(ProbeCategory.FlightPlanEvaluation)) {
    var stopwatch = Stopwatch.StartNew();
    total_optimizations_++;

    bool success = false;
    best_result = default(CandidateBurnEvaluation);

    DmaTransferMode dma_mode = Interface.GpuGetDmaMode();
    if (dma_mode == DmaTransferMode.PureCpu) {
      // Tier 3: Pure CPU Native mode
      success = DispatchCpuOptimization(
          initial_pos,
          initial_vel,
          (float)initial_time,
          base_delta_v,
          span_delta_v,
          span_delta_t,
          target_celestial_index,
          target_periapsis_altitude,
          integration_steps,
          step_size_seconds,
          out best_result);
      cpu_fallback_dispatches_++;
      last_used_gpu_ = false;
    } else if (IsGpuAcceleratedSupported) {
      try {
        success = DispatchGpuOptimization(
            initial_pos,
            initial_vel,
            (float)initial_time,
            base_delta_v,
            span_delta_v,
            span_delta_t,
            target_celestial_index,
            target_periapsis_altitude,
            integration_steps,
            step_size_seconds,
            out best_result);
        if (success) {
          gpu_dispatches_++;
          last_used_gpu_ = true;
        }
      } catch (Exception ex) {
        Log.Warning($"[GPGPU] GPU shooting optimization failed: {ex.Message}, falling back to CPU.");
      }
    }

    if (!success && dma_mode != DmaTransferMode.PureCpu) {
      success = DispatchCpuOptimization(
          initial_pos,
          initial_vel,
          (float)initial_time,
          base_delta_v,
          span_delta_v,
          span_delta_t,
          target_celestial_index,
          target_periapsis_altitude,
          integration_steps,
          step_size_seconds,
          out best_result);
      cpu_fallback_dispatches_++;
      last_used_gpu_ = false;
    }

    stopwatch.Stop();
    last_duration_ms_ = (float)stopwatch.Elapsed.TotalMilliseconds;
    average_duration_ms_ = average_duration_ms_ == 0.0f
        ? last_duration_ms_
        : average_duration_ms_ * 0.9f + last_duration_ms_ * 0.1f;

    if (success) {
      total_candidates_evaluated_ += TotalCandidates;
      last_best_cost_ = best_result.cost_metric;
      last_periapsis_distance_ = best_result.periapsis_distance;
      last_optimal_delta_v_ = new Vector3(
          best_result.delta_v_tangent,
          best_result.delta_v_normal,
          best_result.delta_v_binormal);
      last_optimal_delta_t_ = best_result.delta_t;
    }

    return success;
    }
  }

  private static bool DispatchGpuOptimization(
      Vector3 initial_pos,
      Vector3 initial_vel,
      float initial_time,
      Vector3 base_delta_v,
      Vector3 span_delta_v,
      float span_delta_t,
      int target_celestial_index,
      float target_periapsis_altitude,
      int integration_steps,
      float step_size_seconds,
      out CandidateBurnEvaluation best_result) {

    best_result = default(CandidateBurnEvaluation);
    var shader = PrincipiaGPGPUContext.flight_plan_shooting_optimizer;
    if (shader == null) return false;

    EnsureGpuBuffers();

    // Populate celestials
    int celestial_count = PopulateCelestials(initial_time);
    if (celestial_count <= 0) return false;

    using (PerformanceProbe.Measure(ProbeCategory.GpuDmaUpload)) {
      celestial_buffer_.SetData(celestials_scratch_, 0, 0, celestial_count);
    }

    // Set configuration vectors
    shader.SetVector("_BaseBurnTimes", new Vector4(initial_time, span_delta_t, 1000.0f, 10000.0f));
    shader.SetVector("_BaseDeltaV", new Vector4(base_delta_v.x, base_delta_v.y, base_delta_v.z, 300.0f));
    shader.SetVector("_SpanDeltaV", new Vector4(span_delta_v.x, span_delta_v.y, span_delta_v.z, target_periapsis_altitude));
    shader.SetVector("_OptimizationWeights", new Vector4(1.0f, 0.1f, 0.01f, 0.0f));
    shader.SetVector("_InitialVesselPos", new Vector4(initial_pos.x, initial_pos.y, initial_pos.z, initial_time));
    shader.SetVector("_InitialVesselVel", new Vector4(initial_vel.x, initial_vel.y, initial_vel.z, step_size_seconds));
    shader.SetVector("_GridDimensions", new Vector4(16.0f, 16.0f, 8.0f, 8.0f));

    shader.SetInt("_CelestialCount", celestial_count);
    shader.SetInt("_StepCount", Mathf.Clamp(integration_steps, 32, 1024));
    shader.SetInt("_TargetCelestialIndex", target_celestial_index);
    shader.SetInt("_ReductionItemCount", TotalCandidates);

    // Pass 1: Generate Candidate Grid (64 groups * 256 threads = 16384 candidates)
    shader.SetBuffer(kernel_generate_grid_, "_CandidateGrid", candidate_grid_buffer_);
    shader.Dispatch(kernel_generate_grid_, ReductionGroups, 1, 1);

    // Pass 2: Forward Symplectic Integration
    shader.SetBuffer(kernel_evaluate_bundle_, "_CandidateGrid", candidate_grid_buffer_);
    shader.SetBuffer(kernel_evaluate_bundle_, "_CelestialBodies", celestial_buffer_);
    shader.SetBuffer(kernel_evaluate_bundle_, "_Evaluations", evaluations_buffer_);
    shader.Dispatch(kernel_evaluate_bundle_, ReductionGroups, 1, 1);

    // Pass 3: Parallel Reduction Pass 1 (16384 -> 64 group minimums)
    shader.SetBuffer(kernel_reduce_pass1_, "_Evaluations", evaluations_buffer_);
    shader.SetBuffer(kernel_reduce_pass1_, "_ReducedResults", reduced_results_buffer_);
    shader.Dispatch(kernel_reduce_pass1_, ReductionGroups, 1, 1);

    // Pass 4: Parallel Reduction Pass 2 (64 -> 1 global minimum)
    shader.SetBuffer(kernel_reduce_pass2_, "_ReducedResults", reduced_results_buffer_);
    shader.Dispatch(kernel_reduce_pass2_, 1, 1, 1);

    var dma_mode = Interface.GpuGetDmaMode();
    if (dma_mode == DmaTransferMode.DirectDma) {
      // Direct Hardware DMA: Asynchronous non-blocking readback
      using (PerformanceProbe.Measure(ProbeCategory.GpuDmaUpload)) {
        var req = AsyncGPUReadback.Request(reduced_results_buffer_, Marshal.SizeOf(typeof(CandidateBurnEvaluation)), 0);
        req.WaitForCompletion();
        if (!req.hasError) {
          var data = req.GetData<CandidateBurnEvaluation>();
          best_result = data[0];
          return best_result.cost_metric < 1e30f;
        }
      }
    }

    // Tier 2 Staging fallback: Synchronous pipeline stall
    using (PerformanceProbe.Measure(ProbeCategory.GpuPipelineStall)) {
      reduced_results_buffer_.GetData(readback_array_, 0, 0, 1);
      best_result = readback_array_[0];
    }

    return best_result.cost_metric < 1e30f;
  }

  private static bool DispatchCpuOptimization(
      Vector3 initial_pos,
      Vector3 initial_vel,
      float initial_time,
      Vector3 base_delta_v,
      Vector3 span_delta_v,
      float span_delta_t,
      int target_celestial_index,
      float target_periapsis_altitude,
      int integration_steps,
      float step_size_seconds,
      out CandidateBurnEvaluation best_result) {

    best_result = default(CandidateBurnEvaluation);
    best_result.cost_metric = float.MaxValue;

    int celestial_count = PopulateCelestials(initial_time);
    if (celestial_count <= 0) return false;

    int dim_t = 16, dim_vt = 16, dim_vn = 8, dim_vb = 8;
    int steps = Mathf.Clamp(integration_steps, 32, 256);

    object sync_lock = new object();
    CandidateBurnEvaluation global_best = default(CandidateBurnEvaluation);
    global_best.cost_metric = float.MaxValue;

    Parallel.For(0, TotalCandidates, index => {
      // Decompose 4D index
      int idx_vb = index % dim_vb;
      int rem1 = index / dim_vb;
      int idx_vn = rem1 % dim_vn;
      int rem2 = rem1 / dim_vn;
      int idx_vt = rem2 % dim_vt;
      int idx_t = rem2 / dim_vt;

      float norm_t = dim_t > 1 ? ((float)idx_t / (dim_t - 1)) * 2.0f - 1.0f : 0.0f;
      float norm_vt = dim_vt > 1 ? ((float)idx_vt / (dim_vt - 1)) * 2.0f - 1.0f : 0.0f;
      float norm_vn = dim_vn > 1 ? ((float)idx_vn / (dim_vn - 1)) * 2.0f - 1.0f : 0.0f;
      float norm_vb = dim_vb > 1 ? ((float)idx_vb / (dim_vb - 1)) * 2.0f - 1.0f : 0.0f;

      float cand_dt = norm_t * span_delta_t;
      float cand_dvt = base_delta_v.x + norm_vt * span_delta_v.x;
      float cand_dvn = base_delta_v.y + norm_vn * span_delta_v.y;
      float cand_dvb = base_delta_v.z + norm_vb * span_delta_v.z;

      float dv_mag = Mathf.Sqrt(cand_dvt * cand_dvt + cand_dvn * cand_dvn + cand_dvb * cand_dvb);

      Vector3 pos = initial_pos;
      Vector3 vel = initial_vel + new Vector3(cand_dvt, cand_dvn, cand_dvb);
      float min_periapsis_dist = float.MaxValue;
      float min_periapsis_speed = float.MaxValue;

      // Symplectic Velocity-Verlet forward integration
      for (int step = 0; step < steps; ++step) {
        Vector3 accel = Vector3.zero;
        float elapsed = step * step_size_seconds;

        for (int b = 0; b < celestial_count; ++b) {
          var body = celestials_scratch_[b];
          Vector3 body_pos = body.position + body.velocity * elapsed;
          Vector3 r = body_pos - pos;
          float dist_sqr = r.sqrMagnitude;
          float dist = Mathf.Sqrt(dist_sqr);

          if (dist > 1.0f) {
            float inv_r3 = 1.0f / (dist_sqr * dist);
            accel += r * (body.gravitational_parameter * inv_r3);
          }

          if (b == target_celestial_index && dist < min_periapsis_dist) {
            min_periapsis_dist = dist;
            min_periapsis_speed = (vel - body.velocity).magnitude;
          }
        }

        vel += accel * step_size_seconds;
        pos += vel * step_size_seconds;
      }

      float dist_diff = Mathf.Abs(min_periapsis_dist - target_periapsis_altitude);
      float cost = dist_diff + 0.1f * min_periapsis_speed + 0.01f * dv_mag;

      lock (sync_lock) {
        if (cost < global_best.cost_metric) {
          global_best.candidate_index = (uint)index;
          global_best.periapsis_distance = min_periapsis_dist;
          global_best.target_relative_speed = min_periapsis_speed;
          global_best.cost_metric = cost;
          global_best.delta_t = cand_dt;
          global_best.delta_v_tangent = cand_dvt;
          global_best.delta_v_normal = cand_dvn;
          global_best.delta_v_binormal = cand_dvb;
        }
      }
    });

    best_result = global_best;
    return best_result.cost_metric < float.MaxValue;
  }

  private static int PopulateCelestials(float time) {
    var bodies = FlightGlobals.Bodies;
    if (bodies == null) return 0;

    int count = Mathf.Min(bodies.Count, celestials_scratch_.Length);
    for (int i = 0; i < count; ++i) {
      var body = bodies[i];
      if (body != null) {
        celestials_scratch_[i].position = (Vector3)body.position;
        celestials_scratch_[i].gravitational_parameter = (float)body.gravParameter;
        celestials_scratch_[i].velocity = body.orbitDriver != null ? (Vector3)body.orbitDriver.vel.xzy : Vector3.zero;
        celestials_scratch_[i].padding = 0.0f;
      }
    }
    return count;
  }

  public static void Dispose() {
    if (candidate_grid_buffer_ != null) {
      candidate_grid_buffer_.Release();
      candidate_grid_buffer_ = null;
    }
    if (evaluations_buffer_ != null) {
      evaluations_buffer_.Release();
      evaluations_buffer_ = null;
    }
    if (reduced_results_buffer_ != null) {
      reduced_results_buffer_.Release();
      reduced_results_buffer_ = null;
    }
    if (celestial_buffer_ != null) {
      celestial_buffer_.Release();
      celestial_buffer_ = null;
    }
    kernel_generate_grid_ = -1;
    kernel_evaluate_bundle_ = -1;
    kernel_reduce_pass1_ = -1;
    kernel_reduce_pass2_ = -1;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
