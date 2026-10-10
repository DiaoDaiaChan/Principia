using System;
using System.Collections.Generic;

namespace principia {
namespace ksp_plugin_adapter {

/// <summary>
/// Façade for the C++ native EphemerisPacer: an autonomous daemon thread inside
/// principia.dll that prolongs the solar-system ephemeris in the background
/// (see ksp_plugin/ephemeris_pacer.{hpp,cpp} and interface_ephemeris.cpp).
/// All telemetry and parameters are retrieved directly from the native C++ layer.
/// </summary>
internal class RollingEphemerisDriver {
  public const double JulianYear = 365.25 * 86400.0;

  // Horizon configuration, mirrored into the native pacer via EphemerisAdjustHorizon.
  public double lookahead_horizon = 100.0 * 86400.0;
  public double refresh_threshold = 30.0 * 86400.0;

  private readonly IntPtr plugin_;

  public bool NativeAvailable =>
      Interface.HasNativeEphemerisPacer() && plugin_ != IntPtr.Zero;

  // Live status telemetry directly from C++
  public double current_horizon_seconds =>
      NativeAvailable ? plugin_.EphemerisGetHorizonSeconds() : 0.0;
  public double current_horizon_years => current_horizon_seconds / JulianYear;
  public double lookahead_horizon_years => lookahead_horizon / JulianYear;
  public double refresh_threshold_years => refresh_threshold / JulianYear;
  public double target_extended_time =>
      NativeAvailable ? plugin_.EphemerisGetTargetExtendedTime() : 0.0;
  public double progress_ratio =>
      lookahead_horizon > 0.0 ? Math.Min(1.0, Math.Max(0.0, current_horizon_seconds / lookahead_horizon)) : 0.0;
  public string carrier_vessel_name =>
      NativeAvailable
          ? "C++ Native Ephemeris Engine (独立守护线程)"
          : "不可用 (当前 principia.dll 缺少原生星历推演)";
  public string carrier_vessel_guid =>
      NativeAvailable ? "NativeEphemerisPacer" : null;
  public bool is_active => NativeAvailable;
  public bool is_paused {
    get => NativeAvailable && plugin_.EphemerisIsPaused();
    set {
      if (NativeAvailable && plugin_.EphemerisIsPaused() != value) {
        plugin_.EphemerisTogglePause();
      }
    }
  }
  public bool is_prolonging =>
      NativeAvailable && plugin_.EphemerisIsProlonging();

  // Console log buffer
  public readonly List<string> ConsoleLogs = new List<string>();
  public const int MaxConsoleLogs = 30;

  public static RollingEphemerisDriver Instance { get; private set; }

  public RollingEphemerisDriver(IntPtr plugin, PrincipiaPluginAdapter adapter) {
    plugin_ = plugin;
    Instance = this;
    if (Interface.HasNativeEphemerisPacer()) {
      AddLog("星历自动推演驱动器已挂接 (模式: C++ 原生独立守护线程)");
    } else {
      AddLog("<color=red>警告: 当前 principia.dll 缺少原生星历推演接口</color>");
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
    if (!NativeAvailable) return;
    plugin_.EphemerisTogglePause();
    bool paused = plugin_.EphemerisIsPaused();
    AddLog(paused ? "推演已暂停 (挂起 C++ 原生守护线程)" : "推演已恢复 (继续 C++ 原生推演)");
  }

  public void ForceProlong() {
    if (!NativeAvailable) return;
    AddLog("手动触发立即延展...");
    plugin_.EphemerisForceProlong();
  }

  public void AdjustHorizon(double delta_years) {
    lookahead_horizon = Math.Max(0.5 * JulianYear, Math.Min(20.0 * JulianYear, lookahead_horizon + delta_years * JulianYear));
    refresh_threshold = Math.Max(0.2 * JulianYear, lookahead_horizon * 0.333);
    AddLog($"前瞻目标视界已调整为 +{lookahead_horizon_years:F1} 年 (刷新阈值: <{refresh_threshold_years:F1} 年)");
    if (NativeAvailable) {
      plugin_.EphemerisAdjustHorizon(delta_years * JulianYear);
    }
  }

  public void ReselectCarrier() {
    AddLog("当前为 C++ 原生独立推演线程，无需绑定或切换载体飞船");
  }

  public void Reset() {
    if (Instance == this) {
      Instance = null;
    }
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
