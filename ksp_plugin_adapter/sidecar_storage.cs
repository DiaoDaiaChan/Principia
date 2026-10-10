using System;
using System.Collections.Generic;
using System.IO;

namespace principia {
namespace ksp_plugin_adapter {

internal static class SidecarStorage {
  public const int CurrentFormatVersion = 2;

  private static volatile bool pending_quicksave_ = false;

  public static void NotifyAboutToQuicksave() {
    pending_quicksave_ = true;
  }

  public static string GetSaveDirectory() {
    string folder = HighLogic.SaveFolder;
    if (!string.IsNullOrEmpty(folder)) {
      return Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder);
    }
    return Path.Combine(KSPUtil.ApplicationRootPath, "saves", "default");
  }

  public static string SanitizeFileName(string name) {
    if (string.IsNullOrEmpty(name)) {
      return "";
    }
    string clean = name.Trim();
    foreach (char c in Path.GetInvalidFileNameChars()) {
      clean = clean.Replace(c, '_');
    }
    return clean;
  }

  public static string ResolveSaveStem(string explicit_hint = null) {
    if (!string.IsNullOrEmpty(explicit_hint)) {
      string clean_hint = SanitizeFileName(explicit_hint);
      if (!string.IsNullOrEmpty(clean_hint)) {
        return clean_hint;
      }
    }
    if (pending_quicksave_) {
      pending_quicksave_ = false;
      return "quicksave";
    }
    if (HighLogic.CurrentGame != null && !string.IsNullOrEmpty(HighLogic.CurrentGame.Title)) {
      string title = HighLogic.CurrentGame.Title.Trim();
      int mode_idx = title.LastIndexOf(" (");
      if (mode_idx > 0) {
        title = title.Substring(0, mode_idx).Trim();
      }
      string clean = SanitizeFileName(title);
      if (!string.IsNullOrEmpty(clean)) {
        return clean;
      }
    }
    return "persistent";
  }

  public static string FindSidecarPath(string sidecar_filename) {
    if (string.IsNullOrEmpty(sidecar_filename)) {
      return null;
    }
    string primary_path = Path.Combine(GetSaveDirectory(), sidecar_filename);
    if (File.Exists(primary_path)) {
      return primary_path;
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
                                   string save_stem_hint,
                                   out int chunk_count,
                                   out long total_bytes) {
    chunk_count = 1;
    total_bytes = 0;

    string save_dir = GetSaveDirectory();
    if (!Directory.Exists(save_dir)) {
      Directory.CreateDirectory(save_dir);
    }

    string stem = ResolveSaveStem(save_stem_hint);
    // 1:1 strongly bound companion sidecar filename:
    string sidecar_filename = $"{stem}.principia_bin";
    string full_path = Path.Combine(save_dir, sidecar_filename);

    if (string.IsNullOrEmpty(compression)) {
      compression = "gipfeli";
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    bool ok = Interface.PluginSaveToFile(plugin, full_path, compression);
    stopwatch.Stop();

    if (!ok || !File.Exists(full_path)) {
      Log.Error($"Failed to save Principia 1:1 sidecar to {full_path}");
      throw new IOException("Failed to save Principia binary state");
    }

    var fi = new FileInfo(full_path);
    total_bytes = fi.Length;
    Log.Info($"Principia 1:1 sidecar saved atomically to {sidecar_filename} " +
             $"({total_bytes} bytes in {stopwatch.ElapsedMilliseconds} ms)");

    // Save master ephemeris cache alongside in saves/<SaveFolder>/
    string master_ephem_path = Path.Combine(save_dir, "ephemeris_master.principia_cache");
    bool ephem_saved = Interface.SaveMasterEphemerisCache(master_ephem_path);
    if (ephem_saved) {
      Log.Info($"Principia master ephemeris cache saved to {master_ephem_path}");
    }

    // Purge legacy GUID cache and sidecar files to immediately reclaim disk space:
    try {
      PurgeLegacyGuidFiles(save_dir);
    } catch (Exception e) {
      Log.Warning("Non-fatal exception while purging legacy GUID files: " + e);
    }

    return sidecar_filename;
  }

  public static bool LoadSidecar(string sidecar_filename,
                                 out IntPtr plugin_reader,
                                 string default_compression) {
    plugin_reader = IntPtr.Zero;
    string full_path = FindSidecarPath(sidecar_filename);
    if (full_path == null || !File.Exists(full_path)) {
      // If requested file doesn't exist (e.g. was a legacy GUID), check 1:1 companion
      string save_dir = GetSaveDirectory();
      string stem = ResolveSaveStem(null);
      string fallback_path = Path.Combine(save_dir, $"{stem}.principia_bin");
      if (File.Exists(fallback_path)) {
        full_path = fallback_path;
        Log.Info($"Redirected missing sidecar to 1:1 companion: {fallback_path}");
      } else {
        // Try persistent.principia_bin
        string persistent_path = Path.Combine(save_dir, "persistent.principia_bin");
        if (File.Exists(persistent_path)) {
          full_path = persistent_path;
          Log.Info($"Fallback to persistent 1:1 companion: {persistent_path}");
        } else {
          Log.Error($"Could not find Principia sidecar file: {sidecar_filename}");
          return false;
        }
      }
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

    Log.Info($"Principia 1:1 sidecar loaded directly in {stopwatch.ElapsedMilliseconds} ms");

    // Pre-populate Ephemeris L3 Window Ring from master ephemeris cache (zero GC, zero cold start):
    string master_ephem_path = Path.Combine(GetSaveDirectory(), "ephemeris_master.principia_cache");
    if (File.Exists(master_ephem_path)) {
      bool ephem_loaded = Interface.LoadMasterEphemerisCache(master_ephem_path);
      if (ephem_loaded) {
        Log.Info($"Principia master ephemeris cache restored into L3 Window Ring: {master_ephem_path}");
      }
    }

    return true;
  }

  public static void PurgeLegacyGuidFiles(string save_dir) {
    if (!Directory.Exists(save_dir)) {
      return;
    }
    string[] files = Directory.GetFiles(save_dir, "principia_*.*");
    foreach (string file_path in files) {
      string filename = Path.GetFileName(file_path);
      // Legacy GUID format: principia_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX.principia_bin or .principia_cache
      if (filename.StartsWith("principia_", StringComparison.OrdinalIgnoreCase) &&
          (filename.EndsWith(".principia_bin", StringComparison.OrdinalIgnoreCase) ||
           filename.EndsWith(".principia_cache", StringComparison.OrdinalIgnoreCase))) {
        string without_prefix = filename.Substring("principia_".Length);
        int dot_idx = without_prefix.IndexOf('.');
        if (dot_idx == 32) { // 32-character GUID!
          try {
            File.Delete(file_path);
            Log.Info($"Purged legacy GUID cache file: {filename}");
          } catch {
          }
        }
      }
    }
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
