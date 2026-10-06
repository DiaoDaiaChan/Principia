using System;
using System.Collections.Generic;

namespace principia {
namespace ksp_plugin_adapter {

internal class RollingEphemerisDriver {
  public const double JulianYear = 365.25 * 86400.0;

  // Horizon configuration:
  // Maintain a rolling lookahead horizon of 100 days.
  // When remaining lookahead is less than 30 days, extend forward.
  // Clamped to prevent exceeding C++ FlightPlan adaptive step limit (4096 steps).
  public double lookahead_horizon = 100.0 * 86400.0;
  public double refresh_threshold = 30.0 * 86400.0;

  public const string EphemerisAnchorGuid = "00000000-0000-0000-0000-000000000001";
  public const uint EphemerisAnchorPartId = 0xEE000001;
  public const string EphemerisAnchorName = "Principia_Ephemeris_Anchor";

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
  public string carrier_vessel_guid => carrier_vessel_guid_;
  public bool is_active => !string.IsNullOrEmpty(carrier_vessel_guid_);
  public bool is_paused { get; set; } = false;
  public bool is_prolonging { get; private set; } = false;

  public static bool HasAnchor(IntPtr plugin) =>
      plugin != IntPtr.Zero && plugin.HasVessel(EphemerisAnchorGuid);

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

  public bool EnsureEphemerisAnchor() {
    if (plugin_ == IntPtr.Zero) {
      return false;
    }
    if (plugin_.HasVessel(EphemerisAnchorGuid)) {
      return true;
    }
    try {
      var home = FlightGlobals.GetHomeBody() ??
                 (FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 ? FlightGlobals.Bodies[0] : null);
      if (home == null) {
        return false;
      }
      double r = home.Radius + 500000.0;
      double v = Math.Sqrt(Math.Max(1.0, home.gravParameter) / Math.Max(1.0, r));
      plugin_.InsertOrKeepVessel(
          EphemerisAnchorGuid,
          EphemerisAnchorName,
          home.flightGlobalsIndex,
          loaded: false,
          out bool _);
      plugin_.InsertUnloadedPart(
          EphemerisAnchorPartId,
          "EphemerisAnchorPart",
          EphemerisAnchorGuid,
          new QP {
              q = (XYZ)new Vector3d(r, 0, 0),
              p = (XYZ)new Vector3d(0, v, 0)
          });
      return plugin_.HasVessel(EphemerisAnchorGuid);
    } catch (Exception e) {
      Log.Warning($"[RollingEphemeris] Failed to initialize EphemerisAnchor: {e.Message}");
      return false;
    }
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
    if (vessel_guid == EphemerisAnchorGuid) {
      return true;
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
      } else if (EnsureEphemerisAnchor()) {
        carrier_vessel_guid_ = EphemerisAnchorGuid;
        carrier_vessel_name = "星历预演锚点 (Ephemeris Anchor)";
        AddLog("选定星历预演锚点 (无实体飞船时每时每刻常驻推演)");
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
        if (carrier_vessel_guid_ != EphemerisAnchorGuid) {
          var vessel = FlightGlobals.FindVessel(new Guid(carrier_vessel_guid_));
          if (vessel != null) {
            mass = Math.Max(0.1, vessel.GetTotalMass());
          }
        }
        plugin_.FlightPlanCreate(carrier_vessel_guid_, new_target, mass);
        Log.Info($"[RollingEphemeris] Created background rolling ephemeris carrier on '{carrier_vessel_name}' (target: +{lookahead_horizon / JulianYear:F1} yr)");
        AddLog($"创建新背景飞行计划 (目标: +{lookahead_horizon / (86400.0 * 365.25):F2} 年)");
      } else {
        var status = plugin_.FlightPlanSetDesiredFinalTime(carrier_vessel_guid_, new_target);
        if (!status.ok()) {
          double achieved = plugin_.FlightPlanGetDesiredFinalTime(carrier_vessel_guid_);
          if (achieved > current_ut) {
            target_extended_time_ = achieved;
          }
          last_update_realtime_ = UnityEngine.Time.realtimeSinceStartup + 60.0;
          Log.Warning($"[RollingEphemeris] FlightPlanSetDesiredFinalTime status: {status.error} (achieved UT {achieved:F0})");
          AddLog($"推演终点达上限: {status.error} (已延展至上限)");
          return;
        } else {
          AddLog($"星历成功延展至 UT {new_target:F0}s (+{lookahead_horizon / (86400.0 * 365.25):F2} 年)");
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

  // Structured scoring weights for candidate carrier vessels
  private static readonly System.Collections.Generic.Dictionary<VesselType, int> VesselTypeWeights =
      new System.Collections.Generic.Dictionary<VesselType, int> {
        { VesselType.Debris, 500 },
        { VesselType.SpaceObject, 450 },
        { VesselType.Probe, 400 },
        { VesselType.Relay, 400 },
        { VesselType.Station, 300 },
        { VesselType.Base, 300 },
        { VesselType.Ship, 200 },
      };

  private static readonly System.Collections.Generic.Dictionary<Vessel.Situations, int> SituationWeights =
      new System.Collections.Generic.Dictionary<Vessel.Situations, int> {
        { Vessel.Situations.ORBITING, 200 },
        { Vessel.Situations.ESCAPING, 150 },
        { Vessel.Situations.SUB_ORBITAL, 50 },
        { Vessel.Situations.LANDED, 30 },
        { Vessel.Situations.SPLASHED, 30 },
        { Vessel.Situations.FLYING, -100 },
        { Vessel.Situations.PRELAUNCH, -200 },
      };

  private const int DefaultVesselTypeWeight = 100;

  private int ScoreVessel(Vessel v) {
    int score = 0;
    if (VesselTypeWeights.TryGetValue(v.vesselType, out int type_weight)) {
      score += type_weight;
    } else {
      score += DefaultVesselTypeWeight;
    }

    if (SituationWeights.TryGetValue(v.situation, out int situation_weight)) {
      score += situation_weight;
    }

    return score;
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
