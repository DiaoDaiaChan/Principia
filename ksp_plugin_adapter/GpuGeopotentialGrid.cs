using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public static class GpuGeopotentialGrid {
  private const int ThreadGroupSize = 8;
  public const int DefaultGridDim = 64;
  public const int MaxSupportedDegree = 128;

  public static RenderTexture PotentialVolumeTexture { get; private set; }
  public static ComputeBuffer AccelerationBuffer { get; private set; }
  public static bool IsEvaluating { get; private set; }
  public static float LastEvaluationDurationMs { get; private set; }

  private static ComputeBuffer zonal_j_buffer_;
  private static int current_dim_ = 0;

  public static void EnsureResources(int dim) {
    if (PotentialVolumeTexture != null && current_dim_ == dim) {
      return;
    }
    ReleaseResources();
    current_dim_ = dim;

    PotentialVolumeTexture = new RenderTexture(dim, dim, 0, RenderTextureFormat.RFloat) {
      dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
      volumeDepth = dim,
      enableRandomWrite = true,
      filterMode = FilterMode.Trilinear,
      wrapMode = TextureWrapMode.Clamp
    };
    PotentialVolumeTexture.Create();

    int total_voxels = dim * dim * dim;
    AccelerationBuffer = new ComputeBuffer(total_voxels, Marshal.SizeOf(typeof(Vector3)));
    zonal_j_buffer_ = new ComputeBuffer(MaxSupportedDegree + 1, sizeof(float));
  }

  public static void ReleaseResources() {
    if (PotentialVolumeTexture != null) {
      PotentialVolumeTexture.Release();
      UnityEngine.Object.Destroy(PotentialVolumeTexture);
      PotentialVolumeTexture = null;
    }
    if (AccelerationBuffer != null) {
      AccelerationBuffer.Release();
      AccelerationBuffer = null;
    }
    if (zonal_j_buffer_ != null) {
      zonal_j_buffer_.Release();
      zonal_j_buffer_ = null;
    }
    current_dim_ = 0;
  }

  public static bool TryComputeGeopotential(
      CelestialBody centre,
      Vector3 grid_origin,
      float voxel_spacing,
      int grid_dim = DefaultGridDim,
      int degree = 70,
      float[] j_coeffs = null) {

    if (!HeterogeneousDynamicGovernor.IsGpuAccelerated ||
        !PrincipiaGPGPUContext.SupportsCompute ||
        centre == null) {
      return false;
    }

    ComputeShader cs = PrincipiaGPGPUContext.high_degree_geopotential_grid;
    if (cs == null) {
      return false;
    }

    try {
      float start_time = Time.realtimeSinceStartup;
      IsEvaluating = true;
      EnsureResources(grid_dim);

      int k_pot = cs.FindKernel("ComputeGeopotentialField3D");
      int k_acc = cs.FindKernel("ComputeGeopotentialAccelerationField3D");

      int eval_degree = Mathf.Clamp(degree, 2, MaxSupportedDegree);
      var j_array = new float[MaxSupportedDegree + 1];
      if (j_coeffs != null) {
        int copy_len = Mathf.Min(j_coeffs.Length, eval_degree + 1);
        Array.Copy(j_coeffs, j_array, copy_len);
      } else {
        // Default J2 oblateness if none provided
        j_array[2] = 1.08263e-3f; // Earth-like J2
        j_array[3] = -2.54e-6f;
        j_array[4] = -1.61e-6f;
      }
      zonal_j_buffer_.SetData(j_array);

      cs.SetVector("_BodyCenter", (Vector3)centre.position);
      cs.SetFloat("_BodyReferenceRadius", (float)centre.Radius);
      cs.SetFloat("_GravitationalParameter", (float)centre.gravParameter);
      cs.SetInt("_MaxDegree", eval_degree);
      cs.SetVector("_GridOrigin", grid_origin);
      cs.SetFloat("_VoxelSpacing", voxel_spacing);
      cs.SetInts("_GridSize", grid_dim, grid_dim, grid_dim);
      cs.SetFloat("_BodySpinAngleRad", (float)(centre.rotationAngle * Mathf.Deg2Rad));

      cs.SetBuffer(k_pot, "_ZonalHarmonicsJ", zonal_j_buffer_);
      cs.SetTexture(k_pot, "_PotentialVolume", PotentialVolumeTexture);

      int groups = (grid_dim + ThreadGroupSize - 1) / ThreadGroupSize;
      cs.Dispatch(k_pot, groups, groups, groups);

      cs.SetTexture(k_acc, "_PotentialVolume", PotentialVolumeTexture);
      cs.SetBuffer(k_acc, "_AccelerationGrid", AccelerationBuffer);
      cs.Dispatch(k_acc, groups, groups, groups);

      IsEvaluating = false;
      LastEvaluationDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
      GpuModuleTelemetry.RecordDispatch(
          GpuModuleId.GeopotentialGrid,
          LastEvaluationDurationMs,
          $"{grid_dim}³ 体网格 ({grid_dim * grid_dim * grid_dim:N0} 体素) 70 阶球谐展开计算完成");
      return true;
    } catch (Exception ex) {
      Log.Error($"[GPGPU] Exception in geopotential volume calculation: {ex}");
      IsEvaluating = false;
      return false;
    }
  }
}

}
