using System;
using System.Collections.Generic;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public static class VacuumBypassGovernor {
  // Master toggle for Smart Vacuum Bypass
  public static bool EnableVacuumBypass = true;

  // Dynamic calibration interval in physics frames (~1 second based on actual physics timestep)
  public static long CalibrationIntervalFrames =>
      Math.Max(10, (long)(1.0f / Mathf.Max(0.001f, UnityEngine.Time.fixedDeltaTime)));

  // Telemetry metrics
  public static long BypassedPrecalcCount { get; private set; }
  public static long ExecutedPrecalcCount { get; private set; }
  public static long BypassedAeroCount { get; private set; }
  public static long ExecutedAeroCount { get; private set; }
  public static long BypassedForcesCount { get; private set; }
  public static long ExecutedForcesCount { get; private set; }

  // Set of vessel GUIDs that require full physics re-calibration
  private static readonly HashSet<Guid> dirty_vessels_ = new HashSet<Guid>();

  // Physics frame tracking per vessel
  private static readonly Dictionary<Guid, long> vessel_last_executed_frame_ =
      new Dictionary<Guid, long>();

  // Vessel mass and part count caching to detect staging/fuel drain/docking
  private static readonly Dictionary<Guid, double> vessel_last_mass_ =
      new Dictionary<Guid, double>();
  private static readonly Dictionary<Guid, int> vessel_last_part_count_ =
      new Dictionary<Guid, int>();

  private sealed class EventListener {
    public void OnPartDie(Part part) {
      if (part != null && part.vessel != null) {
        MarkDirty(part.vessel);
      } else if (FlightGlobals.ActiveVessel != null) {
        MarkDirty(FlightGlobals.ActiveVessel);
      }
    }

    public void OnPartCouple(GameEvents.FromToAction<Part, Part> action) {
      if (action.from != null && action.from.vessel != null) {
        MarkDirty(action.from.vessel);
      }
      if (action.to != null && action.to.vessel != null) {
        MarkDirty(action.to.vessel);
      }
    }

    public void OnPartUndock(Part part) {
      if (part != null && part.vessel != null) {
        MarkDirty(part.vessel);
      } else if (FlightGlobals.ActiveVessel != null) {
        MarkDirty(FlightGlobals.ActiveVessel);
      }
    }

    public void OnVesselWasModified(Vessel vessel) {
      if (vessel != null) {
        MarkDirty(vessel);
      }
    }

    public void OnStageActivate(int stage) {
      if (FlightGlobals.ActiveVessel != null) {
        MarkDirty(FlightGlobals.ActiveVessel);
      }
    }
  }

  private static readonly EventListener listener_ = new EventListener();
  private static long current_physics_frame_ = 0;
  private static float last_fixed_time_ = float.NegativeInfinity;
  private static bool is_initialized_ = false;

  public static void Initialize() {
    if (is_initialized_) {
      return;
    }
    GameEvents.onPartDie.Add(listener_.OnPartDie);
    GameEvents.onPartCouple.Add(listener_.OnPartCouple);
    GameEvents.onPartUndock.Add(listener_.OnPartUndock);
    GameEvents.onVesselWasModified.Add(listener_.OnVesselWasModified);
    GameEvents.onStageActivate.Add(listener_.OnStageActivate);
    is_initialized_ = true;
    Log.Info("[VacuumBypass] VacuumBypassGovernor initialized.");
  }

  public static void Dispose() {
    if (!is_initialized_) {
      return;
    }
    GameEvents.onPartDie.Remove(listener_.OnPartDie);
    GameEvents.onPartCouple.Remove(listener_.OnPartCouple);
    GameEvents.onPartUndock.Remove(listener_.OnPartUndock);
    GameEvents.onVesselWasModified.Remove(listener_.OnVesselWasModified);
    GameEvents.onStageActivate.Remove(listener_.OnStageActivate);

    dirty_vessels_.Clear();
    vessel_last_executed_frame_.Clear();
    vessel_last_mass_.Clear();
    vessel_last_part_count_.Clear();
    current_physics_frame_ = 0;
    last_fixed_time_ = float.NegativeInfinity;
    is_initialized_ = false;
    Log.Info("[VacuumBypass] VacuumBypassGovernor disposed.");
  }

  public static void ResetTelemetry() {
    BypassedPrecalcCount = 0;
    ExecutedPrecalcCount = 0;
    BypassedAeroCount = 0;
    ExecutedAeroCount = 0;
    BypassedForcesCount = 0;
    ExecutedForcesCount = 0;
  }

  public static void MarkDirty(Vessel vessel) {
    if (vessel != null && vessel.id != Guid.Empty) {
      dirty_vessels_.Add(vessel.id);
    }
  }

  public static void MarkDirty(Guid vessel_id) {
    if (vessel_id != Guid.Empty) {
      dirty_vessels_.Add(vessel_id);
    }
  }

  public static bool ShouldBypassPrecalc(Vessel vessel) {
    if (vessel == null || vessel.id == Guid.Empty) {
      ExecutedPrecalcCount++;
      return false;
    }

    // Advance global physics frame counter once per physics time step.
    if (Time.fixedTime != last_fixed_time_) {
      last_fixed_time_ = Time.fixedTime;
      current_physics_frame_++;
    }

    // 1. Master toggle check
    if (!EnableVacuumBypass) {
      RecordExecution(vessel.id);
      return false;
    }

    // 2. Dirty set check: consume dirty flag and force full physics execution
    if (dirty_vessels_.Remove(vessel.id)) {
      RecordExecution(vessel.id);
      return false;
    }

    // 3. Surface contact, landing, or EVA check
    if (vessel.LandedOrSplashed || vessel.isEVA) {
      RecordExecution(vessel.id);
      return false;
    }

    // 4. Orbit situation check
    if (vessel.situation != Vessel.Situations.ORBITING &&
        vessel.situation != Vessel.Situations.SUB_ORBITAL &&
        vessel.situation != Vessel.Situations.ESCAPING) {
      RecordExecution(vessel.id);
      return false;
    }

    // 5. Orbit altitude and atmosphere check
    if (vessel.mainBody != null &&
        vessel.mainBody.atmosphere &&
        vessel.altitude <= vessel.mainBody.atmosphereDepth) {
      RecordExecution(vessel.id);
      return false;
    }

    // 6. Throttle and power/acceleration check
    if (vessel.ctrlState.mainThrottle > 1e-4f ||
        vessel.acceleration.sqrMagnitude > 1e-4) {
      RecordExecution(vessel.id);
      return false;
    }

    // 7. Pilot / SAS control inputs (pitch, yaw, roll, RCS translation)
    if (Mathf.Abs(vessel.ctrlState.pitch) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.yaw) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.roll) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.X) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.Y) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.Z) > 1e-4f) {
      RecordExecution(vessel.id);
      return false;
    }

    // 8. Angular velocity check (steady-state requires virtually zero rotation)
    if (vessel.angularVelocity.sqrMagnitude > 1e-5f) {
      RecordExecution(vessel.id);
      return false;
    }

    // 9. Vessel mass and part count consistency check (e.g. fuel burn or part count change)
    int part_count = vessel.parts != null ? vessel.parts.Count : 0;
    if (vessel_last_part_count_.TryGetValue(vessel.id, out int last_parts)) {
      if (last_parts != part_count) {
        vessel_last_part_count_[vessel.id] = part_count;
        RecordExecution(vessel.id);
        return false;
      }
    } else {
      vessel_last_part_count_[vessel.id] = part_count;
    }

    double total_mass = vessel.totalMass;
    if (vessel_last_mass_.TryGetValue(vessel.id, out double last_mass)) {
      if (Math.Abs(total_mass - last_mass) > 1e-5) {
        vessel_last_mass_[vessel.id] = total_mass;
        RecordExecution(vessel.id);
        return false;
      }
    } else {
      vessel_last_mass_[vessel.id] = total_mass;
    }

    // 10. Periodic calibration guard (every 50 physics frames)
    if (!vessel_last_executed_frame_.TryGetValue(vessel.id, out long last_frame) ||
        (current_physics_frame_ - last_frame) >= CalibrationIntervalFrames) {
      RecordExecution(vessel.id);
      return false;
    }

    // All vacuum static conditions satisfied: bypass precalculation
    BypassedPrecalcCount++;
    return true;
  }

  public static bool ShouldBypassAero(Vessel vessel) {
    if (!EnableVacuumBypass || vessel == null) {
      ExecutedAeroCount++;
      return false;
    }
    // Deep space or atmosphere-free body
    if (vessel.mainBody == null || !vessel.mainBody.atmosphere) {
      BypassedAeroCount++;
      return true;
    }
    // High altitude outside atmospheric ceiling
    if (vessel.altitude > vessel.mainBody.atmosphereDepth) {
      BypassedAeroCount++;
      return true;
    }
    ExecutedAeroCount++;
    return false;
  }

  public static bool IsVesselInStableVacuum(Vessel vessel) {
    if (!EnableVacuumBypass || vessel == null || vessel.id == Guid.Empty) {
      return false;
    }
    // Dirty flag check (staging, destruction, undocking)
    if (dirty_vessels_.Contains(vessel.id)) {
      return false;
    }
    // Atmospheric envelope check
    if (vessel.mainBody != null &&
        vessel.mainBody.atmosphere &&
        vessel.altitude <= vessel.mainBody.atmosphereDepth) {
      return false;
    }
    // Landed or splashed on terrain/ocean
    if (vessel.LandedOrSplashed) {
      return false;
    }
    // Kerbal on EVA (might be climbing ladder or parachuting)
    if (vessel.isEVA) {
      return false;
    }
    // Active throttle or external non-gravitational acceleration
    if (vessel.ctrlState.mainThrottle > 1e-4f ||
        vessel.acceleration.sqrMagnitude > 1e-4) {
      return false;
    }
    // Angular rotation (steady-state requires virtually zero rotation)
    if (vessel.angularVelocity.sqrMagnitude > 1e-5f) {
      return false;
    }
    // Rotational & translational pilot / SAS steering inputs
    if (Mathf.Abs(vessel.ctrlState.pitch) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.yaw) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.roll) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.X) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.Y) > 1e-4f ||
        Mathf.Abs(vessel.ctrlState.Z) > 1e-4f) {
      return false;
    }
    return true;
  }

  public static bool ShouldForceCalibration(Guid vessel_id) {
    if (!vessel_last_executed_frame_.TryGetValue(vessel_id, out long last_frame) ||
        (current_physics_frame_ - last_frame) >= CalibrationIntervalFrames) {
      vessel_last_executed_frame_[vessel_id] = current_physics_frame_;
      return true;
    }
    return false;
  }

  public static void RecordBypassedForces() {
    BypassedForcesCount++;
  }

  public static void RecordExecutedForces() {
    ExecutedForcesCount++;
  }

  private static void RecordExecution(Guid vessel_id) {
    ExecutedPrecalcCount++;
    vessel_last_executed_frame_[vessel_id] = current_physics_frame_;
  }
}

}
