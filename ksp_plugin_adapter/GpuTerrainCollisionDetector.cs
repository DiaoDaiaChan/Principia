using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace principia.ksp_plugin_adapter {

public static class GpuTerrainCollisionDetector {
  private const int ThreadGroupSize = 64;

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct TrajectorySamplePoint {
    public Vector3 position_m;
    public float time_s;
    public Vector3 velocity_m_s;
    public float padding;
  }

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct CollisionHitResult {
    public uint has_collision;
    public float collision_time;
    public Vector3 collision_world_pos;
    public float latitude_rad;
    public float longitude_rad;
    public float terrain_altitude;
    public float slope_degrees;
    public float roughness_metric;
  }

  public static bool IsDetecting { get; private set; }
  public static float LastDetectionDurationMs { get; private set; }
  public static CollisionHitResult? LastHitResult { get; private set; }

  private static ComputeBuffer trajectory_buffer_;
  private static ComputeBuffer collision_hits_buffer_;
  private static int current_capacity_ = 0;
  private static readonly Dictionary<string, Texture2D> heightmap_cache_ =
      new Dictionary<string, Texture2D>();

  public static void EnsureBuffers(int sample_count) {
    if (trajectory_buffer_ != null && current_capacity_ >= sample_count) {
      return;
    }
    ReleaseBuffers();
    current_capacity_ = Mathf.NextPowerOfTwo(Mathf.Max(256, sample_count));
    trajectory_buffer_ = new ComputeBuffer(
        current_capacity_,
        Marshal.SizeOf(typeof(TrajectorySamplePoint)));
    collision_hits_buffer_ = new ComputeBuffer(
        current_capacity_,
        Marshal.SizeOf(typeof(CollisionHitResult)));
  }

  public static void ReleaseBuffers() {
    if (trajectory_buffer_ != null) {
      trajectory_buffer_.Release();
      trajectory_buffer_ = null;
    }
    if (collision_hits_buffer_ != null) {
      collision_hits_buffer_.Release();
      collision_hits_buffer_ = null;
    }
    current_capacity_ = 0;
  }

  public static void Dispose() {
    ReleaseBuffers();
    foreach (var tex in heightmap_cache_.Values) {
      if (tex != null) {
        UnityEngine.Object.Destroy(tex);
      }
    }
    heightmap_cache_.Clear();
  }

  public static Texture2D GetOrCreateHeightmap(CelestialBody centre, int resolution_x = 256, int resolution_y = 128) {
    if (centre == null) return null;
    if (heightmap_cache_.TryGetValue(centre.name, out var cached_tex) && cached_tex != null) {
      return cached_tex;
    }

    var tex = new Texture2D(resolution_x, resolution_y, TextureFormat.RFloat, false) {
      filterMode = FilterMode.Bilinear,
      wrapMode = TextureWrapMode.Repeat
    };

    // Correctly calculate maximum elevation above body mean radius:
    // PQS.radiusMax represents absolute radial distance from center.
    double max_elev;
    if (centre.pqsController != null) {
      max_elev = Math.Max(10.0, centre.pqsController.radiusMax - centre.Radius);
    } else if (centre.hasSolidSurface) {
      max_elev = Math.Max(1000.0, centre.Radius * 0.015);
    } else {
      max_elev = 1.0;
    }
    double range_r = Math.Max(1.0, max_elev);

    var pixels = new float[resolution_x * resolution_y];
    if (centre.hasSolidSurface) {
      for (int y = 0; y < resolution_y; ++y) {
        double lat = ((double)y / (resolution_y - 1) - 0.5) * Math.PI;
        double lat_deg = lat * (180.0 / Math.PI);
        for (int x = 0; x < resolution_x; ++x) {
          double lon = ((double)x / resolution_x) * 2.0 * Math.PI;
          double lon_deg = lon * (180.0 / Math.PI);
          double alt = 0.0;
          try {
            alt = centre.TerrainAltitude(lat_deg, lon_deg, allowNegative: !centre.ocean);
          } catch {
            alt = 0.0;
          }
          pixels[y * resolution_x + x] = Mathf.Clamp01((float)(alt / range_r));
        }
      }
    }
    tex.SetPixelData(pixels, 0);
    tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
    heightmap_cache_[centre.name] = tex;
    return tex;
  }

  public static bool TryDetectCollision(
      CelestialBody centre,
      TrajectorySamplePoint[] samples,
      int sample_count,
      out CollisionHitResult hit) {

    hit = default;
    if (!HeterogeneousDynamicGovernor.IsGpuAccelerated ||
        !PrincipiaGPGPUContext.SupportsCompute ||
        centre == null || samples == null || sample_count < 2) {
      return false;
    }

    ComputeShader cs = PrincipiaGPGPUContext.terrain_collision_raymarcher;
    if (cs == null) {
      return false;
    }

    try {
      float start_time = Time.realtimeSinceStartup;
      IsDetecting = true;
      EnsureBuffers(sample_count);

      int k_raymarch = cs.FindKernel("RaymarchTrajectoryCollision");
      Texture2D heightmap = GetOrCreateHeightmap(centre);

      trajectory_buffer_.SetData(samples, 0, 0, sample_count);

      double min_alt = 0;
      double max_alt = centre.pqsController != null
          ? Math.Max(0.0, centre.pqsController.radiusMax - centre.Radius)
          : (centre.hasSolidSurface ? Math.Max(1000.0, centre.Radius * 0.015) : 0.0);

      cs.SetVector("_BodyWorldCenter", (Vector3)centre.position);
      cs.SetFloat("_BodyMeanRadius", (float)centre.Radius);
      cs.SetFloat("_BodyMinRadius", (float)(centre.Radius + min_alt));
      cs.SetFloat("_BodyMaxRadius", (float)(centre.Radius + max_alt));
      cs.SetFloat("_BodyRotationAngleRad", (float)(centre.rotationAngle * Mathf.Deg2Rad));
      cs.SetInt("_SampleCount", sample_count);
      cs.SetInt("_MaxRaymarchSteps", 16);
      cs.SetFloat("_StepEpsilonMeters", 1.0f);

      cs.SetBuffer(k_raymarch, "_TrajectorySamples", trajectory_buffer_);
      if (heightmap != null) {
        cs.SetTexture(k_raymarch, "_HeightMap", heightmap);
      }
      cs.SetBuffer(k_raymarch, "_CollisionHits", collision_hits_buffer_);

      int groups_x = (sample_count - 1 + ThreadGroupSize - 1) / ThreadGroupSize;
      cs.Dispatch(k_raymarch, groups_x, 1, 1);

      var results = new CollisionHitResult[sample_count - 1];
      collision_hits_buffer_.GetData(results);

      IsDetecting = false;
      LastDetectionDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;

      for (int i = 0; i < results.Length; ++i) {
        if (results[i].has_collision != 0) {
          hit = results[i];
          LastHitResult = hit;
          GpuModuleTelemetry.RecordDispatch(
              GpuModuleId.TerrainCollisionDetector,
              LastDetectionDurationMs,
              $"撞击告警: 高程 {hit.terrain_altitude:F0}m, 坡度 {hit.slope_degrees:F1}°");
          return true;
        }
      }

      LastHitResult = null;
      GpuModuleTelemetry.RecordDispatch(
          GpuModuleId.TerrainCollisionDetector,
          LastDetectionDurationMs,
          $"轨迹安全: {sample_count} 点光线步进未发现地表撞击");
      return false;
    } catch (Exception ex) {
      Log.Error($"[GPGPU] Exception in GPU terrain collision detection: {ex}");
      IsDetecting = false;
      return false;
    }
  }
}

}
