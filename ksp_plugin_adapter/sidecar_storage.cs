using System;
using System.Collections.Generic;
using System.IO;

namespace principia {
namespace ksp_plugin_adapter {

internal static class SidecarStorage {
  public const int CurrentFormatVersion = 2;

  public static string GetSaveDirectory() {
    string folder = HighLogic.SaveFolder;
    if (!string.IsNullOrEmpty(folder)) {
      return Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder);
    }
    return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "Principia",
                        "PluginStates");
  }

  public static string FindSidecarPath(string sidecar_filename) {
    if (string.IsNullOrEmpty(sidecar_filename)) {
      return null;
    }
    string primary_path = Path.Combine(GetSaveDirectory(), sidecar_filename);
    if (File.Exists(primary_path)) {
      return primary_path;
    }
    string global_path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData",
                                      "Principia", "PluginStates",
                                      sidecar_filename);
    if (File.Exists(global_path)) {
      return global_path;
    }
    string saves_root = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
    if (Directory.Exists(saves_root)) {
      try {
        var matches = Directory.GetFiles(saves_root, sidecar_filename,
                                         SearchOption.AllDirectories);
        if (matches.Length > 0) {
          return matches[0];
        }
      } catch (Exception e) {
        Log.Warning("Exception while searching for sidecar file: " + e);
      }
    }
    return null;
  }

  public static string SaveSidecar(IntPtr plugin,
                                   string compression,
                                   out int chunk_count,
                                   out long total_bytes) {
    chunk_count = 1;
    total_bytes = 0;

    string save_dir = GetSaveDirectory();
    if (!Directory.Exists(save_dir)) {
      Directory.CreateDirectory(save_dir);
    }

    string sidecar_guid = Guid.NewGuid().ToString("N");
    string sidecar_filename = $"principia_{sidecar_guid}.principia_bin";
    string full_path = Path.Combine(save_dir, sidecar_filename);

    if (string.IsNullOrEmpty(compression)) {
      compression = "gipfeli";
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    bool ok = Interface.PluginSaveToFile(plugin, full_path, compression);
    stopwatch.Stop();

    if (!ok || !File.Exists(full_path)) {
      Log.Error($"Failed to save Principia sidecar to {full_path}");
      throw new IOException("Failed to save Principia binary state");
    }

    var fi = new FileInfo(full_path);
    total_bytes = fi.Length;
    Log.Info($"Principia sidecar saved directly to {sidecar_filename} " +
             $"({total_bytes} bytes in {stopwatch.ElapsedMilliseconds} ms)");

    try {
      PruneOrphanedSidecars(save_dir, sidecar_filename);
    } catch (Exception e) {
      Log.Warning("Non-fatal exception while pruning old sidecars: " + e);
    }

    return sidecar_filename;
  }

  public static bool LoadSidecar(string sidecar_filename,
                                 out IntPtr plugin_reader,
                                 string default_compression) {
    plugin_reader = IntPtr.Zero;
    string full_path = FindSidecarPath(sidecar_filename);
    if (full_path == null || !File.Exists(full_path)) {
      Log.Error($"Could not find Principia sidecar file: {sidecar_filename}");
      return false;
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    string compressor = string.IsNullOrEmpty(default_compression) ? "gipfeli" : default_compression;
    bool ok = Interface.PluginLoadFromFile(
        full_path,
        compressor,
        out plugin_reader);
    stopwatch.Stop();

    if (!ok || plugin_reader == IntPtr.Zero) {
      Log.Error($"Failed to load Principia sidecar from {full_path}");
      return false;
    }

    Log.Info($"Principia sidecar loaded directly in {stopwatch.ElapsedMilliseconds} ms");
    return true;
  }

  private static void PruneOrphanedSidecars(string save_dir, string current_sidecar_filename) {
    if (!Directory.Exists(save_dir)) {
      return;
    }
    string[] cache_files = Directory.GetFiles(save_dir, "principia_*.principia_bin");
    if (cache_files.Length <= 15) {
      return;
    }
    var active_sidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrEmpty(current_sidecar_filename)) {
      active_sidecars.Add(current_sidecar_filename);
    }
    string[] sfs_files = Directory.GetFiles(save_dir, "*.sfs");
    foreach (string sfs_path in sfs_files) {
      try {
        using (var reader = new StreamReader(sfs_path)) {
          string line;
          while ((line = reader.ReadLine()) != null) {
            if (line.Contains("principia_sidecar_file")) {
              string[] parts = line.Split('=');
              if (parts.Length >= 2) {
                active_sidecars.Add(parts[1].Trim());
              }
            }
          }
        }
      } catch {
      }
    }
    DateTime recent_threshold = DateTime.UtcNow.AddHours(-2);
    DateTime old_threshold = DateTime.UtcNow.AddDays(-2);
    foreach (string cache_path in cache_files) {
      string filename = Path.GetFileName(cache_path);
      if (active_sidecars.Contains(filename)) {
        continue;
      }
      DateTime last_write = File.GetLastWriteTimeUtc(cache_path);
      if (last_write > recent_threshold) {
        continue;
      }
      if (last_write < old_threshold || cache_files.Length > 40) {
        try {
          File.Delete(cache_path);
          Log.Info($"Pruned orphaned sidecar: {filename}");
        } catch {
        }
      }
    }
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
