using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace principia {
namespace ksp_plugin_adapter {

internal static class SidecarStorage {
  private static readonly byte[] MagicHeader =
      Encoding.ASCII.GetBytes("PRNC_SIDECAR_V1\0");
  private static readonly byte[] MagicFooter =
      Encoding.ASCII.GetBytes("PRNCDONE");

  public const int CurrentFormatVersion = 1;

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

    // 1. Check active save folder
    string primary_path = Path.Combine(GetSaveDirectory(), sidecar_filename);
    if (File.Exists(primary_path)) {
      return primary_path;
    }

    // 2. Check PluginStates global folder
    string global_path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData",
                                      "Principia", "PluginStates",
                                      sidecar_filename);
    if (File.Exists(global_path)) {
      return global_path;
    }

    // 3. Search all save folders if save folder was renamed or moved
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
    chunk_count = 0;
    total_bytes = 0;

    string save_dir = GetSaveDirectory();
    if (!Directory.Exists(save_dir)) {
      Directory.CreateDirectory(save_dir);
    }

    string sidecar_guid = Guid.NewGuid().ToString("N");
    string sidecar_filename = $"principia_{sidecar_guid}.principia_cache";
    string full_path = Path.Combine(save_dir, sidecar_filename);

    if (string.IsNullOrEmpty(compression)) {
      compression = "gipfeli";
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    using (var fs = new FileStream(full_path, FileMode.Create,
                                   FileAccess.Write, FileShare.None,
                                   bufferSize: 1048576))
    using (var writer = new BinaryWriter(fs, Encoding.UTF8)) {
      writer.Write(MagicHeader);
      writer.Write(CurrentFormatVersion);
      writer.Write(compression);

      long chunk_count_placeholder_pos = fs.Position;
      writer.Write((int)0); // Placeholder for chunk count

      IntPtr serializer = IntPtr.Zero;
      for (;;) {
        string serialization = plugin.SerializePlugin(
            ref serializer,
            compression,
            encoder: "base64");
        if (serialization == null) {
          break;
        }

        byte[] raw_bytes = Convert.FromBase64String(serialization);
        writer.Write(raw_bytes.Length);
        writer.Write(raw_bytes);
        total_bytes += raw_bytes.Length;
        chunk_count++;
      }

      // Fill in actual chunk count
      fs.Seek(chunk_count_placeholder_pos, SeekOrigin.Begin);
      writer.Write(chunk_count);
      fs.Seek(0, SeekOrigin.End);

      writer.Write(MagicFooter);
      writer.Flush();
    }

    stopwatch.Stop();
    Log.Info($"Principia sidecar saved: {sidecar_filename} " +
             $"({chunk_count} chunks, {total_bytes} bytes in {stopwatch.ElapsedMilliseconds} ms)");

    // Clean up older unreferenced sidecars in the background
    try {
      PruneOrphanedSidecars(save_dir);
    } catch (Exception e) {
      Log.Warning("Non-fatal exception while pruning old sidecars: " + e);
    }

    return sidecar_filename;
  }

  public static bool LoadSidecar(string sidecar_filename,
                                 ref IntPtr deserializer,
                                 ref IntPtr plugin_reader,
                                 string default_compression) {
    string full_path = FindSidecarPath(sidecar_filename);
    if (full_path == null || !File.Exists(full_path)) {
      Log.Error($"Could not find Principia sidecar file: {sidecar_filename}");
      return false;
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    using (var fs = new FileStream(full_path, FileMode.Open,
                                   FileAccess.Read, FileShare.Read,
                                   bufferSize: 1048576))
    using (var reader = new BinaryReader(fs, Encoding.UTF8)) {
      byte[] header = reader.ReadBytes(MagicHeader.Length);
      if (header.Length != MagicHeader.Length) {
        Log.Error("Sidecar header truncated");
        return false;
      }
      for (int i = 0; i < MagicHeader.Length; ++i) {
        if (header[i] != MagicHeader[i]) {
          Log.Error("Invalid Principia sidecar magic header");
          return false;
        }
      }

      int version = reader.ReadInt32();
      if (version > CurrentFormatVersion) {
        Log.Error($"Unsupported sidecar format version: {version}");
        return false;
      }

      string compression = reader.ReadString();
      if (string.IsNullOrEmpty(compression)) {
        compression = default_compression ?? "gipfeli";
      }

      int chunk_count = reader.ReadInt32();
      Log.Info($"Loading Principia sidecar with {chunk_count} chunks (v{version}, {compression})");

      for (int i = 0; i < chunk_count; ++i) {
        int length = reader.ReadInt32();
        byte[] raw_bytes = reader.ReadBytes(length);
        string chunk_base64 = Convert.ToBase64String(raw_bytes);
        Interface.DeserializePlugin(chunk_base64,
                                    ref deserializer,
                                    ref plugin_reader,
                                    compression,
                                    encoder: "base64");
      }

      // Signal end of stream
      Interface.DeserializePlugin("",
                                  ref deserializer,
                                  ref plugin_reader,
                                  compression,
                                  encoder: "base64");
    }

    stopwatch.Stop();
    Log.Info($"Principia sidecar loaded in {stopwatch.ElapsedMilliseconds} ms");
    return true;
  }

  private static void PruneOrphanedSidecars(string save_dir) {
    if (!Directory.Exists(save_dir)) {
      return;
    }

    string[] cache_files = Directory.GetFiles(save_dir, "principia_*.principia_cache");
    if (cache_files.Length <= 15) {
      return; // Keep recent cache files without scanning
    }

    // Collect all referenced sidecar filenames from .sfs files in save_dir
    var active_sidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        // Skip unreadable sfs file
      }
    }

    DateTime threshold = DateTime.UtcNow.AddDays(-2);
    foreach (string cache_path in cache_files) {
      string filename = Path.GetFileName(cache_path);
      if (!active_sidecars.Contains(filename)) {
        DateTime last_write = File.GetLastWriteTimeUtc(cache_path);
        if (last_write < threshold || cache_files.Length > 40) {
          try {
            File.Delete(cache_path);
            Log.Info($"Pruned orphaned sidecar cache: {filename}");
          } catch {
            // Ignore deletion errors for locked files
          }
        }
      }
    }
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
