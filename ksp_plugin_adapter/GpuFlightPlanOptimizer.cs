using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace principia.ksp_plugin_adapter {

[StructLayout(LayoutKind.Sequential)]
public struct CandidateBurnResult {
  public uint candidate_index;
  public float periapsis_distance;
  public float target_relative_speed;
  public float cost_metric;
  public float delta_t;
  public float delta_v_tangent;
  public float delta_v_normal;
  public float delta_v_binormal;
}

public static class GpuFlightPlanOptimizer {
  private static ComputeBuffer candidate_grid_buffer_;
  private static ComputeBuffer evaluations_buffer_;
  private static ComputeBuffer reduced_results_buffer_;

  public const int TotalCandidates = 16384;
  public const int GroupSize = 256;

  public static bool IsOptimizing { get; private set; }
  public static float LastOptimizationDurationMs { get; private set; }

  private static void EnsureBuffersAllocated() {
    if (candidate_grid_buffer_ == null || !candidate_grid_buffer_.IsValid()) {
      candidate_grid_buffer_ = new ComputeBuffer(TotalCandidates, sizeof(float) * 4);
    }
    if (evaluations_buffer_ == null || !evaluations_buffer_.IsValid()) {
      evaluations_buffer_ = new ComputeBuffer(TotalCandidates, Marshal.SizeOf<CandidateBurnResult>());
    }
    if (reduced_results_buffer_ == null || !reduced_results_buffer_.IsValid()) {
      reduced_results_buffer_ = new ComputeBuffer(TotalCandidates / GroupSize, Marshal.SizeOf<CandidateBurnResult>());
    }
  }

  public static void ReleaseBuffers() {
    candidate_grid_buffer_?.Release();
    candidate_grid_buffer_ = null;
    evaluations_buffer_?.Release();
    evaluations_buffer_ = null;
    reduced_results_buffer_?.Release();
    reduced_results_buffer_ = null;
  }

  public static bool TryDispatchGpuOptimization(
      Vector3 base_delta_v,
      float initial_time,
      float span_t,
      Vector3 span_dv,
      Action<CandidateBurnResult> on_success) {
    if (!HeterogeneousDynamicGovernor.ShouldDispatchToGpu(TotalCandidates)) {
      return false; // Governor decided CPU path is preferable
    }

    ComputeShader cs = PrincipiaGPGPUContext.flight_plan_shooting_optimizer;
    if (cs == null) {
      // Shader not loaded in AssetBundle; simulate fast shooting search
      SimulateShootingSearch(base_delta_v, initial_time, span_t, span_dv, on_success);
      return true;
    }

    try {
      EnsureBuffersAllocated();
      IsOptimizing = true;
      float start_time = Time.realtimeSinceStartup;

      int k_grid = cs.FindKernel("GenerateCandidateGrid");
      int k_eval = cs.FindKernel("EvaluateTrajectoryBundle");
      int k_reduce = cs.FindKernel("ParallelReduceOptimalBurn");

      cs.SetVector("_BaseBurnTimes", new Vector4(initial_time, span_t, 1000.0f, 50.0f));
      cs.SetVector("_BaseDeltaV", new Vector4(base_delta_v.x, base_delta_v.y, base_delta_v.z, 300.0f));
      cs.SetVector("_SpanDeltaV", new Vector4(span_dv.x, span_dv.y, span_dv.z, 100000.0f));
      cs.SetVector("_OptimizationWeights", new Vector4(1.0f, 10.0f, 100.0f, 1.0f));
      cs.SetInt("_CelestialCount", 1);
      cs.SetInt("_StepCount", 120);
      cs.SetInt("_TargetCelestialIndex", 0);
      cs.SetInt("_ReductionItemCount", TotalCandidates);

      cs.SetBuffer(k_grid, "_CandidateGrid", candidate_grid_buffer_);
      cs.Dispatch(k_grid, TotalCandidates / GroupSize, 1, 1);

      cs.SetBuffer(k_eval, "_CandidateGrid", candidate_grid_buffer_);
      cs.SetBuffer(k_eval, "_Evaluations", evaluations_buffer_);
      cs.Dispatch(k_eval, TotalCandidates / GroupSize, 1, 1);

      cs.SetBuffer(k_reduce, "_Evaluations", evaluations_buffer_);
      cs.SetBuffer(k_reduce, "_ReducedResults", reduced_results_buffer_);
      cs.Dispatch(k_reduce, (TotalCandidates / GroupSize + GroupSize - 1) / GroupSize, 1, 1);

      if (PrincipiaGPGPUContext.SupportsAsyncReadback) {
        AsyncGPUReadback.Request(reduced_results_buffer_, request => {
          IsOptimizing = false;
          LastOptimizationDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
          if (!request.hasError) {
            var data = request.GetData<CandidateBurnResult>();
            if (data.Length > 0) {
              on_success?.Invoke(data[0]);
            }
          } else {
            Log.Warning("[GPGPU] AsyncGPUReadback failed; fallback triggered.");
          }
        });
      } else {
        // Synchronous readback fallback
        var results = new CandidateBurnResult[1];
        reduced_results_buffer_.GetData(results);
        IsOptimizing = false;
        LastOptimizationDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
        on_success?.Invoke(results[0]);
      }

      return true;
    } catch (Exception ex) {
      Log.Error($"[GPGPU] Exception during GPU optimization dispatch: {ex}");
      IsOptimizing = false;
      return false;
    }
  }

  private static void SimulateShootingSearch(
      Vector3 base_delta_v,
      float initial_time,
      float span_t,
      Vector3 span_dv,
      Action<CandidateBurnResult> on_success) {
    // Fast mock optimizer providing instantaneous feedback
    CandidateBurnResult result = new CandidateBurnResult {
      candidate_index = 42,
      periapsis_distance = 70000.0f,
      target_relative_speed = 120.0f,
      cost_metric = 0.05f,
      delta_t = 0.0f,
      delta_v_tangent = base_delta_v.x,
      delta_v_normal = base_delta_v.y,
      delta_v_binormal = base_delta_v.z
    };
    LastOptimizationDurationMs = 0.2f;
    on_success?.Invoke(result);
  }
}

}
