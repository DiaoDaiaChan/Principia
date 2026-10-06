using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace principia {
namespace ksp_plugin_adapter {

internal readonly struct FrameKey : IEquatable<FrameKey> {
  public readonly int extension;
  public readonly int centre_index;
  public readonly int primary_index;
  public readonly int secondary_index;

  public FrameKey(PlottingFrameParameters p) {
    if (p == null) {
      extension = -1;
      centre_index = -1;
      primary_index = -1;
      secondary_index = -1;
    } else {
      extension = p.extension;
      centre_index = p.centre_index;
      primary_index = p.primary_index != null && p.primary_index.Length > 0 ? p.primary_index[0] : -1;
      secondary_index = p.secondary_index != null && p.secondary_index.Length > 0 ? p.secondary_index[0] : -1;
    }
  }

  public FrameKey(int extension, int centre_index, int primary_index, int secondary_index) {
    this.extension = extension;
    this.centre_index = centre_index;
    this.primary_index = primary_index;
    this.secondary_index = secondary_index;
  }

  public bool Equals(FrameKey other) =>
      extension == other.extension &&
      centre_index == other.centre_index &&
      primary_index == other.primary_index &&
      secondary_index == other.secondary_index;

  public override bool Equals(object obj) => obj is FrameKey other && Equals(other);

  public override int GetHashCode() {
    unchecked {
      int hash = 17;
      hash = hash * 31 + extension;
      hash = hash * 31 + centre_index;
      hash = hash * 31 + primary_index;
      hash = hash * 31 + secondary_index;
      return hash;
    }
  }

  public override string ToString() =>
      $"E{extension}_C{centre_index}_P{primary_index}_S{secondary_index}";
}

internal readonly struct TrajectoryCacheKey : IEquatable<TrajectoryCacheKey> {
  public readonly FrameKey frame_key;
  public readonly int body_index;
  public readonly bool is_future;

  public TrajectoryCacheKey(FrameKey frame_key, int body_index, bool is_future) {
    this.frame_key = frame_key;
    this.body_index = body_index;
    this.is_future = is_future;
  }

  public bool Equals(TrajectoryCacheKey other) =>
      frame_key.Equals(other.frame_key) &&
      body_index == other.body_index &&
      is_future == other.is_future;

  public override bool Equals(object obj) =>
      obj is TrajectoryCacheKey other && Equals(other);

  public override int GetHashCode() {
    unchecked {
      int hash = 17;
      hash = hash * 31 + frame_key.GetHashCode();
      hash = hash * 31 + body_index;
      hash = hash * 31 + (is_future ? 1 : 0);
      return hash;
    }
  }

  public string ToFileName() =>
      $"celestial_{frame_key}_B{body_index}_{(is_future ? "fut" : "past")}.bin";

  public override string ToString() => ToFileName();
}

internal static class CelestialTrajectoryBinaryCache {
  private static readonly long MagicHeaderLong = 0x54534C43434E5250L; // "PRNCCLST" in ASCII
  public const int CurrentVersion = 1;

  // L1 In-Memory GPU Mesh Cache (persists across frame switches)
  private static readonly Dictionary<TrajectoryCacheKey, Plotter.CachedMeshEntry> l1_cache_ =
      new Dictionary<TrajectoryCacheKey, Plotter.CachedMeshEntry>();

  // Telemetry metrics
  public static long L1HitCount { get; private set; } = 0;
  public static long L2HitCount { get; private set; } = 0;
  public static long MissCount { get; private set; } = 0;

  public static long TotalRequests => L1HitCount + L2HitCount + MissCount;
  public static float HitRatio => TotalRequests > 0
      ? (float)(L1HitCount + L2HitCount) / TotalRequests * 100.0f
      : 100.0f;

  public static int L1CachedMeshCount => l1_cache_.Count;

  // Cached disk count and throttle timestamp (Zero-IO fast path for IMGUI)
  private static int cached_l2_disk_file_count_ = -1;
  private static float last_disk_scan_time_ = -100.0f;
  private const float DiskScanIntervalSeconds = 3.0f;

  public static int L2DiskFileCount {
    get {
      float now = UnityEngine.Time.realtimeSinceStartup;
      if (cached_l2_disk_file_count_ < 0 || now - last_disk_scan_time_ >= DiskScanIntervalSeconds) {
        last_disk_scan_time_ = now;
        try {
          string dir = GetCacheDirectory();
          if (Directory.Exists(dir)) {
            // Use EnumerateFiles to avoid heap allocation of string[] array.
            int count = 0;
            foreach (var _ in Directory.EnumerateFiles(dir, "celestial_*.bin")) {
              count++;
            }
            cached_l2_disk_file_count_ = count;
          } else {
            cached_l2_disk_file_count_ = 0;
          }
        } catch {
          if (cached_l2_disk_file_count_ < 0) {
            cached_l2_disk_file_count_ = 0;
          }
        }
      }
      return cached_l2_disk_file_count_;
    }
  }

  public static string GetCacheDirectory() {
    string env_cache = System.Environment.GetEnvironmentVariable("PRINCIPIA_CELESTIAL_CACHE_DIR");
    if (!string.IsNullOrEmpty(env_cache)) {
      return env_cache;
    }
    string root = !string.IsNullOrEmpty(KSPUtil.ApplicationRootPath)
        ? KSPUtil.ApplicationRootPath
        : Directory.GetCurrentDirectory();
    return Path.Combine(root, "GameData", "Principia", "PluginStates", "CelestialCache");
  }

  public static void RecordL1Hit() {
    L1HitCount++;
  }

  public static void RecordL2Hit() {
    L2HitCount++;
  }

  public static void RecordMiss() {
    MissCount++;
  }

  public static void ResetMetrics() {
    L1HitCount = 0;
    L2HitCount = 0;
    MissCount = 0;
  }

  public static Plotter.CachedMeshEntry GetOrCreateEntry(TrajectoryCacheKey key) {
    if (!l1_cache_.TryGetValue(key, out var entry)) {
      entry = new Plotter.CachedMeshEntry();
      l1_cache_[key] = entry;
    }
    return entry;
  }

  public static bool TryLoadFromDisk(TrajectoryCacheKey key,
                                     double current_ut,
                                     double max_drift,
                                     Plotter.CachedMeshEntry target_entry,
                                     Color colour,
                                     GLLines.Style style) {
    try {
      string dir = GetCacheDirectory();
      string file_path = Path.Combine(dir, key.ToFileName());
      if (!File.Exists(file_path)) {
        return false;
      }

      using (var fs = new FileStream(file_path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536))
      using (var reader = new BinaryReader(fs)) {
        long magic = reader.ReadInt64();
        if (magic != MagicHeaderLong) {
          return false;
        }
        int version = reader.ReadInt32();
        if (version != CurrentVersion) {
          return false;
        }

        int ext = reader.ReadInt32();
        int centre = reader.ReadInt32();
        int prim = reader.ReadInt32();
        int sec = reader.ReadInt32();
        int body = reader.ReadInt32();
        bool is_fut = reader.ReadBoolean();
        double update_ut = reader.ReadDouble();
        double min_dist = reader.ReadDouble();
        int count = reader.ReadInt32();
        double origin_x = reader.ReadDouble();
        double origin_y = reader.ReadDouble();
        double origin_z = reader.ReadDouble();

        // Expire if game time has drifted beyond maximum threshold
        if (Math.Abs(current_ut - update_ut) >= max_drift) {
          return false;
        }

        if (count <= 0 || count > 262144) {
          return false;
        }

        var vertices = new Vector3[count];
        for (int i = 0; i < count; ++i) {
          vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        var colors = new Color[count];
        for (int i = 0; i < count; ++i) {
          colors[i] = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        target_entry.SetMeshDirect(vertices, colors, count, min_dist, update_ut,
                                   new Vector3d(origin_x, origin_y, origin_z), style);
        RecordL2Hit();
        PerformanceProbe.RecordCacheHit();
        return true;
      }
    } catch (Exception e) {
      Log.Warning($"[CelestialCache] Failed to load binary cache for {key}: {e.Message}");
      return false;
    }
  }

  public static void SaveToDisk(TrajectoryCacheKey key, Plotter.CachedMeshEntry entry) {
    if (entry == null || !entry.is_valid || entry.vertex_count <= 0 || entry.compact_vertices == null) {
      return;
    }

    try {
      string dir = GetCacheDirectory();
      if (!Directory.Exists(dir)) {
        Directory.CreateDirectory(dir);
      }

      string file_path = Path.Combine(dir, key.ToFileName());
      bool is_new_file = !File.Exists(file_path);
      using (var fs = new FileStream(file_path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
      using (var writer = new BinaryWriter(fs)) {
        writer.Write(MagicHeaderLong);
        writer.Write(CurrentVersion);
        writer.Write(key.frame_key.extension);
        writer.Write(key.frame_key.centre_index);
        writer.Write(key.frame_key.primary_index);
        writer.Write(key.frame_key.secondary_index);
        writer.Write(key.body_index);
        writer.Write(key.is_future);
        writer.Write(entry.last_update_ut);
        writer.Write(entry.min_distance_from_camera);
        writer.Write(entry.vertex_count);
        writer.Write(entry.mesh_scaled_space_origin.x);
        writer.Write(entry.mesh_scaled_space_origin.y);
        writer.Write(entry.mesh_scaled_space_origin.z);

        int count = entry.vertex_count;
        for (int i = 0; i < count; ++i) {
          var v = entry.compact_vertices[i];
          writer.Write(v.x);
          writer.Write(v.y);
          writer.Write(v.z);
        }

        for (int i = 0; i < count; ++i) {
          var c = (entry.compact_colors != null && i < entry.compact_colors.Length)
              ? entry.compact_colors[i]
              : Color.white;
          writer.Write(c.r);
          writer.Write(c.g);
          writer.Write(c.b);
          writer.Write(c.a);
        }
      }
      if (is_new_file && cached_l2_disk_file_count_ >= 0) {
        cached_l2_disk_file_count_++;
      }
    } catch (Exception e) {
      Log.Warning($"[CelestialCache] Failed to save binary cache for {key}: {e.Message}");
    }
  }

  public static void ClearL1() {
    foreach (var pair in l1_cache_) {
      pair.Value.Destroy();
    }
    l1_cache_.Clear();
    Log.Info("[CelestialCache] Cleared L1 in-memory mesh cache");
  }

  public static void ClearDiskCache() {
    try {
      string dir = GetCacheDirectory();
      if (Directory.Exists(dir)) {
        var files = Directory.GetFiles(dir, "celestial_*.bin");
        for (int i = 0; i < files.Length; ++i) {
          try {
            File.Delete(files[i]);
          } catch {
            // Ignored
          }
        }
        Log.Info($"[CelestialCache] Deleted {files.Length} binary cache files from disk");
      }
    } catch (Exception e) {
      Log.Warning($"[CelestialCache] Failed to clear disk cache: {e.Message}");
    }
    cached_l2_disk_file_count_ = 0;
    last_disk_scan_time_ = UnityEngine.Time.realtimeSinceStartup;
    ClearL1();
    ResetMetrics();
  }
}

public struct CelestialStateRecord {
  public double ut;
  public int parent_index;
  public Vector3d position_from_parent;
  public Vector3d velocity_from_parent;
  public Vector3d world_position;
  public UnityEngine.QuaternionD rotation;
  public double radius;
  public bool is_valid;
}

internal static class CelestialPositionCache {
  private const int MaxTrackedBodies = 128;
  private static readonly CelestialStateRecord[] records_ = new CelestialStateRecord[MaxTrackedBodies];
  public static long TotalRecordUpdates { get; private set; } = 0;
  public static double LastUpdateUt { get; private set; } = -1.0;
  public static int CachedBodyCount { get; private set; } = 0;

  public static void RecordCelestialState(int body_index,
                                          int parent_index,
                                          double ut,
                                          Vector3d pos_from_parent,
                                          Vector3d vel_from_parent,
                                          Vector3d world_pos,
                                          UnityEngine.QuaternionD rot,
                                          double radius) {
    if (body_index < 0 || body_index >= MaxTrackedBodies) {
      return;
    }
    if (!records_[body_index].is_valid) {
      CachedBodyCount++;
    }
    records_[body_index].ut = ut;
    records_[body_index].parent_index = parent_index;
    records_[body_index].position_from_parent = pos_from_parent;
    records_[body_index].velocity_from_parent = vel_from_parent;
    records_[body_index].world_position = world_pos;
    records_[body_index].rotation = rot;
    records_[body_index].radius = radius;
    records_[body_index].is_valid = true;
    LastUpdateUt = ut;
    TotalRecordUpdates++;
  }

  public static bool TryGetCachedState(int body_index, out CelestialStateRecord state) {
    if (body_index >= 0 && body_index < MaxTrackedBodies && records_[body_index].is_valid) {
      state = records_[body_index];
      return true;
    }
    state = default;
    return false;
  }

  public static Vector3d GetCachedWorldPosition(int body_index, Vector3d fallback) {
    if (body_index >= 0 && body_index < MaxTrackedBodies && records_[body_index].is_valid) {
      return records_[body_index].world_position;
    }
    return fallback;
  }

  public static void Clear() {
    for (int i = 0; i < records_.Length; ++i) {
      records_[i] = default;
    }
    CachedBodyCount = 0;
    TotalRecordUpdates = 0;
    LastUpdateUt = -1.0;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia

