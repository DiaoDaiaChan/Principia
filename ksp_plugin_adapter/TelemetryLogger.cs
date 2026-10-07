using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

/// <summary>
/// Lightweight, real-time performance telemetry logger.
/// Periodically (default: 1 Hz) dumps complete Principia metrics,
/// rolling ephemeris driver status, and decoupler/governor telemetry to:
///  1) PrincipiaTelemetry.csv (easy for Excel / Python plotting)
///  2) PrincipiaTelemetry.jsonl (easy for Agent / automated query)
///  3) PrincipiaTelemetry_latest.json (instant Agent snapshot view)
/// </summary>
public static class TelemetryLogger {
  public static bool IsLoggingEnabled { get; set; } = true;
  public static double LogIntervalSeconds { get; set; } = 1.0;

  private static double last_log_realtime_ = -1.0;
  private static bool initialized_ = false;

  private static readonly List<string> output_directories_ = new List<string>();
  private static readonly object file_lock_ = new object();

  private const string CsvHeader =
      "timestamp,realtime_s,universal_time,fps,frametime_ms,gc_alloc_kb_f,principia_total_ms,unity_other_ms," +
      "precalc_ms,precalc_peak_ms,fixed_update_ms,fixed_update_peak_ms,waited_for_fixed_ms,waited_for_fixed_peak_ms," +
      "better_late_ms,better_late_peak_ms,late_update_ms,late_update_peak_ms," +
      "physics_advance_ms,physics_advance_peak_ms,vessel_harvest_ms,vessel_harvest_peak_ms," +
      "body_frames_ms,body_frames_peak_ms,plotting_ms,plotting_peak_ms," +
      "mesh_gen_ms,mesh_gen_peak_ms,flight_plan_eval_ms,flight_plan_eval_peak_ms," +
      "gpu_compute_ms,gpu_compute_peak_ms,celestial_plot_ms,celestial_plot_peak_ms," +
      "vessel_plot_ms,vessel_plot_peak_ms,equipotential_plot_ms,equipotential_plot_peak_ms," +
      "cache_hit_ratio,cache_hits,cache_misses,plot_invalidations,vertices_rendered," +
      "celestial_l1_mesh_count,celestial_l2_file_count,celestial_hit_ratio,celestial_l1_hits,celestial_l2_hits,celestial_misses," +
      "ephem_active,ephem_paused,ephem_prolonging,ephem_horizon_s,ephem_horizon_y,ephem_lookahead_y,ephem_refresh_threshold_y,ephem_progress_ratio,ephem_carrier," +
      "decouple_active,snapshots_consumed,tracked_entities,staged_harvest_count," +
      "gpu_name,supports_compute,zero_copy_enabled,zero_copy_direct_count,zero_copy_peak_vertices,zero_copy_capacity," +
      "governor_affinity,backpressure_active,gpu_dispatch_count,cpu_dispatch_count," +
      "vacuum_bypass_enabled,bypassed_precalc_count,bypassed_aero_count,executed_precalc_count," +
      "circuit_breaker_enabled,circuit_breaker_trips,rigid_motion_bypassed_parts,rigid_motion_executed_parts";

  public static void InitializeIfNeeded() {
    if (initialized_) return;
    initialized_ = true;

    try {
      // 1. KSP Application root directory
      string ksp_root = KSPUtil.ApplicationRootPath;
      if (!string.IsNullOrEmpty(ksp_root) && Directory.Exists(ksp_root)) {
        output_directories_.Add(ksp_root);
      }
    } catch {
      // Ignore if KSPUtil is unavailable during standalone unit test
    }

    // 2. Principia GitHub workspace directory (for instant agent access)
    const string DevWorkspace = @"c:\Users\43701\Documents\github\Principia";
    if (Directory.Exists(DevWorkspace) && !output_directories_.Contains(DevWorkspace)) {
      output_directories_.Add(DevWorkspace);
    }

    // Initialize CSV headers if files do not exist or are empty
    lock (file_lock_) {
      foreach (string dir in output_directories_) {
        try {
          string csv_path = Path.Combine(dir, "PrincipiaTelemetry.csv");
          if (!File.Exists(csv_path) || new FileInfo(csv_path).Length == 0) {
            File.WriteAllText(csv_path, CsvHeader + Environment.NewLine, Encoding.UTF8);
          }
        } catch (Exception ex) {
          Log.Warning($"[TelemetryLogger] Failed to initialize CSV header in {dir}: {ex.Message}");
        }
      }
    }
  }

  public static void Update() {
    if (!IsLoggingEnabled) return;

    double now = Time.realtimeSinceStartup;
    if (last_log_realtime_ >= 0 && now - last_log_realtime_ < LogIntervalSeconds) {
      return;
    }
    last_log_realtime_ = now;

    InitializeIfNeeded();
    RecordTelemetrySnapshot(now);
  }

  public static void ForceFlushSnapshot() {
    InitializeIfNeeded();
    RecordTelemetrySnapshot(Time.realtimeSinceStartup);
  }

  private static void RecordTelemetrySnapshot(double realtime_s) {
    try {
      string iso_time = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
      double ut = 0.0;
      try {
        ut = Planetarium.GetUniversalTime();
      } catch {
        ut = realtime_s;
      }

      // 1. Frame & Memory
      float fps = PerformanceProbe.CurrentFps;
      float frametime_ms = PerformanceProbe.FrameTimeMs;
      float gc_alloc = PerformanceProbe.GcAllocRateKbPerFrame;

      // 2. Lifecycle
      float precalc_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.UnityPrecalc);
      float precalc_peak = PerformanceProbe.GetPeakMs(ProbeCategory.UnityPrecalc);
      float fixed_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.UnityFixedUpdate);
      float fixed_peak = PerformanceProbe.GetPeakMs(ProbeCategory.UnityFixedUpdate);
      float waited_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.UnityWaitedForFixedUpdate);
      float waited_peak = PerformanceProbe.GetPeakMs(ProbeCategory.UnityWaitedForFixedUpdate);
      float better_late_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.UnityBetterLate);
      float better_late_peak = PerformanceProbe.GetPeakMs(ProbeCategory.UnityBetterLate);
      float late_update_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.UnityLateUpdate);
      float late_update_peak = PerformanceProbe.GetPeakMs(ProbeCategory.UnityLateUpdate);

      float principia_total_ms = precalc_ms + fixed_ms + waited_ms + better_late_ms + late_update_ms;
      float unity_other_ms = Mathf.Max(0.0f, frametime_ms - principia_total_ms);

      // 3. Subsystems
      float phys_adv_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.PhysicsAdvance);
      float phys_adv_peak = PerformanceProbe.GetPeakMs(ProbeCategory.PhysicsAdvance);
      float harvest_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.VesselHarvest);
      float harvest_peak = PerformanceProbe.GetPeakMs(ProbeCategory.VesselHarvest);
      float body_frames_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.BodyFramesUpdate);
      float body_frames_peak = PerformanceProbe.GetPeakMs(ProbeCategory.BodyFramesUpdate);
      float plotting_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.TrajectoryPlotting);
      float plotting_peak = PerformanceProbe.GetPeakMs(ProbeCategory.TrajectoryPlotting);
      float mesh_gen_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.MeshGeneration);
      float mesh_gen_peak = PerformanceProbe.GetPeakMs(ProbeCategory.MeshGeneration);
      float fp_eval_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.FlightPlanEvaluation);
      float fp_eval_peak = PerformanceProbe.GetPeakMs(ProbeCategory.FlightPlanEvaluation);
      float gpu_compute_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.GpuCompute);
      float gpu_compute_peak = PerformanceProbe.GetPeakMs(ProbeCategory.GpuCompute);
      float cel_plot_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.CelestialPlot);
      float cel_plot_peak = PerformanceProbe.GetPeakMs(ProbeCategory.CelestialPlot);
      float ves_plot_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.VesselPlot);
      float ves_plot_peak = PerformanceProbe.GetPeakMs(ProbeCategory.VesselPlot);
      float eq_plot_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.EquipotentialPlot);
      float eq_plot_peak = PerformanceProbe.GetPeakMs(ProbeCategory.EquipotentialPlot);

      // 4. Cache & Geometry
      float cache_hit_ratio = PerformanceProbe.CacheHitRatio;
      long cache_hits = PerformanceProbe.CacheHits;
      long cache_misses = PerformanceProbe.CacheMisses;
      long plot_invalidations = PerformanceProbe.PlotInvalidations;
      long vertices_rendered = PerformanceProbe.TotalVerticesRendered;

      int cel_l1_mesh = CelestialTrajectoryBinaryCache.L1CachedMeshCount;
      int cel_l2_files = CelestialTrajectoryBinaryCache.L2DiskFileCount;
      float cel_hit_ratio = CelestialTrajectoryBinaryCache.HitRatio;
      long cel_l1_hits = CelestialTrajectoryBinaryCache.L1HitCount;
      long cel_l2_hits = CelestialTrajectoryBinaryCache.L2HitCount;
      long cel_misses = CelestialTrajectoryBinaryCache.MissCount;

      // 5. Rolling Ephemeris Driver
      var driver = RollingEphemerisDriver.Instance;
      bool ephem_active = driver != null && driver.is_active;
      bool ephem_paused = driver != null && driver.is_paused;
      bool ephem_prolonging = driver != null && driver.is_prolonging;
      double ephem_horizon_s = driver != null ? driver.current_horizon_seconds : 0.0;
      double ephem_horizon_y = driver != null ? driver.current_horizon_years : 0.0;
      double ephem_lookahead_y = driver != null ? driver.lookahead_horizon_years : 0.0;
      double ephem_threshold_y = driver != null ? driver.refresh_threshold_years : 0.0;
      double ephem_progress = driver != null ? driver.progress_ratio : 0.0;
      string ephem_carrier = driver != null ? driver.carrier_vessel_name : "None";

      // 6. Decoupler & Governor
      bool decouple_active = HeterogeneousDynamicGovernor.EnableAutonomousPhysicsDecoupling;
      long snapshots_consumed = NativeStateSnapshotChannel.SnapshotsConsumed;
      int tracked_entities = NativeStateSnapshotChannel.TrackedEntityCount;
      long staged_harvest = HeterogeneousDynamicGovernor.StagedHarvestCount;

      string gpu_name = PrincipiaGPGPUContext.GpuDeviceName ?? "Unknown";
      bool supports_compute = PrincipiaGPGPUContext.SupportsCompute;
      bool zero_copy_enabled = HeterogeneousDynamicGovernor.EnableZeroCopyTrajectoryPipeline;
      long zero_copy_direct = HeterogeneousDynamicGovernor.ZeroCopyDirectCount;
      long zero_copy_peak_vert = NativeZeroCopyTrajectoryPipeline.PeakVertexCountObserved;
      int zero_copy_cap = NativeZeroCopyTrajectoryPipeline.CurrentCapacity;

      string affinity_str = HeterogeneousDynamicGovernor.Affinity.ToString();
      bool backpressure = HeterogeneousDynamicGovernor.IsBackpressureActive;
      long gpu_dispatches = HeterogeneousDynamicGovernor.GpuDispatchCount;
      long cpu_dispatches = HeterogeneousDynamicGovernor.CpuDispatchCount;

      bool vacuum_bypass = VacuumBypassGovernor.EnableVacuumBypass;
      long bypassed_precalc = VacuumBypassGovernor.BypassedPrecalcCount;
      long bypassed_aero = VacuumBypassGovernor.BypassedAeroCount;
      long executed_precalc = VacuumBypassGovernor.ExecutedPrecalcCount;

      bool circuit_breaker = HeterogeneousDynamicGovernor.EnableCircuitBreaker;
      long cb_trips = HeterogeneousDynamicGovernor.CircuitBreakerTrips;
      long rigid_bypassed = HeterogeneousDynamicGovernor.RigidMotionBypassedPartCount;
      long rigid_executed = HeterogeneousDynamicGovernor.RigidMotionExecutedPartCount;

      var inv = CultureInfo.InvariantCulture;

      // Construct CSV line
      string csv_line = string.Join(",",
          iso_time,
          realtime_s.ToString("F2", inv),
          ut.ToString("F1", inv),
          fps.ToString("F1", inv),
          frametime_ms.ToString("F2", inv),
          gc_alloc.ToString("F1", inv),
          principia_total_ms.ToString("F2", inv),
          unity_other_ms.ToString("F2", inv),
          precalc_ms.ToString("F3", inv),
          precalc_peak.ToString("F2", inv),
          fixed_ms.ToString("F3", inv),
          fixed_peak.ToString("F2", inv),
          waited_ms.ToString("F3", inv),
          waited_peak.ToString("F2", inv),
          better_late_ms.ToString("F3", inv),
          better_late_peak.ToString("F2", inv),
          late_update_ms.ToString("F3", inv),
          late_update_peak.ToString("F2", inv),
          phys_adv_ms.ToString("F3", inv),
          phys_adv_peak.ToString("F2", inv),
          harvest_ms.ToString("F3", inv),
          harvest_peak.ToString("F2", inv),
          body_frames_ms.ToString("F3", inv),
          body_frames_peak.ToString("F2", inv),
          plotting_ms.ToString("F3", inv),
          plotting_peak.ToString("F2", inv),
          mesh_gen_ms.ToString("F3", inv),
          mesh_gen_peak.ToString("F2", inv),
          fp_eval_ms.ToString("F3", inv),
          fp_eval_peak.ToString("F2", inv),
          gpu_compute_ms.ToString("F3", inv),
          gpu_compute_peak.ToString("F2", inv),
          cel_plot_ms.ToString("F3", inv),
          cel_plot_peak.ToString("F2", inv),
          ves_plot_ms.ToString("F3", inv),
          ves_plot_peak.ToString("F2", inv),
          eq_plot_ms.ToString("F3", inv),
          eq_plot_peak.ToString("F2", inv),
          cache_hit_ratio.ToString("F1", inv),
          cache_hits.ToString(inv),
          cache_misses.ToString(inv),
          plot_invalidations.ToString(inv),
          vertices_rendered.ToString(inv),
          cel_l1_mesh.ToString(inv),
          cel_l2_files.ToString(inv),
          cel_hit_ratio.ToString("F1", inv),
          cel_l1_hits.ToString(inv),
          cel_l2_hits.ToString(inv),
          cel_misses.ToString(inv),
          ephem_active ? "true" : "false",
          ephem_paused ? "true" : "false",
          ephem_prolonging ? "true" : "false",
          ephem_horizon_s.ToString("F0", inv),
          ephem_horizon_y.ToString("F2", inv),
          ephem_lookahead_y.ToString("F1", inv),
          ephem_threshold_y.ToString("F1", inv),
          ephem_progress.ToString("F3", inv),
          "\"" + ephem_carrier.Replace("\"", "\"\"") + "\"",
          decouple_active ? "true" : "false",
          snapshots_consumed.ToString(inv),
          tracked_entities.ToString(inv),
          staged_harvest.ToString(inv),
          "\"" + gpu_name.Replace("\"", "\"\"") + "\"",
          supports_compute ? "true" : "false",
          zero_copy_enabled ? "true" : "false",
          zero_copy_direct.ToString(inv),
          zero_copy_peak_vert.ToString(inv),
          zero_copy_cap.ToString(inv),
          affinity_str,
          backpressure ? "true" : "false",
          gpu_dispatches.ToString(inv),
          cpu_dispatches.ToString(inv),
          vacuum_bypass ? "true" : "false",
          bypassed_precalc.ToString(inv),
          bypassed_aero.ToString(inv),
          executed_precalc.ToString(inv),
          circuit_breaker ? "true" : "false",
          cb_trips.ToString(inv),
          rigid_bypassed.ToString(inv),
          rigid_executed.ToString(inv)
      );

      // Construct compact JSONL string
      string jsonl_line =
          "{" +
          $"\"timestamp\":\"{iso_time}\"," +
          $"\"realtime_s\":{realtime_s.ToString("F2", inv)}," +
          $"\"universal_time\":{ut.ToString("F1", inv)}," +
          "\"frame\":{" +
            $"\"fps\":{fps.ToString("F1", inv)}," +
            $"\"frametime_ms\":{frametime_ms.ToString("F2", inv)}," +
            $"\"gc_alloc_kb_f\":{gc_alloc.ToString("F1", inv)}," +
            $"\"principia_total_ms\":{principia_total_ms.ToString("F2", inv)}," +
            $"\"unity_other_ms\":{unity_other_ms.ToString("F2", inv)}" +
          "}," +
          "\"lifecycle\":{" +
            $"\"precalc_ms\":{precalc_ms.ToString("F3", inv)},\"precalc_peak_ms\":{precalc_peak.ToString("F2", inv)}," +
            $"\"fixed_update_ms\":{fixed_ms.ToString("F3", inv)},\"fixed_update_peak_ms\":{fixed_peak.ToString("F2", inv)}," +
            $"\"waited_for_fixed_ms\":{waited_ms.ToString("F3", inv)},\"waited_for_fixed_peak_ms\":{waited_peak.ToString("F2", inv)}," +
            $"\"better_late_ms\":{better_late_ms.ToString("F3", inv)},\"better_late_peak_ms\":{better_late_peak.ToString("F2", inv)}," +
            $"\"late_update_ms\":{late_update_ms.ToString("F3", inv)},\"late_update_peak_ms\":{late_update_peak.ToString("F2", inv)}" +
          "}," +
          "\"subsystems\":{" +
            $"\"physics_advance_ms\":{phys_adv_ms.ToString("F3", inv)},\"physics_advance_peak_ms\":{phys_adv_peak.ToString("F2", inv)}," +
            $"\"vessel_harvest_ms\":{harvest_ms.ToString("F3", inv)},\"vessel_harvest_peak_ms\":{harvest_peak.ToString("F2", inv)}," +
            $"\"body_frames_ms\":{body_frames_ms.ToString("F3", inv)},\"body_frames_peak_ms\":{body_frames_peak.ToString("F2", inv)}," +
            $"\"plotting_ms\":{plotting_ms.ToString("F3", inv)},\"plotting_peak_ms\":{plotting_peak.ToString("F2", inv)}," +
            $"\"mesh_gen_ms\":{mesh_gen_ms.ToString("F3", inv)},\"mesh_gen_peak_ms\":{mesh_gen_peak.ToString("F2", inv)}," +
            $"\"flight_plan_eval_ms\":{fp_eval_ms.ToString("F3", inv)},\"flight_plan_eval_peak_ms\":{fp_eval_peak.ToString("F2", inv)}," +
            $"\"gpu_compute_ms\":{gpu_compute_ms.ToString("F3", inv)},\"gpu_compute_peak_ms\":{gpu_compute_peak.ToString("F2", inv)}," +
            $"\"celestial_plot_ms\":{cel_plot_ms.ToString("F3", inv)},\"celestial_plot_peak_ms\":{cel_plot_peak.ToString("F2", inv)}," +
            $"\"vessel_plot_ms\":{ves_plot_ms.ToString("F3", inv)},\"vessel_plot_peak_ms\":{ves_plot_peak.ToString("F2", inv)}," +
            $"\"equipotential_plot_ms\":{eq_plot_ms.ToString("F3", inv)},\"equipotential_plot_peak_ms\":{eq_plot_peak.ToString("F2", inv)}" +
          "}," +
          "\"cache\":{" +
            $"\"hit_ratio\":{cache_hit_ratio.ToString("F1", inv)}," +
            $"\"hits\":{cache_hits}," +
            $"\"misses\":{cache_misses}," +
            $"\"plot_invalidations\":{plot_invalidations}," +
            $"\"vertices_rendered\":{vertices_rendered}," +
            $"\"celestial_l1_mesh\":{cel_l1_mesh}," +
            $"\"celestial_l2_files\":{cel_l2_files}," +
            $"\"celestial_hit_ratio\":{cel_hit_ratio.ToString("F1", inv)}," +
            $"\"celestial_l1_hits\":{cel_l1_hits}," +
            $"\"celestial_l2_hits\":{cel_l2_hits}," +
            $"\"celestial_misses\":{cel_misses}" +
          "}," +
          "\"ephemeris\":{" +
            $"\"active\":{(ephem_active ? "true" : "false")}," +
            $"\"paused\":{(ephem_paused ? "true" : "false")}," +
            $"\"prolonging\":{(ephem_prolonging ? "true" : "false")}," +
            $"\"horizon_seconds\":{ephem_horizon_s.ToString("F0", inv)}," +
            $"\"horizon_years\":{ephem_horizon_y.ToString("F2", inv)}," +
            $"\"lookahead_years\":{ephem_lookahead_y.ToString("F1", inv)}," +
            $"\"refresh_threshold_years\":{ephem_threshold_y.ToString("F1", inv)}," +
            $"\"progress_ratio\":{ephem_progress.ToString("F3", inv)}," +
            $"\"carrier\":\"{EscapeJson(ephem_carrier)}\"" +
          "}," +
          "\"decoupler_governor\":{" +
            $"\"autonomous_decouple\":{(decouple_active ? "true" : "false")}," +
            $"\"snapshots_consumed\":{snapshots_consumed}," +
            $"\"tracked_entities\":{tracked_entities}," +
            $"\"staged_harvest\":{staged_harvest}," +
            $"\"gpu_name\":\"{EscapeJson(gpu_name)}\"," +
            $"\"supports_compute\":{(supports_compute ? "true" : "false")}," +
            $"\"zero_copy_enabled\":{(zero_copy_enabled ? "true" : "false")}," +
            $"\"zero_copy_direct_count\":{zero_copy_direct}," +
            $"\"zero_copy_peak_vertices\":{zero_copy_peak_vert}," +
            $"\"zero_copy_capacity\":{zero_copy_cap}," +
            $"\"governor_affinity\":\"{affinity_str}\"," +
            $"\"backpressure_active\":{(backpressure ? "true" : "false")}," +
            $"\"gpu_dispatch_count\":{gpu_dispatches}," +
            $"\"cpu_dispatch_count\":{cpu_dispatches}," +
            $"\"vacuum_bypass_enabled\":{(vacuum_bypass ? "true" : "false")}," +
            $"\"bypassed_precalc_count\":{bypassed_precalc}," +
            $"\"bypassed_aero_count\":{bypassed_aero}," +
            $"\"executed_precalc_count\":{executed_precalc}," +
            $"\"circuit_breaker_enabled\":{(circuit_breaker ? "true" : "false")}," +
            $"\"circuit_breaker_trips\":{cb_trips}," +
            $"\"rigid_motion_bypassed_parts\":{rigid_bypassed}," +
            $"\"rigid_motion_executed_parts\":{rigid_executed}" +
          "}" +
          "}";

      // Pretty formatted version for PrincipiaTelemetry_latest.json
      string pretty_latest_json = FormatJsonPretty(jsonl_line);

      // Write to disks
      lock (file_lock_) {
        foreach (string dir in output_directories_) {
          try {
            string csv_path = Path.Combine(dir, "PrincipiaTelemetry.csv");
            string jsonl_path = Path.Combine(dir, "PrincipiaTelemetry.jsonl");
            string latest_path = Path.Combine(dir, "PrincipiaTelemetry_latest.json");

            File.AppendAllText(csv_path, csv_line + Environment.NewLine, Encoding.UTF8);
            File.AppendAllText(jsonl_path, jsonl_line + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(latest_path, pretty_latest_json, Encoding.UTF8);
          } catch {
            // Ignore temporary file locking errors
          }
        }
      }
    } catch (Exception ex) {
      Log.Warning($"[TelemetryLogger] Exception recording snapshot: {ex.Message}");
    }
  }

  private static string EscapeJson(string str) {
    if (string.IsNullOrEmpty(str)) return "";
    return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
  }

  private static string FormatJsonPretty(string compact_json) {
    var sb = new StringBuilder(compact_json.Length * 2);
    int indent = 0;
    bool in_quotes = false;

    for (int i = 0; i < compact_json.Length; i++) {
      char c = compact_json[i];
      if (c == '\"' && (i == 0 || compact_json[i - 1] != '\\')) {
        in_quotes = !in_quotes;
        sb.Append(c);
      } else if (!in_quotes) {
        if (c == '{' || c == '[') {
          sb.Append(c);
          sb.Append(Environment.NewLine);
          indent += 2;
          sb.Append(' ', indent);
        } else if (c == '}' || c == ']') {
          sb.Append(Environment.NewLine);
          indent = Math.Max(0, indent - 2);
          sb.Append(' ', indent);
          sb.Append(c);
        } else if (c == ',') {
          sb.Append(c);
          sb.Append(Environment.NewLine);
          sb.Append(' ', indent);
        } else if (c == ':') {
          sb.Append(": ");
        } else {
          sb.Append(c);
        }
      } else {
        sb.Append(c);
      }
    }
    return sb.ToString();
  }
}

}  // namespace principia.ksp_plugin_adapter
