using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace principia.ksp_plugin_adapter {

public static class PrincipiaGPGPUContext {
  private static AssetBundle gpu_bundle_;

  // Compute Shaders
  public static ComputeShader batch_orbit_integrator { get; private set; }
  public static ComputeShader flight_plan_shooting_optimizer { get; private set; }
  public static ComputeShader trajectory_tessellator { get; private set; }

  // Procedural Shaders & Materials
  public static Shader trajectory_procedural_shader { get; private set; }
  public static Material trajectory_procedural_material { get; private set; }

  // Persistent Buffers
  public static ComputeBuffer celestial_constants_buffer { get; internal set; }
  public static ComputeBuffer optimization_results_buffer { get; internal set; }

  public static bool IsInitialized { get; private set; }
  public static bool HasAssetBundleLoaded => gpu_bundle_ != null;

  // Hardware capability discovery
  public static bool SupportsCompute => SystemInfo.supportsComputeShaders;

  public static bool SupportsDoublePrecision =>
      SystemInfo.supportsComputeShaders &&
      SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2 &&
      SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3;

  public static bool SupportsAsyncReadback => SystemInfo.supportsAsyncGPUReadback;

  public static string GpuDeviceName => SystemInfo.graphicsDeviceName ?? "Unknown GPU";
  public static int GpuMemoryMB => SystemInfo.graphicsMemorySize;

  public static void EnsureInitialized() {
    if (!IsInitialized) {
      Initialize();
    }
  }

  public static void Initialize() {
    if (IsInitialized) return;

    Log.Info($"[GPGPU] Initializing Principia GPGPU Context on {GpuDeviceName} ({GpuMemoryMB} MB VRAM, Compute: {SupportsCompute}, AsyncReadback: {SupportsAsyncReadback})");

    if (!SupportsCompute) {
      Log.Warning("[GPGPU] System does not support Compute Shaders. GPGPU pipeline will remain disabled, falling back to CPU.");
      IsInitialized = true;
      return;
    }

    try {
      string env_bundle = System.Environment.GetEnvironmentVariable("PRINCIPIA_GPU_BUNDLE_PATH");
      string bundle_path = !string.IsNullOrEmpty(env_bundle) && File.Exists(env_bundle)
          ? env_bundle
          : Path.Combine(
              !string.IsNullOrEmpty(KSPUtil.ApplicationRootPath) ? KSPUtil.ApplicationRootPath : Directory.GetCurrentDirectory(),
              "GameData", "Principia", "assets", "principia_gpu.bundle");

      if (File.Exists(bundle_path)) {
        gpu_bundle_ = AssetBundle.LoadFromFile(bundle_path);
        if (gpu_bundle_ != null) {
          flight_plan_shooting_optimizer = gpu_bundle_.LoadAsset<ComputeShader>("FlightPlanShootingOptimizer");
          batch_orbit_integrator = gpu_bundle_.LoadAsset<ComputeShader>("BatchOrbitIntegrator");
          trajectory_tessellator = gpu_bundle_.LoadAsset<ComputeShader>("TrajectoryTessellator");
          trajectory_procedural_shader = gpu_bundle_.LoadAsset<Shader>("TrajectoryProcedural");
          Log.Info("[GPGPU] principia_gpu.bundle successfully loaded.");
        }
      } else {
        Log.Info($"[GPGPU] principia_gpu.bundle not found at {bundle_path}; runtime will use CPU fallback or simulated acceleration.");
      }

      if (trajectory_procedural_shader == null) {
        trajectory_procedural_shader = Shader.Find("Principia/TrajectoryProcedural");
      }
      if (trajectory_procedural_shader != null) {
        trajectory_procedural_material = new Material(trajectory_procedural_shader);
      }

      IsInitialized = true;
      Log.Info("[GPGPU] Context initialized successfully.");
    } catch (Exception e) {
      Log.Error($"[GPGPU] Exception during GPGPU Context initialization: {e}");
      Dispose();
    }
  }

  public static void Dispose() {
    if (celestial_constants_buffer != null) {
      celestial_constants_buffer.Release();
      celestial_constants_buffer = null;
    }

    if (optimization_results_buffer != null) {
      optimization_results_buffer.Release();
      optimization_results_buffer = null;
    }

    if (gpu_bundle_ != null) {
      try {
        gpu_bundle_.Unload(unloadAllLoadedObjects: true);
      } catch (Exception ex) {
        Log.Warning($"[GPGPU] Warning unloading GPU bundle: {ex.Message}");
      }
      gpu_bundle_ = null;
    }

    flight_plan_shooting_optimizer = null;
    batch_orbit_integrator = null;
    trajectory_tessellator = null;

    if (trajectory_procedural_material != null) {
      UnityEngine.Object.Destroy(trajectory_procedural_material);
      trajectory_procedural_material = null;
    }
    trajectory_procedural_shader = null;

    IsInitialized = false;
    Log.Info("[GPGPU] Principia GPGPU Context safely disposed.");
  }
}

}
