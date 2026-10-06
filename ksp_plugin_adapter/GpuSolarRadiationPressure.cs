using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public static class GpuSolarRadiationPressure {
  private const int ThreadGroupSize = 64;

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct OccultingBodyData {
    public Vector3 position_m;
    public float radius_m;
  }

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct SrpEvaluationInput {
    public Vector3 position_m;
    public float mass_kg;
    public float cross_sectional_area_m2;
    public float reflection_coefficient;
  }

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct SrpEvaluationOutput {
    public Vector3 acceleration_m_s2;
    public float shadow_factor;
    public float flux_w_m2;
    public float padding;
  }

  public static bool IsEvaluating { get; private set; }
  public static float LastEvaluationDurationMs { get; private set; }

  private static ComputeBuffer occulting_bodies_buffer_;
  private static ComputeBuffer vessels_buffer_;
  private static ComputeBuffer outputs_buffer_;
  private static int current_vessel_capacity_ = 0;
  private static int current_body_capacity_ = 0;

  public static void EnsureBuffers(int body_count, int vessel_count) {
    if (vessels_buffer_ != null &&
        current_vessel_capacity_ >= vessel_count &&
        current_body_capacity_ >= body_count) {
      return;
    }
    ReleaseBuffers();

    current_vessel_capacity_ = Mathf.NextPowerOfTwo(Mathf.Max(64, vessel_count));
    current_body_capacity_ = Mathf.NextPowerOfTwo(Mathf.Max(16, body_count));

    occulting_bodies_buffer_ = new ComputeBuffer(
        current_body_capacity_,
        Marshal.SizeOf(typeof(OccultingBodyData)));
    vessels_buffer_ = new ComputeBuffer(
        current_vessel_capacity_,
        Marshal.SizeOf(typeof(SrpEvaluationInput)));
    outputs_buffer_ = new ComputeBuffer(
        current_vessel_capacity_,
        Marshal.SizeOf(typeof(SrpEvaluationOutput)));
  }

  public static void ReleaseBuffers() {
    if (occulting_bodies_buffer_ != null) {
      occulting_bodies_buffer_.Release();
      occulting_bodies_buffer_ = null;
    }
    if (vessels_buffer_ != null) {
      vessels_buffer_.Release();
      vessels_buffer_ = null;
    }
    if (outputs_buffer_ != null) {
      outputs_buffer_.Release();
      outputs_buffer_ = null;
    }
    current_vessel_capacity_ = 0;
    current_body_capacity_ = 0;
  }

  public static void Dispose() {
    ReleaseBuffers();
  }

  public static bool TryComputeSrp(
      CelestialBody sun,
      IList<CelestialBody> occulting_bodies,
      SrpEvaluationInput[] vessels,
      int vessel_count,
      out SrpEvaluationOutput[] outputs) {

    outputs = null;
    if (!HeterogeneousDynamicGovernor.IsGpuAccelerated ||
        !PrincipiaGPGPUContext.SupportsCompute ||
        sun == null || vessels == null || vessel_count == 0) {
      return false;
    }

    ComputeShader cs = PrincipiaGPGPUContext.solar_eclipse_occlusion;
    if (cs == null) {
      return false;
    }

    try {
      float start_time = Time.realtimeSinceStartup;
      IsEvaluating = true;

      int body_count = occulting_bodies?.Count ?? 0;
      EnsureBuffers(body_count, vessel_count);

      int k_srp = cs.FindKernel("ComputeMultiBodyEclipseAndSRP");

      if (body_count > 0) {
        var body_data = new OccultingBodyData[body_count];
        for (int i = 0; i < body_count; ++i) {
          var b = occulting_bodies[i];
          body_data[i] = new OccultingBodyData {
            position_m = (Vector3)b.position,
            radius_m = (float)b.Radius
          };
        }
        occulting_bodies_buffer_.SetData(body_data, 0, 0, body_count);
      }

      vessels_buffer_.SetData(vessels, 0, 0, vessel_count);

      cs.SetVector("_SunPosition", (Vector3)sun.position);
      cs.SetFloat("_SunRadius", (float)sun.Radius);
      cs.SetInt("_OccultingBodyCount", body_count);
      cs.SetInt("_VesselCount", vessel_count);
      cs.SetFloat("_SolarFlux1AU", 1361.0f);

      cs.SetBuffer(k_srp, "_OccultingBodies", occulting_bodies_buffer_);
      cs.SetBuffer(k_srp, "_Vessels", vessels_buffer_);
      cs.SetBuffer(k_srp, "_SrpOutputs", outputs_buffer_);

      int groups = (vessel_count + ThreadGroupSize - 1) / ThreadGroupSize;
      cs.Dispatch(k_srp, groups, 1, 1);

      outputs = new SrpEvaluationOutput[vessel_count];
      outputs_buffer_.GetData(outputs, 0, 0, vessel_count);

      IsEvaluating = false;
      LastEvaluationDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
      GpuModuleTelemetry.RecordDispatch(
          GpuModuleId.SolarRadiationPressure,
          LastEvaluationDurationMs,
          $"评估完成: {vessel_count} 载具光压, {body_count} 遮挡天体");
      return true;
    } catch (Exception ex) {
      Log.Error($"[GPGPU] Exception in SRP flux evaluation: {ex}");
      IsEvaluating = false;
      return false;
    }
  }
}

}
