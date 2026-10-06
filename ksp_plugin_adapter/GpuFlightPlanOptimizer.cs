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

[StructLayout(LayoutKind.Sequential)]
public struct CelestialBodyState {
  public Vector3 position;
  public float gravitational_parameter;
  public Vector3 velocity;
  public float padding;
}

public static class GpuFlightPlanOptimizer {
  private static ComputeBuffer candidate_grid_buffer_;
  private static ComputeBuffer evaluations_buffer_;
  private static ComputeBuffer reduced_results_buffer_;
  private static ComputeBuffer celestial_bodies_buffer_;
  private static CelestialBodyState[] bodies_scratch_ = new CelestialBodyState[128];

  public const int TotalCandidates = 16384;
  public const int GroupSize = 256;
  public const int DefaultCelestialCapacity = 64;

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
    int body_count = Mathf.Max(DefaultCelestialCapacity,
        FlightGlobals.Bodies != null ? FlightGlobals.Bodies.Count : DefaultCelestialCapacity);
    if (celestial_bodies_buffer_ == null || !celestial_bodies_buffer_.IsValid() ||
        celestial_bodies_buffer_.count < body_count) {
      celestial_bodies_buffer_?.Release();
      celestial_bodies_buffer_ = new ComputeBuffer(body_count, Marshal.SizeOf<CelestialBodyState>());
    }
  }

  public static void ReleaseBuffers() {
    candidate_grid_buffer_?.Release();
    candidate_grid_buffer_ = null;
    evaluations_buffer_?.Release();
    evaluations_buffer_ = null;
    reduced_results_buffer_?.Release();
    reduced_results_buffer_ = null;
    celestial_bodies_buffer_?.Release();
    celestial_bodies_buffer_ = null;
  }

  public static bool TryDispatchGpuOptimization(
      Vector3 base_delta_v,
      float initial_time,
      float span_t,
      Vector3 span_dv,
      Vessel vessel,
      CelestialBody target_celestial,
      Action<CandidateBurnResult> on_success) {
    ComputeShader cs = PrincipiaGPGPUContext.flight_plan_shooting_optimizer;
    bool prefer_cpu = !HeterogeneousDynamicGovernor.ShouldDispatchToGpu(TotalCandidates) || cs == null;

    try {
      EnsureBuffersAllocated();
      IsOptimizing = true;
      float start_time = Time.realtimeSinceStartup;

      int k_grid = cs.FindKernel("GenerateCandidateGrid");
      int k_eval = cs.FindKernel("EvaluateTrajectoryBundle");
      int k_reduce = cs.FindKernel("ParallelReduceOptimalBurn");
      int k_reduce_final = cs.FindKernel("ParallelReduceOptimalBurnFinal");

      CelestialBody central_body = target_celestial;
      if (central_body == null) {
        central_body = vessel?.mainBody ??
                       FlightGlobals.currentMainBody ??
                       FlightGlobals.GetHomeBody() ??
                       (FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 ? FlightGlobals.Bodies[0] : null);
      }
      float central_gm = central_body != null ? (float)central_body.gravParameter : 3.5316e12f;

      Vector3 central_body_pos = central_body != null ? (Vector3)central_body.position : Vector3.zero;
      Vector3 central_body_vel = central_body?.orbit != null ? (Vector3)central_body.orbit.GetVel() : Vector3.zero;

      Vector3 vessel_pos = Vector3.zero;
      Vector3 vessel_vel = Vector3.zero;
      if (vessel != null) {
        vessel_pos = (Vector3)vessel.GetWorldPos3D() - central_body_pos;
        vessel_vel = (Vector3)vessel.GetObtVelocity();
      }

      int body_buffer_len = celestial_bodies_buffer_.count;
      if (bodies_scratch_.Length < body_buffer_len) {
        Array.Resize(ref bodies_scratch_, body_buffer_len);
      }
      var bodies = bodies_scratch_;
      int celestial_count = 1;
      int target_index = 0;

      bodies[0] = new CelestialBodyState {
        position = Vector3.zero,
        gravitational_parameter = central_gm,
        velocity = Vector3.zero,
        padding = 0
      };

      if (target_celestial != null && target_celestial != central_body) {
        bodies[1] = new CelestialBodyState {
          position = (Vector3)(target_celestial.position - (Vector3d)central_body_pos),
          gravitational_parameter = (float)target_celestial.gravParameter,
          velocity = (Vector3)((target_celestial.orbit != null ? target_celestial.orbit.GetVel() : Vector3d.zero) - (Vector3d)central_body_vel),
          padding = 0
        };
        target_index = 1;
        celestial_count = 2;
      }
      celestial_bodies_buffer_.SetData(bodies);

      // Dynamic integration horizon based on transfer geometry or orbital period
      int step_count = 256;
      double flight_time = 3600.0;
      if (target_celestial != null && vessel != null) {
        double dist = (vessel.GetWorldPos3D() - target_celestial.position).magnitude;
        double speed = Math.Max(100.0, vessel.GetObtVelocity().magnitude + base_delta_v.magnitude);
        flight_time = Math.Max(3600.0, (dist / speed) * 1.2);
        if (vessel.orbit != null) {
          flight_time = Math.Max(flight_time, vessel.orbit.period * 0.5);
        }
      } else if (vessel?.orbit != null) {
        flight_time = Math.Max(3600.0, vessel.orbit.period * 1.2);
      }
      flight_time = Mathf.Clamp((float)flight_time, 1800.0f, 86400.0f * 365.0f);
      float dt = (float)(flight_time / step_count);

      // Target periapsis radius
      float target_r = 100000.0f;
      if (target_celestial != null) {
        float safe_alt = (float)(target_celestial.atmosphere ? target_celestial.atmosphereDepth * 1.2 : target_celestial.Radius * 0.1);
        target_r = (float)target_celestial.Radius + Mathf.Max(10000.0f, safe_alt);
      }

      if (prefer_cpu) {
        // Multi-threaded CPU parallel shooting optimizer fallback
        bool cpu_ok = GpuFlightPlanShootingOptimizer.OptimizeManoeuvre(
            vessel_pos,
            vessel_vel,
            initial_time,
            base_delta_v,
            span_dv,
            span_t,
            target_index,
            target_r,
            step_count,
            dt,
            out var eval);
        IsOptimizing = false;
        LastOptimizationDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
        if (cpu_ok) {
          GpuModuleTelemetry.RecordDispatch(
              GpuModuleId.FlightPlanOptimizer,
              LastOptimizationDurationMs,
              $"[CPU并行打靶] 最优候选 #{eval.candidate_index}: cost={eval.cost_metric:F2}, Δv={eval.delta_v_tangent:F1} m/s");
          on_success?.Invoke(new CandidateBurnResult {
            candidate_index = eval.candidate_index,
            periapsis_distance = eval.periapsis_distance,
            target_relative_speed = eval.target_relative_speed,
            cost_metric = eval.cost_metric,
            delta_t = eval.delta_t,
            delta_v_tangent = eval.delta_v_tangent,
            delta_v_normal = eval.delta_v_normal,
            delta_v_binormal = eval.delta_v_binormal
          });
          return true;
        }
        return false;
      }

      // Collect real vessel mass, thrust and Isp
      float vessel_mass = vessel != null ? (float)vessel.totalMass * 1000.0f : 1000.0f;
      float vessel_thrust = 50000.0f;
      float vessel_isp = 300.0f;
      if (vessel != null && vessel.parts != null) {
        float total_thrust = 0.0f;
        float weighted_isp = 0.0f;
        for (int pi = 0; pi < vessel.parts.Count; ++pi) {
          var engines = vessel.parts[pi].FindModulesImplementing<ModuleEngines>();
          if (engines != null) {
            for (int ei = 0; ei < engines.Count; ++ei) {
              var eng = engines[ei];
              if (eng.isOperational && eng.maxThrust > 0) {
                float th = eng.maxThrust * 1000.0f;
                total_thrust += th;
                weighted_isp += th * eng.realIsp;
              }
            }
          }
        }
        if (total_thrust > 0) {
          vessel_thrust = total_thrust;
          vessel_isp = weighted_isp / total_thrust;
        }
      }

      cs.SetVector("_BaseBurnTimes", new Vector4(initial_time, span_t, vessel_mass, vessel_thrust));
      cs.SetVector("_BaseDeltaV", new Vector4(base_delta_v.x, base_delta_v.y, base_delta_v.z, vessel_isp));
      cs.SetVector("_SpanDeltaV", new Vector4(span_dv.x, span_dv.y, span_dv.z, target_r));
      cs.SetVector("_OptimizationWeights", new Vector4(1.0f, 10.0f, 100.0f, 1.0f));
      cs.SetVector("_InitialVesselPos", new Vector4(vessel_pos.x, vessel_pos.y, vessel_pos.z, initial_time));
      cs.SetVector("_InitialVesselVel", new Vector4(vessel_vel.x, vessel_vel.y, vessel_vel.z, dt));
      cs.SetVector("_GridDimensions", new Vector4(16, 16, 8, 8));
      cs.SetInt("_CelestialCount", celestial_count);
      cs.SetInt("_StepCount", step_count);
      cs.SetInt("_TargetCelestialIndex", target_index);
      cs.SetInt("_ReductionItemCount", TotalCandidates);

      cs.SetBuffer(k_grid, "_CandidateGrid", candidate_grid_buffer_);
      cs.Dispatch(k_grid, TotalCandidates / GroupSize, 1, 1);

      cs.SetBuffer(k_eval, "_CandidateGrid", candidate_grid_buffer_);
      cs.SetBuffer(k_eval, "_CelestialBodies", celestial_bodies_buffer_);
      cs.SetBuffer(k_eval, "_Evaluations", evaluations_buffer_);
      cs.Dispatch(k_eval, TotalCandidates / GroupSize, 1, 1);

      // Pass 1: Reduce 16,384 candidates across 64 thread groups (256 each) into 64 intermediate minimums
      cs.SetBuffer(k_reduce, "_Evaluations", evaluations_buffer_);
      cs.SetBuffer(k_reduce, "_ReducedResults", reduced_results_buffer_);
      cs.Dispatch(k_reduce, TotalCandidates / GroupSize, 1, 1);

      // Pass 2: Reduce 64 intermediate minimums in 1 thread group into the true global minimum at index 0
      cs.SetBuffer(k_reduce_final, "_ReducedResults", reduced_results_buffer_);
      cs.Dispatch(k_reduce_final, 1, 1, 1);

      if (PrincipiaGPGPUContext.SupportsAsyncReadback) {
        AsyncGPUReadback.Request(reduced_results_buffer_, request => {
          IsOptimizing = false;
          LastOptimizationDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
          if (!request.hasError) {
            var data = request.GetData<CandidateBurnResult>();
            if (data.Length > 0) {
              GpuModuleTelemetry.RecordDispatch(
                  GpuModuleId.FlightPlanOptimizer,
                  LastOptimizationDurationMs,
                  $"最优候选 #{data[0].candidate_index}: cost={data[0].cost_metric:F2}, Δv={data[0].delta_v_tangent:F1} m/s");
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
        GpuModuleTelemetry.RecordDispatch(
            GpuModuleId.FlightPlanOptimizer,
            LastOptimizationDurationMs,
            $"最优候选 #{results[0].candidate_index}: cost={results[0].cost_metric:F2}, Δv={results[0].delta_v_tangent:F1} m/s");
        on_success?.Invoke(results[0]);
      }

      return true;
    } catch (Exception ex) {
      Log.Error($"[GPGPU] Exception during GPU optimization dispatch: {ex}, attempting CPU parallel fallback");
      IsOptimizing = false;
      try {
        bool cpu_ok = GpuFlightPlanShootingOptimizer.OptimizeManoeuvre(
            Vector3.zero,
            Vector3.zero,
            initial_time,
            base_delta_v,
            span_dv,
            span_t,
            0,
            100000.0f,
            256,
            14.0f,
            out var eval);
        if (cpu_ok) {
          on_success?.Invoke(new CandidateBurnResult {
            candidate_index = eval.candidate_index,
            periapsis_distance = eval.periapsis_distance,
            target_relative_speed = eval.target_relative_speed,
            cost_metric = eval.cost_metric,
            delta_t = eval.delta_t,
            delta_v_tangent = eval.delta_v_tangent,
            delta_v_normal = eval.delta_v_normal,
            delta_v_binormal = eval.delta_v_binormal
          });
          return true;
        }
      } catch {
        // Fallback failed
      }
      return false;
    }
  }
}

}
