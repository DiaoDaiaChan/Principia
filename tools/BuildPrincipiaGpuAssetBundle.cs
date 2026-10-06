// BuildPrincipiaGpuAssetBundle.cs - Unity Editor Tool for building the GPGPU AssetBundle
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace principia.tools {

public static class BuildPrincipiaGpuAssetBundle {
  [MenuItem("Principia/Build GPGPU AssetBundle")]
  public static void BuildAssetBundle() {
    string envPath = System.Environment.GetEnvironmentVariable("PRINCIPIA_BUNDLE_OUTPUT_DIR");
    string outputPath = !string.IsNullOrEmpty(envPath)
        ? envPath
        : Path.GetFullPath(Path.Combine(Application.dataPath, "../../Release/GameData/Principia/assets"));

    if (!Directory.Exists(outputPath)) {
      Directory.CreateDirectory(outputPath);
    }

    AssetBundleBuild[] buildMap = new AssetBundleBuild[1];
    buildMap[0].assetBundleName = "principia_gpu.bundle";
    buildMap[0].assetNames = new string[] {
      "Assets/PrincipiaShaders/FlightPlanShootingOptimizer.compute",
      "Assets/PrincipiaShaders/BatchOrbitIntegrator.compute",
      "Assets/PrincipiaShaders/TrajectoryTessellator.compute",
      "Assets/PrincipiaShaders/TrajectoryProcedural.shader",
    };

    BuildPipeline.BuildAssetBundles(
        outputPath,
        buildMap,
        BuildAssetBundleOptions.None,
        BuildTarget.StandaloneWindows64);

    Debug.Log($"[Principia] GPGPU AssetBundle successfully built at {outputPath}/principia_gpu.bundle");
  }
}

}
#endif
