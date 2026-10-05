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

  public double current_horizon_seconds =>
      Math.Max(0.0, target_extended_time_ - Planetarium.GetUniversalTime());
  public double current_horizon_years => current_horizon_seconds / JulianYear;
  public double lookahead_horizon_years => lookahead_horizon / JulianYear;
  public double refresh_threshold_years => refresh_threshold / JulianYear;
  public double target_extended_time => target_extended_time_;
  public string carrier_vessel_name { get; private set; } = "None";
  public bool is_active => !string.IsNullOrEmpty(carrier_vessel_guid_);

  public static RollingEphemerisDriver Instance { get; private set; }

  public RollingEphemerisDriver(IntPtr plugin, PrincipiaPluginAdapter adapter) {
    plugin_ = plugin;
    adapter_ = adapter;
    Instance = this;
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
    }
  }

  public void Reset() {
    carrier_vessel_guid_ = null;
    suppress_plot_on_active_ = false;
    target_extended_time_ = -1.0;
    carrier_vessel_name = "None";
    if (Instance == this) {
      Instance = null;
    }
  }

  public void OnTimeWarpRateChanged() {
    if (plugin_ == IntPtr.Zero || !adapter_.PluginRunning()) {
      return;
    }
    double current_ut = Planetarium.GetUniversalTime();
    double remaining = target_extended_time_ - current_ut;
    if (remaining < refresh_threshold || target_extended_time_ <= 0) {
      EnsureHorizon(current_ut);
    }
  }

  public void Update() {
    if (plugin_ == IntPtr.Zero || !adapter_.PluginRunning()) {
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

  private void EnsureHorizon(double current_ut) {
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
      }
    }

    if (string.IsNullOrEmpty(carrier_vessel_guid_)) {
      return;
    }

    // 3. Request background prolongation via carrier flight plan.
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
      } else {
        var status = plugin_.FlightPlanSetDesiredFinalTime(carrier_vessel_guid_, new_target);
        if (!status.ok()) {
          Log.Warning($"[RollingEphemeris] FlightPlanSetDesiredFinalTime status: {status.error}");
        }
      }
      target_extended_time_ = new_target;
    } catch (Exception e) {
      Log.Warning($"[RollingEphemeris] Failed to prolong ephemeris horizon: {e}");
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
