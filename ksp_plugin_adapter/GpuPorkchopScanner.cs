using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace principia.ksp_plugin_adapter {

public static class GpuPorkchopScanner {
  public const int DefaultGridResolution = 512;
  private const int GroupSizeX = 16;
  private const int GroupSizeY = 16;
  private const int GroupReduceSize = 256;

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct KeplerOrbitElements {
    public float semi_major_axis;
    public float eccentricity;
    public float inclination;
    public float lan;
    public float arg_pe;
    public float mean_anomaly_0;
    public float epoch;
    public float mu;
  }

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct PorkchopMinimumResult {
    public float departure_time;
    public float arrival_time;
    public float total_delta_v;
    public float departure_delta_v;
    public float arrival_delta_v;
    public float c3;
    public Vector3 v1_vector;
    public Vector3 v2_vector;
  }

  public static RenderTexture HeatmapTexture { get; private set; }
  public static bool IsScanning { get; private set; }
  public static float LastScanDurationMs { get; private set; }
  public static PorkchopMinimumResult BestTransfer { get; private set; }

  private static ComputeBuffer candidates_buffer_;
  private static ComputeBuffer final_minimum_buffer_;
  private static int current_resolution_ = 0;

  public static void EnsureResources(int resolution) {
    if (HeatmapTexture != null && current_resolution_ == resolution) {
      return;
    }

    ReleaseResources();

    current_resolution_ = resolution;
    HeatmapTexture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat) {
      enableRandomWrite = true,
      filterMode = FilterMode.Bilinear,
      wrapMode = TextureWrapMode.Clamp
    };
    HeatmapTexture.Create();

    int total_pixels = resolution * resolution;
    int group_count = (total_pixels + GroupReduceSize - 1) / GroupReduceSize;
    candidates_buffer_ = new ComputeBuffer(
        Mathf.Max(group_count, GroupReduceSize),
        Marshal.SizeOf(typeof(PorkchopMinimumResult)));
    final_minimum_buffer_ = new ComputeBuffer(1, Marshal.SizeOf(typeof(PorkchopMinimumResult)));
  }

  public static void ReleaseResources() {
    if (HeatmapTexture != null) {
      HeatmapTexture.Release();
      UnityEngine.Object.Destroy(HeatmapTexture);
      HeatmapTexture = null;
    }
    if (candidates_buffer_ != null) {
      candidates_buffer_.Release();
      candidates_buffer_ = null;
    }
    if (final_minimum_buffer_ != null) {
      final_minimum_buffer_.Release();
      final_minimum_buffer_ = null;
    }
    current_resolution_ = 0;
  }

  public static KeplerOrbitElements ExtractKeplerElements(Orbit orbit) {
    if (orbit == null || orbit.referenceBody == null) {
      return default;
    }
    return new KeplerOrbitElements {
      semi_major_axis = (float)orbit.semiMajorAxis,
      eccentricity = (float)orbit.eccentricity,
      inclination = (float)(orbit.inclination * Mathf.Deg2Rad),
      lan = (float)(orbit.LAN * Mathf.Deg2Rad),
      arg_pe = (float)(orbit.argumentOfPeriapsis * Mathf.Deg2Rad),
      mean_anomaly_0 = (float)orbit.meanAnomalyAtEpoch,
      epoch = (float)orbit.epoch,
      mu = (float)orbit.referenceBody.gravParameter
    };
  }

  public static bool TryDispatchScan(
      CelestialBody origin,
      CelestialBody target,
      double t_depart_min,
      double t_depart_max,
      double t_arrive_min,
      double t_arrive_max,
      Action<PorkchopMinimumResult> on_success = null,
      int resolution = DefaultGridResolution,
      float max_dv_cap = 15000.0f,
      float min_flight_time = 0.0f) {

    if (!HeterogeneousDynamicGovernor.IsGpuAccelerated ||
        !PrincipiaGPGPUContext.SupportsCompute ||
        origin == null || target == null ||
        origin.orbit == null || target.orbit == null ||
        origin.referenceBody != target.referenceBody) {
      return false;
    }

    ComputeShader cs = PrincipiaGPGPUContext.porkchop_lambert_scanner;
    if (cs == null) {
      return false;
    }

    try {
      float start_time = Time.realtimeSinceStartup;
      IsScanning = true;
      EnsureResources(resolution);

      int k_scan = cs.FindKernel("ScanPorkchopGrid");
      int k_reduce = cs.FindKernel("FindPorkchopMinimum");
      int k_reduce_final = cs.FindKernel("FindPorkchopMinimumFinal");

      KeplerOrbitElements origin_elem = ExtractKeplerElements(origin.orbit);
      KeplerOrbitElements target_elem = ExtractKeplerElements(target.orbit);
      float central_mu = (float)origin.referenceBody.gravParameter;

      if (min_flight_time <= 0.0f) {
        min_flight_time = (float)Math.Max(1800.0, (t_arrive_min - t_depart_min) * 0.5);
      }

      cs.SetFloat("_TDepartMin", (float)t_depart_min);
      cs.SetFloat("_TDepartMax", (float)t_depart_max);
      cs.SetFloat("_TArriveMin", (float)t_arrive_min);
      cs.SetFloat("_TArriveMax", (float)t_arrive_max);
      cs.SetInt("_GridWidth", resolution);
      cs.SetInt("_GridHeight", resolution);
      cs.SetFloat("_CentralBodyGM", central_mu);
      cs.SetFloat("_MaxDeltaVCap", max_dv_cap);
      cs.SetFloat("_MinFlightTime", min_flight_time);

      cs.SetTexture(k_scan, "_PorkchopHeatmap", HeatmapTexture);
      cs.Dispatch(k_scan, resolution / GroupSizeX, resolution / GroupSizeY, 1);

      // Parallel reduction to locate optimal transfer
      int total_pixels = resolution * resolution;
      int groups_x = (total_pixels + GroupReduceSize - 1) / GroupReduceSize;
      cs.SetTexture(k_reduce, "_PorkchopHeatmap", HeatmapTexture);
      cs.SetBuffer(k_reduce, "_MinimumCandidates", candidates_buffer_);
      cs.Dispatch(k_reduce, groups_x, 1, 1);

      cs.SetBuffer(k_reduce_final, "_MinimumCandidates", candidates_buffer_);
      cs.SetBuffer(k_reduce_final, "_FinalMinimum", final_minimum_buffer_);
      cs.Dispatch(k_reduce_final, 1, 1, 1);

      if (PrincipiaGPGPUContext.SupportsAsyncReadback) {
        AsyncGPUReadback.Request(final_minimum_buffer_, request => {
          IsScanning = false;
          LastScanDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
          if (!request.hasError) {
            var data = request.GetData<PorkchopMinimumResult>();
            if (data.Length > 0) {
              BestTransfer = data[0];
              GpuModuleTelemetry.RecordDispatch(
                  GpuModuleId.PorkchopScanner,
                  LastScanDurationMs,
                  $"{resolution}² 像元扫描完成, 最优 Δv: {BestTransfer.total_delta_v:F1} m/s (C3: {BestTransfer.c3:F1} km²/s²)");
              on_success?.Invoke(BestTransfer);
            }
          }
        });
      } else {
        var results = new PorkchopMinimumResult[1];
        final_minimum_buffer_.GetData(results);
        BestTransfer = results[0];
        IsScanning = false;
        LastScanDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
        GpuModuleTelemetry.RecordDispatch(
            GpuModuleId.PorkchopScanner,
            LastScanDurationMs,
            $"{resolution}² 像元扫描完成, 最优 Δv: {BestTransfer.total_delta_v:F1} m/s (C3: {BestTransfer.c3:F1} km²/s²)");
        on_success?.Invoke(BestTransfer);
      }

      return true;
    } catch (Exception ex) {
      Log.Error($"[GPGPU] Exception in Porkchop scan dispatch: {ex}");
      IsScanning = false;
      return false;
    }
  }
}

}
