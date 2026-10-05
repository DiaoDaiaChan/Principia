using System;
using System.Collections.Generic;

namespace principia {
namespace ksp_plugin_adapter {

internal class RollingEphemerisDriver {
  public const double JulianYear = 365.25 * 86400.0;

  // Horizon configuration:
  // Maintain a rolling lookahead horizon of 3.0 Julian Years.
  // When remaining lookahead is less than 1.0 Julian Year, extend forward.
  public double lookahead_horizon = 3.0 * JulianYear;
  public double refresh_threshold = 1.0 * JulianYear;

  private readonly IntPtr plugin_;
  private readonly PrincipiaPluginAdapter adapter_;

  private static string carrier_vessel_guid_ = null;
  private static bool suppress_plot_on_active_ = false;

  private double target_extended_time_ = -1.0;
  private double last_update_realtime_ = 0.0;

  // Live status telemetry
  public double current_horizon_seconds =>
      Math.Max(0.0, target_extended_time_ - Planetarium.GetUniversalTime());
  public double current_horizon_years => current_horizon_seconds / JulianYear;
  public double lookahead_horizon_years => lookahead_horizon / JulianYear;
  public double refresh_threshold_years => refresh_threshold / JulianYear;
  public double target_extended_time => target_extended_time_;
  public double progress_ratio =>
      lookahead_horizon > 0.0 ? Math.Min(1.0, Math.Max(0.0, current_horizon_seconds / lookahead_horizon)) : 0.0;
  public string carrier_vessel_name { get; private set; } = "None";
  public bool is_active => !string.IsNullOrEmpty(carrier_vessel_guid_);
  public bool is_paused { get; set; } = false;
  public bool is_prolonging { get; private set; } = false;

  // Console log buffer
  public readonly List<string> ConsoleLogs = new List<string>();
  public const int MaxConsoleLogs = 30;

  public static RollingEphemerisDriver Instance { get; private set; }

  public RollingEphemerisDriver(IntPtr plugin, PrincipiaPluginAdapter adapter) {
    plugin_ = plugin;
    adapter_ = adapter;
    Instance = this;
    AddLog("星历自动推演驱动器已初始化");
  }

  public void AddLog(string msg) {
    string time_str = DateTime.Now.ToString("HH:mm:ss");
    lock (ConsoleLogs) {
      if (ConsoleLogs.Count >= MaxConsoleLogs) {
        ConsoleLogs.RemoveAt(0);
      }
      ConsoleLogs.Add($"[{time_str}] {msg}");
    }
    Log.Info($"[RollingEphemeris] {msg}");
  }

  public void TogglePause() {
    is_paused = !is_paused;
    AddLog(is_paused ? "推演已暂停 (挂起后台推进)" : "推演已恢复 (继续自动延展)");
  }

  public void ForceProlong() {
    AddLog("手动触发立即延展...");
    EnsureHorizon(Planetarium.GetUniversalTime(), force: true);
  }

  public void AdjustHorizon(double delta_years) {
    lookahead_horizon = Math.Max(0.5 * JulianYear, Math.Min(20.0 * JulianYear, lookahead_horizon + delta_years * JulianYear));
    refresh_threshold = Math.Max(0.2 * JulianYear, lookahead_horizon * 0.333);
    AddLog($"前瞻目标视界已调整为 +{lookahead_horizon_years:F1} 年 (刷新阈值: <{refresh_threshold_years:F1} 年)");
    EnsureHorizon(Planetarium.GetUniversalTime(), force: true);
  }

  public void ReselectCarrier() {
    if (!string.IsNullOrEmpty(carrier_vessel_guid_)) {
      ReleaseCarrier(carrier_vessel_guid_);
    }
    AddLog("重新扫描并优选最佳载体...");
    EnsureHorizon(Planetarium.GetUniversalTime(), force: true);
  }

  public static bool ShouldSuppressPlot(string vessel_guid) {
    if (string.IsNullOrEmpty(vessel_guid)) {
      return false;
    }
    return suppress_plot_on_active_ && vessel_guid == carrier_vessel_guid_;
  }

  public static bool IsCarrier(string vessel_guid) {
    if (string.IsNullOrEmpty(vessel_guid)) {
      return false;
    }
    return vessel_guid == carrier_vessel_guid_;
  }

  public static void ReleaseCarrier(string vessel_guid) {
    if (vessel_guid == carrier_vessel_guid_) {
      Log.Info($"[RollingEphemeris] Releasing carrier {carrier_vessel_guid_} for user flight planning");
      carrier_vessel_guid_ = null;
      suppress_plot_on_active_ = false;
      Instance?.AddLog("已释放当前载体飞船");
    }
  }

  public void Reset() {
    carrier_vessel_guid_ = null;
    suppress_plot_on_active_ = false;
    target_extended_time_ = -1.0;
    carrier_vessel_name = "None";
    is_prolonging = false;
    if (Instance == this) {
      Instance = null;
    }
  }

  public void OnTimeWarpRateChanged() {
    if (plugin_ == IntPtr.Zero || !adapter_.PluginRunning() || is_paused) {
      return;
    }
    double current_ut = Planetarium.GetUniversalTime();
    double remaining = target_extended_time_ - current_ut;
    if (remaining < refresh_threshold || target_extended_time_ <= 0) {
      EnsureHorizon(current_ut);
    }
  }

  public void Update() {
    if (plugin_ == IntPtr.Zero || !adapter_.PluginRunning() || is_paused) {
      return;
    }

    double current_realtime = UnityEngine.Time.realtimeSinceStartup;
    bool is_warping = TimeWarp.CurrentRate > 1.0;
    double check_interval = is_warping ? 0.5 : 2.0;

    if (current_realtime - last_update_realtime_ < check_interval) {
      return;
    }
    last_update_realtime_ = current_realtime;

    double current_ut = Planetarium.GetUniversalTime();
    double remaining = target_extended_time_ - current_ut;

    if (target_extended_time_ > 0 && remaining >= refresh_threshold) {
      return;
    }

    EnsureHorizon(current_ut);
  }

  private void EnsureHorizon(double current_ut, bool force = false) {
    if (is_paused && !force) {
      return;
    }

    // 1. Verify existing carrier validity.
    if (!string.IsNullOrEmpty(carrier_vessel_guid_)) {
      if (!plugin_.HasVessel(carrier_vessel_guid_)) {
        ReleaseCarrier(carrier_vessel_guid_);
      }
    }

    // 2. Select best carrier vessel if none assigned.
    if (string.IsNullOrEmpty(carrier_vessel_guid_)) {
      var carrier = FindBestCarrier();
      if (carrier != null) {
        carrier_vessel_guid_ = carrier.id.ToString();
        carrier_vessel_name = carrier.vesselName;
        AddLog($"选定推演载体: '{carrier_vessel_name}' ({carrier.vesselType})");
      } else {
        AddLog("未找到可用载体飞船 (待机中)");
      }
    }

    if (string.IsNullOrEmpty(carrier_vessel_guid_)) {
      return;
    }

    // 3. Request background prolongation via carrier flight plan.
    is_prolonging = true;
    double new_target = current_ut + lookahead_horizon;
    try {
      if (!plugin_.FlightPlanExists(carrier_vessel_guid_)) {
        double mass = 1.0;
        var vessel = FlightGlobals.Vessels?.Find(v => v?.id.ToString() == carrier_vessel_guid_);
        if (vessel != null) {
          mass = Math.Max(0.1, vessel.GetTotalMass());
        }
        plugin_.FlightPlanCreate(carrier_vessel_guid_, new_target, mass);
        Log.Info($"[RollingEphemeris] Created background rolling ephemeris carrier on '{carrier_vessel_name}' (target: +{lookahead_horizon / JulianYear:F1} yr)");
        AddLog($"创建新背景飞行计划 (目标: +{lookahead_horizon / JulianYear:F1} 年)");
      } else {
        var status = plugin_.FlightPlanSetDesiredFinalTime(carrier_vessel_guid_, new_target);
        if (!status.ok()) {
          Log.Warning($"[RollingEphemeris] FlightPlanSetDesiredFinalTime status: {status.error}");
          AddLog($"更新推演终点异常: {status.error}");
        } else {
          AddLog($"星历成功延展至 UT {new_target:F0}s (+{lookahead_horizon / JulianYear:F1} 年)");
        }
      }
      target_extended_time_ = new_target;
    } catch (Exception e) {
      Log.Warning($"[RollingEphemeris] Failed to prolong ephemeris horizon: {e}");
      AddLog($"延展异常: {e.Message}");
    } finally {
      is_prolonging = false;
    }
  }

  private Vessel FindBestCarrier() {
    var vessels = FlightGlobals.Vessels;
    if (vessels == null || vessels.Count == 0) {
      return null;
    }

    Vessel best_inactive_vessel = null;
    int best_score = -1;

    for (int i = 0; i < vessels.Count; ++i) {
      var v = vessels[i];
      if (v == null) {
        continue;
      }
      string guid = v.id.ToString();
      if (!plugin_.HasVessel(guid)) {
        continue;
      }

      bool is_active = (v == FlightGlobals.ActiveVessel);
      if (is_active) {
        continue; // Always prefer inactive vessels first!
      }

      int score = ScoreVessel(v);
      if (score > best_score) {
        best_score = score;
        best_inactive_vessel = v;
      }
    }

    if (best_inactive_vessel != null) {
      suppress_plot_on_active_ = false;
      return best_inactive_vessel;
    }

    // Fallback: If only ActiveVessel exists in the game, check if we can use it safely.
    var active = FlightGlobals.ActiveVessel;
    if (active != null) {
      string active_guid = active.id.ToString();
      if (plugin_.HasVessel(active_guid)) {
        if (!plugin_.FlightPlanExists(active_guid) ||
            plugin_.FlightPlanNumberOfManoeuvres(active_guid) == 0) {
          suppress_plot_on_active_ = true;
          return active;
        }
      }
    }

    return null;
  }

  private int ScoreVessel(Vessel v) {
    int score = 0;
    switch (v.vesselType) {
      case VesselType.Debris:
        score += 500;
        break;
      case VesselType.SpaceObject:
        score += 450;
        break;
      case VesselType.Probe:
      case VesselType.Relay:
        score += 400;
        break;
      case VesselType.Station:
      case VesselType.Base:
        score += 300;
        break;
      case VesselType.Ship:
        score += 200;
        break;
      default:
        score += 100;
        break;
    }

    switch (v.situation) {
      case Vessel.Situations.ORBITING:
        score += 200;
        break;
      case Vessel.Situations.ESCAPING:
        score += 150;
        break;
      case Vessel.Situations.SUB_ORBITAL:
        score += 50;
        break;
      case Vessel.Situations.LANDED:
      case Vessel.Situations.SPLASHED:
        score += 30;
        break;
      case Vessel.Situations.FLYING:
        score -= 100;
        break;
      case Vessel.Situations.PRELAUNCH:
        score -= 200;
        break;
    }

    return score;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
