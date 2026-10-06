using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public static class GpuConjunctionMonitor {
  private const int TileSize = 256;
  public const int MaxAlertsCapacity = 1024;

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct OrbitalDebrisObject {
    public uint object_id;
    public Vector3 position_m;
    public Vector3 velocity_m_s;
    public float periapsis_radius_m;
    public float apoapsis_radius_m;
    public float collision_radius_m;
  }

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public struct ConjunctionAlert {
    public uint object_a_id;
    public uint object_b_id;
    public float distance_m;
    public float relative_speed_m_s;
  }

  public static bool IsMonitoring { get; private set; }
  public static float LastScanDurationMs { get; private set; }
  public static int LastDetectedAlertCount { get; private set; }

  private static ComputeBuffer debris_buffer_;
  private static ComputeBuffer alerts_buffer_;
  private static ComputeBuffer counter_buffer_;
  private static int current_capacity_ = 0;

  public static void EnsureBuffers(int object_count) {
    if (debris_buffer_ != null && current_capacity_ >= object_count) {
      return;
    }
    ReleaseBuffers();

    current_capacity_ = Mathf.NextPowerOfTwo(Mathf.Max(256, object_count));
    debris_buffer_ = new ComputeBuffer(
        current_capacity_,
        Marshal.SizeOf(typeof(OrbitalDebrisObject)));
    alerts_buffer_ = new ComputeBuffer(
        MaxAlertsCapacity,
        Marshal.SizeOf(typeof(ConjunctionAlert)));
    counter_buffer_ = new ComputeBuffer(1, sizeof(uint));
  }

  public static void ReleaseBuffers() {
    if (debris_buffer_ != null) {
      debris_buffer_.Release();
      debris_buffer_ = null;
    }
    if (alerts_buffer_ != null) {
      alerts_buffer_.Release();
      alerts_buffer_ = null;
    }
    if (counter_buffer_ != null) {
      counter_buffer_.Release();
      counter_buffer_ = null;
    }
    current_capacity_ = 0;
  }

  public static void Dispose() {
    ReleaseBuffers();
  }

  public static bool TryFilterConjunctions(
      IList<Vessel> vessels,
      float warning_distance_m,
      out ConjunctionAlert[] alerts) {

    alerts = null;
    if (!HeterogeneousDynamicGovernor.IsGpuAccelerated ||
        !PrincipiaGPGPUContext.SupportsCompute ||
        vessels == null || vessels.Count < 2) {
      return false;
    }

    ComputeShader cs = PrincipiaGPGPUContext.all_pairs_conjunction_broadphase;
    if (cs == null) {
      return false;
    }

    try {
      float start_time = Time.realtimeSinceStartup;
      IsMonitoring = true;

      int count = vessels.Count;
      EnsureBuffers(count);

      int k_filter = cs.FindKernel("BroadphaseFilterAllPairs");

      var objects = new OrbitalDebrisObject[count];
      for (int i = 0; i < count; ++i) {
        var v = vessels[i];
        if (v == null || v.orbit == null) continue;
        objects[i] = new OrbitalDebrisObject {
          object_id = (uint)i,
          position_m = (Vector3)v.GetWorldPos3D(),
          velocity_m_s = (Vector3)v.GetObtVelocity(),
          periapsis_radius_m = (float)(v.orbit.PeR),
          apoapsis_radius_m = (float)(v.orbit.ApR),
          collision_radius_m = 10.0f
        };
      }

      debris_buffer_.SetData(objects, 0, 0, count);

      uint[] reset_counter = new uint[1] { 0 };
      counter_buffer_.SetData(reset_counter);

      cs.SetInt("_TotalObjects", count);
      cs.SetFloat("_WarningDistanceThresholdMeters", warning_distance_m);
      cs.SetFloat("_RelativeSpeedCap", 20000.0f);
      cs.SetInt("_MaxOutputAlerts", MaxAlertsCapacity);

      cs.SetBuffer(k_filter, "_DebrisObjects", debris_buffer_);
      cs.SetBuffer(k_filter, "_AlertPairs", alerts_buffer_);
      cs.SetBuffer(k_filter, "_AlertCounter", counter_buffer_);

      int groups = (count + TileSize - 1) / TileSize;
      cs.Dispatch(k_filter, groups, 1, 1);

      uint[] alert_count = new uint[1];
      counter_buffer_.GetData(alert_count);
      int valid_alerts = Mathf.Min((int)alert_count[0], MaxAlertsCapacity);
      LastDetectedAlertCount = valid_alerts;

      if (valid_alerts > 0) {
        alerts = new ConjunctionAlert[valid_alerts];
        alerts_buffer_.GetData(alerts, 0, 0, valid_alerts);
      } else {
        alerts = Array.Empty<ConjunctionAlert>();
      }

      IsMonitoring = false;
      LastScanDurationMs = (Time.realtimeSinceStartup - start_time) * 1000.0f;
      GpuModuleTelemetry.RecordDispatch(
          GpuModuleId.ConjunctionMonitor,
          LastScanDurationMs,
          $"广相筛选完成: {count} 碎片 ({count * (count - 1L) / 2L:N0} 对), 告警: {valid_alerts} 处");
      return true;
    } catch (Exception ex) {
      Log.Error($"[GPGPU] Exception in debris conjunction broadphase scan: {ex}");
      IsMonitoring = false;
      return false;
    }
  }
}

}
