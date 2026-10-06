using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace principia {
namespace ksp_plugin_adapter {

internal static class SmoothPlottingBridge {
  private static bool? external_mod_detected_ = null;
  private static bool smooth_interpolation_enabled_ = true;

  // Scratch buffers for Catmull-Rom spline evaluation (zero GC allocation)
  private static Vector3[] scratch_subdivided_ = new Vector3[65536];

  // Telemetry metrics
  private static long total_subdivisions_performed_ = 0;
  private static long total_subdivided_vertices_ = 0;

  public static bool IsExternalModDetected {
    get {
      if (!external_mod_detected_.HasValue) {
        external_mod_detected_ = DetectExternalMod();
      }
      return external_mod_detected_.Value;
    }
  }

  public static bool SmoothInterpolationEnabled {
    get => smooth_interpolation_enabled_;
    set => smooth_interpolation_enabled_ = value;
  }

  public static long SubdivisionsPerformed => total_subdivisions_performed_;
  public static long SubdividedVertices => total_subdivided_vertices_;

  private static bool DetectExternalMod() {
    try {
      foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
        string name = assembly.GetName().Name;
        if (name.IndexOf("SmoothPlotting", StringComparison.OrdinalIgnoreCase) >= 0) {
          return true;
        }
      }
    } catch {
      // Ignored: fallback to false on security/reflection constraints
    }
    return false;
  }

  /// <summary>
  /// Evaluates Catmull-Rom spline interpolation between 4 control points at parameter t in [0, 1].
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static Vector3 EvaluateCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t) {
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5f * (
        (2.0f * p1) +
        (-p0 + p2) * t +
        (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2 +
        (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3);
  }

  /// <summary>
  /// Applies adaptive Catmull-Rom smoothing to a sequence of trajectory points.
  /// Generates a smooth C1-continuous curve while strictly preserving memory and GC bounds.
  /// </summary>
  public static unsafe bool TrySmoothTrajectory(Vector3[] in_vertices,
                                                int in_count,
                                                int subdivisions_per_segment,
                                                out Vector3[] out_vertices,
                                                out int out_count) {
    out_vertices = in_vertices;
    out_count = in_count;

    if (!smooth_interpolation_enabled_ || in_vertices == null || in_count < 4 || subdivisions_per_segment <= 1) {
      return false;
    }

    int expected_count = (in_count - 1) * subdivisions_per_segment + 1;
    if (expected_count > scratch_subdivided_.Length) {
      int new_len = Math.Max(expected_count, scratch_subdivided_.Length * 2);
      Array.Resize(ref scratch_subdivided_, new_len);
    }

    int dst_idx = 0;
    fixed (Vector3* p_src = in_vertices)
    fixed (Vector3* p_dst = scratch_subdivided_) {
      for (int i = 0; i < in_count - 1; ++i) {
        Vector3 p0 = (i == 0) ? p_src[0] : p_src[i - 1];
        Vector3 p1 = p_src[i];
        Vector3 p2 = p_src[i + 1];
        Vector3 p3 = (i + 2 < in_count) ? p_src[i + 2] : p2;

        float step = 1.0f / subdivisions_per_segment;
        for (int s = 0; s < subdivisions_per_segment; ++s) {
          float t = s * step;
          p_dst[dst_idx++] = EvaluateCatmullRom(p0, p1, p2, p3, t);
        }
      }
      p_dst[dst_idx++] = p_src[in_count - 1];
    }

    out_vertices = scratch_subdivided_;
    out_count = dst_idx;

    total_subdivisions_performed_++;
    total_subdivided_vertices_ += out_count;
    return true;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
