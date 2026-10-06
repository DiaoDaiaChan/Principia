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
  public static ComputeShader wsb_bundle_evaluator { get; private set; }
  public static ComputeShader equipotential_marching_cubes { get; private set; }
  public static ComputeShader porkchop_lambert_scanner { get; private set; }
  public static ComputeShader terrain_collision_raymarcher { get; private set; }
  public static ComputeShader high_degree_geopotential_grid { get; private set; }
  public static ComputeShader solar_eclipse_occlusion { get; private set; }
  public static ComputeShader all_pairs_conjunction_broadphase { get; private set; }
  public static ComputeShader ground_track_swath_accumulator { get; private set; }
  public static ComputeShader aerodynamic_area_reduction { get; private set; }

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
          wsb_bundle_evaluator = gpu_bundle_.LoadAsset<ComputeShader>("WSBBundleEvaluator");
          equipotential_marching_cubes = gpu_bundle_.LoadAsset<ComputeShader>("EquipotentialMarchingCubes");
          porkchop_lambert_scanner = gpu_bundle_.LoadAsset<ComputeShader>("PorkchopLambertScanner");
          terrain_collision_raymarcher = gpu_bundle_.LoadAsset<ComputeShader>("TerrainCollisionRaymarcher");
          high_degree_geopotential_grid = gpu_bundle_.LoadAsset<ComputeShader>("HighDegreeGeopotentialGrid");
          solar_eclipse_occlusion = gpu_bundle_.LoadAsset<ComputeShader>("SolarEclipseOcclusion");
          all_pairs_conjunction_broadphase = gpu_bundle_.LoadAsset<ComputeShader>("AllPairsConjunctionBroadphase");
          ground_track_swath_accumulator = gpu_bundle_.LoadAsset<ComputeShader>("GroundTrackSwathAccumulator");
          aerodynamic_area_reduction = gpu_bundle_.LoadAsset<ComputeShader>("AerodynamicAreaReduction");
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
    wsb_bundle_evaluator = null;
    equipotential_marching_cubes = null;
    porkchop_lambert_scanner = null;
    terrain_collision_raymarcher = null;
    high_degree_geopotential_grid = null;
    solar_eclipse_occlusion = null;
    all_pairs_conjunction_broadphase = null;
    ground_track_swath_accumulator = null;
    aerodynamic_area_reduction = null;

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
