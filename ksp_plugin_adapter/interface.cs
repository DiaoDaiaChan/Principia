using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace principia {
namespace ksp_plugin_adapter {

internal partial class Status {
  public static Status OK = new Status{ error = 0 };

  public bool is_aborted() {
    return error == 10;
  }

  public bool is_deadline_exceeded() {
    return error == 4;
  }

  public bool is_failed_precondition() {
    return error == 9;
  }

  public bool is_invalid_argument() {
    return error == 3;
  }

  public bool is_out_of_range() {
    return error == 11;
  }

  public bool is_resource_exhausted() {
    return error == 8;
  }

  public bool is_unavailable() {
    return error == 14;
  }

  public bool ok() {
    return error == 0;
  }

  public void Update(Status s) {
    if (ok() && !s.ok()) {
      this.error = s.error;
      this.message = s.message;
    }
  }
}

public partial struct XYZ {
  public static explicit operator XYZ(UnityEngine.Vector3 v) {
    return new XYZ{ x = v.x, y = v.y, z = v.z };
  }

  public static explicit operator XYZ(Vector3d v) {
    return new XYZ { x = v.x, y = v.y, z = v.z };
  }

  public static explicit operator UnityEngine.Vector3(XYZ v) {
    return new UnityEngine.Vector3((float)v.x, (float)v.y, (float)v.z);
  }

  public static explicit operator Vector3d(XYZ v) {
    return new Vector3d{ x = v.x, y = v.y, z = v.z };
  }
}

internal partial struct WXYZ {
  public static explicit operator WXYZ(UnityEngine.Quaternion q) {
    return new WXYZ{ w = q.w, x = q.x, y = q.y, z = q.z };
  }

  public static explicit operator WXYZ(UnityEngine.QuaternionD q) {
    return new WXYZ{ w = q.w, x = q.x, y = q.y, z = q.z };
  }

  public static explicit operator UnityEngine.QuaternionD(WXYZ q) {
    return new UnityEngine.QuaternionD{ w = q.w, x = q.x, y = q.y, z = q.z };
  }
}

internal partial struct Interval {
  public static readonly Interval Empty =
      new Interval
          { min = double.PositiveInfinity, max = double.NegativeInfinity };
  public double measure => min > max ? 0 : max - min;
  public double midpoint => min > max ? double.NaN : min + measure / 2;

  public void Include(double value) {
    if (value < min) {
      min = value;
    }
    if (value > max) {
      max = value;
    }
  }

  public bool Contains(double value) {
    return value >= min && value <= max;
  }

  public Interval IntersectedWith(Interval other) {
    return new Interval
        { min = Math.Max(min, other.min), max = Math.Min(max, other.max) };
  }
}

public enum FrameType {
  BARYCENTRIC_ROTATING = 6001,
  BODY_CENTRED_NON_ROTATING = 6000,
  BODY_CENTRED_PARENT_DIRECTION = 6002,
  BODY_SURFACE = 6003,
  ROTATING_PULSATING = 6004,
}

// We imbue PlottingFrameParameters and NavigationFrameParameters with a common
// interface and make the latter behave like a subtype of the former.

internal interface IReferenceFrameParameters {
  FrameType Extension  { get; set; }
  int CentreIndex { get; set; }
  int[] PrimaryIndices { get; set; }
  int[] SecondaryIndices { get; set; }
}

internal partial class PlottingFrameParameters : IReferenceFrameParameters,
                                                 IConfigNode {
  public PlottingFrameParameters() {
    primary_index = new int[]{};
    secondary_index = new int[]{};
  }

  public static explicit operator NavigationFrameParameters(
      PlottingFrameParameters p) {
    if ((FrameType)p.extension == FrameType.ROTATING_PULSATING) {
      return null;
    } else {
      return new NavigationFrameParameters{
          extension = p.extension,
          centre_index = p.centre_index,
          PrimaryIndices = p.PrimaryIndices,
          SecondaryIndices = p.SecondaryIndices
      };
    }
  }

  public override bool Equals(object obj) {
    return obj is IReferenceFrameParameters other && this == other;
  }

  public override int GetHashCode() {
    int p = primary_index != null && primary_index.Length > 0 ? primary_index[0] : -1;
    int s = secondary_index != null && secondary_index.Length > 0 ? secondary_index[0] : -1;
    return (extension, centre_index, p, s).GetHashCode();
  }

  public void Load(ConfigNode node) {
    if (!Enum.TryParse(node.GetUniqueValue("extension"),
                       out FrameType extension)) {
      Log.Fatal("Bad FrameType " + node.GetUniqueValue("extension"));
    }
    Extension = extension;
    if (!int.TryParse(node.GetUniqueValue("centre_index"), out centre_index)) {
      Log.Fatal("Bad centre_index " + node.GetUniqueValue("centre_index"));
    }
    var primary_indices = new List<int>();
    foreach (string value in node.GetValues("primary_indices")) {
      if (!int.TryParse(value, out int i)) {
        Log.Fatal("Bad primary_indices " + value);
      }
      primary_indices.Add(i);
    }
    primary_index = primary_indices.ToArray();
    var secondary_indices = new List<int>();
    foreach (string value in node.GetValues("secondary_indices")) {
      if (!int.TryParse(value, out int i)) {
        Log.Fatal("Bad secondary_indices " + value);
      }
      secondary_indices.Add(i);
    }
    secondary_index = secondary_indices.ToArray();
  }

  public void Save(ConfigNode node) {
    node.AddValue("extension", Extension.ToString());
    node.AddValue("centre_index", centre_index);
    foreach (int i in primary_index) {
      node.AddValue("primary_indices", i);
    }
    foreach (int i in secondary_index) {
      node.AddValue("secondary_indices", i);
    }
  }

  public static bool operator ==(PlottingFrameParameters left,
                                 IReferenceFrameParameters right) {
    if ((object)left == null && (object)right == null) {
      return true;
    } else if ((object)left == null || (object)right == null) {
      return false;
    } else {
      return left.Extension == right.Extension &&
             left.centre_index == right.CentreIndex &&
             left.PrimaryIndices.SequenceEqual(right.PrimaryIndices) &&
             left.SecondaryIndices.SequenceEqual(right.SecondaryIndices);
    }
  }

  public static bool operator !=(PlottingFrameParameters left,
                                 IReferenceFrameParameters right) {
    return !(left == right);
  }

  public FrameType Extension {
    get => (FrameType)extension;
    set => extension = (int)value;
  }

  public int CentreIndex {
    get => centre_index;
    set => centre_index = value;
  }

  public int[] PrimaryIndices {
    get => primary_index;
    set => primary_index = value;
  }

  public int[] SecondaryIndices {
    get => secondary_index;
    set => secondary_index = value;
  }
}

internal partial class NavigationFrameParameters : IReferenceFrameParameters {
  public static implicit operator PlottingFrameParameters(
      NavigationFrameParameters p) {
    return new PlottingFrameParameters{
        extension = p.extension,
        centre_index = p.centre_index,
        PrimaryIndices = p.PrimaryIndices,
        SecondaryIndices = p.SecondaryIndices
    };
  }

  public override bool Equals(object obj) {
    return obj is IReferenceFrameParameters other && this == other;
  }

  public override int GetHashCode() =>
      (extension, centre_index, primary_index, secondary_index).GetHashCode();

  public static bool operator ==(NavigationFrameParameters left,
                                 IReferenceFrameParameters right) {
    if ((object)left == null && (object)right == null) {
      return true;
    } else if ((object)left == null || (object)right == null) {
      return false;
    } else {
      return left.Extension == right.Extension &&
             left.CentreIndex == right.CentreIndex &&
             left.PrimaryIndices.SequenceEqual(right.PrimaryIndices) &&
             left.SecondaryIndices.SequenceEqual(right.SecondaryIndices);
    }
  }

  public static bool operator !=(NavigationFrameParameters left,
                                 IReferenceFrameParameters right) {
    return !(left == right);
  }

  public FrameType Extension {
    get => (FrameType)extension;
    set => extension = (int)value;
  }

  public int CentreIndex {
    get => centre_index;
    set => centre_index = value;
  }

  public int[] PrimaryIndices {
    get => primary_index == -1 ? new int[] {} : new[] { primary_index };
    set => primary_index = value.DefaultIfEmpty(-1).Single();
  }

  public int[] SecondaryIndices {
    get => secondary_index == -1 ? new int[] {} : new[] { secondary_index };
    set => secondary_index = value.DefaultIfEmpty(-1).Single();
  }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PartForceEntry {
  public uint part_id;
  public double force_x;
  public double force_y;
  public double force_z;
  public double lever_arm_x;
  public double lever_arm_y;
  public double lever_arm_z;
}

internal static partial class Interface {
  internal const string dll_path = "principia";

  internal static KeplerianElements Elements(this Orbit orbit) {
    double mean_motion = 2 * Math.PI / orbit.period;
    return new KeplerianElements{
        eccentricity                           = orbit.eccentricity,
        semimajor_axis                         = double.NaN,
        mean_motion                            = mean_motion,
        inclination_in_degrees                 = orbit.inclination,
        longitude_of_ascending_node_in_degrees = orbit.LAN,
        argument_of_periapsis_in_degrees       = orbit.argumentOfPeriapsis,
        mean_anomaly                           =
            orbit.meanAnomalyAtEpoch - orbit.epoch * mean_motion
    };
  }

  private static Symbols symbols_ = null;

  private partial class Symbols {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool PluginSaveToFileDelegate(
        IntPtr plugin,
        [MarshalAs(UnmanagedType.LPStr)] string file_path,
        [MarshalAs(UnmanagedType.LPStr)] string compressor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool PluginLoadFromFileDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string file_path,
        [MarshalAs(UnmanagedType.LPStr)] string compressor,
        out IntPtr plugin_reader);

    public PluginSaveToFileDelegate PluginSaveToFile =
        Loader.LoadOptionalFunction<PluginSaveToFileDelegate>(
            "principia__PluginSaveToFile");

    public PluginLoadFromFileDelegate PluginLoadFromFile =
        Loader.LoadOptionalFunction<PluginLoadFromFileDelegate>(
            "principia__PluginLoadFromFile");

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ApplyPartForcesBatchDelegate(
        IntPtr plugin,
        [In] PartForceEntry[] entries,
        int count);

    public ApplyPartForcesBatchDelegate ApplyPartForcesBatch =
        Loader.LoadOptionalFunction<ApplyPartForcesBatchDelegate>(
            "principia__ApplyPartForcesBatch");

    // Ephemeris Pacer
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double EphemerisGetHorizonSecondsDelegate(IntPtr plugin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double EphemerisGetTargetExtendedTimeDelegate(IntPtr plugin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool EphemerisIsProlongingDelegate(IntPtr plugin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EphemerisForceProlongDelegate(IntPtr plugin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EphemerisAdjustHorizonDelegate(IntPtr plugin, double delta_seconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EphemerisTogglePauseDelegate(IntPtr plugin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool EphemerisIsPausedDelegate(IntPtr plugin);

    public EphemerisGetHorizonSecondsDelegate EphemerisGetHorizonSeconds =
        Loader.LoadOptionalFunction<EphemerisGetHorizonSecondsDelegate>("principia__EphemerisGetHorizonSeconds");
    public EphemerisGetTargetExtendedTimeDelegate EphemerisGetTargetExtendedTime =
        Loader.LoadOptionalFunction<EphemerisGetTargetExtendedTimeDelegate>("principia__EphemerisGetTargetExtendedTime");
    public EphemerisIsProlongingDelegate EphemerisIsProlonging =
        Loader.LoadOptionalFunction<EphemerisIsProlongingDelegate>("principia__EphemerisIsProlonging");
    public EphemerisForceProlongDelegate EphemerisForceProlong =
        Loader.LoadOptionalFunction<EphemerisForceProlongDelegate>("principia__EphemerisForceProlong");
    public EphemerisAdjustHorizonDelegate EphemerisAdjustHorizon =
        Loader.LoadOptionalFunction<EphemerisAdjustHorizonDelegate>("principia__EphemerisAdjustHorizon");
    public EphemerisTogglePauseDelegate EphemerisTogglePause =
        Loader.LoadOptionalFunction<EphemerisTogglePauseDelegate>("principia__EphemerisTogglePause");
    public EphemerisIsPausedDelegate EphemerisIsPaused =
        Loader.LoadOptionalFunction<EphemerisIsPausedDelegate>("principia__EphemerisIsPaused");

    // DXGI, Hardware Profiler & Dynamic Workload Balancer
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GpuGetAdapterCountDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool GpuGetAdapterInfoDelegate(int index, out GpuAdapterInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GpuSelectComputeAdapterDelegate(int adapter_index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GpuGetSelectedAdapterDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GpuSetDmaModeDelegate(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GpuGetDmaModeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double GpuGetLastDmaWriteSecondsDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ulong GpuGetTotalDmaBytesDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool GpuBenchmarkDmaTransferDelegate(
        int segment_count, out double duration_seconds, out double bandwidth_gbps);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool RunHardwareCalibrationDelegate(out HardwareProfile profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool GetHardwareProfileDelegate(out HardwareProfile profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void UpdateSceneFeedbackDelegate(
        int scene_type, double render_time_ms, double physics_time_ms);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetWorkloadBalanceStateDelegate(out WorkloadBalanceState state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetWorkloadBypassDelegate(
        [MarshalAs(UnmanagedType.I1)] bool master_bypass, int module_mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetManualWorkloadOverrideDelegate(
        [MarshalAs(UnmanagedType.I1)] bool enable_manual, double manual_gpu_ratio);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PartitionVesselBatchDelegate(
        int total_vessels, out int cpu_count, out int gpu_count);

    public GpuGetAdapterCountDelegate GpuGetAdapterCount =
        Loader.LoadOptionalFunction<GpuGetAdapterCountDelegate>("principia__GpuGetAdapterCount");
    public GpuGetAdapterInfoDelegate GpuGetAdapterInfo =
        Loader.LoadOptionalFunction<GpuGetAdapterInfoDelegate>("principia__GpuGetAdapterInfo");
    public GpuSelectComputeAdapterDelegate GpuSelectComputeAdapter =
        Loader.LoadOptionalFunction<GpuSelectComputeAdapterDelegate>("principia__GpuSelectComputeAdapter");
    public GpuGetSelectedAdapterDelegate GpuGetSelectedAdapter =
        Loader.LoadOptionalFunction<GpuGetSelectedAdapterDelegate>("principia__GpuGetSelectedAdapter");
    public GpuSetDmaModeDelegate GpuSetDmaMode =
        Loader.LoadOptionalFunction<GpuSetDmaModeDelegate>("principia__GpuSetDmaMode");
    public GpuGetDmaModeDelegate GpuGetDmaMode =
        Loader.LoadOptionalFunction<GpuGetDmaModeDelegate>("principia__GpuGetDmaMode");
    public GpuGetLastDmaWriteSecondsDelegate GpuGetLastDmaWriteSeconds =
        Loader.LoadOptionalFunction<GpuGetLastDmaWriteSecondsDelegate>("principia__GpuGetLastDmaWriteSeconds");
    public GpuGetTotalDmaBytesDelegate GpuGetTotalDmaBytes =
        Loader.LoadOptionalFunction<GpuGetTotalDmaBytesDelegate>("principia__GpuGetTotalDmaBytes");
    public GpuBenchmarkDmaTransferDelegate GpuBenchmarkDmaTransfer =
        Loader.LoadOptionalFunction<GpuBenchmarkDmaTransferDelegate>("principia__GpuBenchmarkDmaTransfer");

    public RunHardwareCalibrationDelegate RunHardwareCalibration =
        Loader.LoadOptionalFunction<RunHardwareCalibrationDelegate>("principia__RunHardwareCalibration");
    public GetHardwareProfileDelegate GetHardwareProfile =
        Loader.LoadOptionalFunction<GetHardwareProfileDelegate>("principia__GetHardwareProfile");
    public UpdateSceneFeedbackDelegate UpdateSceneFeedback =
        Loader.LoadOptionalFunction<UpdateSceneFeedbackDelegate>("principia__UpdateSceneFeedback");
    public GetWorkloadBalanceStateDelegate GetWorkloadBalanceState =
        Loader.LoadOptionalFunction<GetWorkloadBalanceStateDelegate>("principia__GetWorkloadBalanceState");
    public SetWorkloadBypassDelegate SetWorkloadBypass =
        Loader.LoadOptionalFunction<SetWorkloadBypassDelegate>("principia__SetWorkloadBypass");
    public SetManualWorkloadOverrideDelegate SetManualWorkloadOverride =
        Loader.LoadOptionalFunction<SetManualWorkloadOverrideDelegate>("principia__SetManualWorkloadOverride");
    public PartitionVesselBatchDelegate PartitionVesselBatch =
        Loader.LoadOptionalFunction<PartitionVesselBatchDelegate>("principia__PartitionVesselBatch");

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool GpuGetDeviceInfoDelegate(int index, out GpuDeviceInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GpuSetDeviceRoleDelegate(int adapter_index, int role);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GpuGetMultiDeviceSummaryDelegate(
        out int device_count, out int active_count, out double total_compute_score);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PartitionMultiDeviceBatchDelegate(
        int total_work, [Out] int[] out_device_counts, int max_devices);

    public GpuGetDeviceInfoDelegate GpuGetDeviceInfo =
        Loader.LoadOptionalFunction<GpuGetDeviceInfoDelegate>("principia__GpuGetDeviceInfo");
    public GpuSetDeviceRoleDelegate GpuSetDeviceRole =
        Loader.LoadOptionalFunction<GpuSetDeviceRoleDelegate>("principia__GpuSetDeviceRole");
    public GpuGetMultiDeviceSummaryDelegate GpuGetMultiDeviceSummary =
        Loader.LoadOptionalFunction<GpuGetMultiDeviceSummaryDelegate>("principia__GpuGetMultiDeviceSummary");
    public PartitionMultiDeviceBatchDelegate PartitionMultiDeviceBatch =
        Loader.LoadOptionalFunction<PartitionMultiDeviceBatchDelegate>("principia__PartitionMultiDeviceBatch");

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetCpuModuleThreadingStatusDelegate(
        IntPtr plugin, out CpuModuleThreadingStatus status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetFrameBudgetMillisecondsDelegate(double ms);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool AdvanceInactiveVesselsBatchGpuDelegate(
        IntPtr plugin, double time_step_seconds, int sub_steps);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool OptimizeFlightPlanShootingDelegate(
        in ShootingContextGpu context, out OptimalCandidateBurn out_optimal);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool SaveMasterEphemerisCacheDelegate(string file_path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool LoadMasterEphemerisCacheDelegate(string file_path);

    public GetCpuModuleThreadingStatusDelegate GetCpuModuleThreadingStatus =
        Loader.LoadOptionalFunction<GetCpuModuleThreadingStatusDelegate>("principia__GetCpuModuleThreadingStatus");
    public SetFrameBudgetMillisecondsDelegate SetFrameBudgetMilliseconds =
        Loader.LoadOptionalFunction<SetFrameBudgetMillisecondsDelegate>("principia__SetFrameBudgetMilliseconds");
    public AdvanceInactiveVesselsBatchGpuDelegate AdvanceInactiveVesselsBatchGpu =
        Loader.LoadOptionalFunction<AdvanceInactiveVesselsBatchGpuDelegate>("principia__AdvanceInactiveVesselsBatchGpu");
    public OptimizeFlightPlanShootingDelegate OptimizeFlightPlanShooting =
        Loader.LoadOptionalFunction<OptimizeFlightPlanShootingDelegate>("principia__OptimizeFlightPlanShooting");
    public SaveMasterEphemerisCacheDelegate SaveMasterEphemerisCache =
        Loader.LoadOptionalFunction<SaveMasterEphemerisCacheDelegate>("principia__SaveMasterEphemerisCache");
    public LoadMasterEphemerisCacheDelegate LoadMasterEphemerisCache =
        Loader.LoadOptionalFunction<LoadMasterEphemerisCacheDelegate>("principia__LoadMasterEphemerisCache");
  }

  internal static bool HasNativeEphemerisPacer() =>
      symbols_?.EphemerisGetHorizonSeconds != null;

  internal static double EphemerisGetHorizonSeconds(this IntPtr plugin) =>
      symbols_?.EphemerisGetHorizonSeconds != null ? symbols_.EphemerisGetHorizonSeconds(plugin) : 0.0;

  internal static double EphemerisGetTargetExtendedTime(this IntPtr plugin) =>
      symbols_?.EphemerisGetTargetExtendedTime != null ? symbols_.EphemerisGetTargetExtendedTime(plugin) : 0.0;

  internal static bool EphemerisIsProlonging(this IntPtr plugin) =>
      symbols_?.EphemerisIsProlonging != null && symbols_.EphemerisIsProlonging(plugin);

  internal static void EphemerisForceProlong(this IntPtr plugin) =>
      symbols_?.EphemerisForceProlong?.Invoke(plugin);

  internal static void EphemerisAdjustHorizon(this IntPtr plugin, double delta_seconds) =>
      symbols_?.EphemerisAdjustHorizon?.Invoke(plugin, delta_seconds);

  internal static void EphemerisTogglePause(this IntPtr plugin) =>
      symbols_?.EphemerisTogglePause?.Invoke(plugin);

  internal static bool EphemerisIsPaused(this IntPtr plugin) =>
      symbols_?.EphemerisIsPaused != null && symbols_.EphemerisIsPaused(plugin);

  internal static bool HasGpuInteropSupport =>
      symbols_?.GpuGetAdapterCount != null;

  internal static int GpuGetAdapterCount() =>
      symbols_?.GpuGetAdapterCount != null ? symbols_.GpuGetAdapterCount() : 0;

  internal static bool GpuGetAdapterInfo(int index, out GpuAdapterInfo info) {
    if (symbols_?.GpuGetAdapterInfo != null) {
      return symbols_.GpuGetAdapterInfo(index, out info);
    }
    info = default;
    return false;
  }

  internal static void GpuSelectComputeAdapter(int adapter_index) =>
      symbols_?.GpuSelectComputeAdapter?.Invoke(adapter_index);

  internal static int GpuGetSelectedAdapter() =>
      symbols_?.GpuGetSelectedAdapter != null ? symbols_.GpuGetSelectedAdapter() : -1;

  internal static void GpuSetDmaMode(DmaTransferMode mode) =>
      symbols_?.GpuSetDmaMode?.Invoke((int)mode);

  internal static DmaTransferMode GpuGetDmaMode() =>
      symbols_?.GpuGetDmaMode != null ? (DmaTransferMode)symbols_.GpuGetDmaMode() : DmaTransferMode.PureCpu;

  internal static double GpuGetLastDmaWriteSeconds() =>
      symbols_?.GpuGetLastDmaWriteSeconds != null ? symbols_.GpuGetLastDmaWriteSeconds() : 0.0;

  internal static ulong GpuGetTotalDmaBytes() =>
      symbols_?.GpuGetTotalDmaBytes != null ? symbols_.GpuGetTotalDmaBytes() : 0;

  internal static bool GpuBenchmarkDmaTransfer(
      int segment_count, out double duration_seconds, out double bandwidth_gbps) {
    if (symbols_?.GpuBenchmarkDmaTransfer != null) {
      return symbols_.GpuBenchmarkDmaTransfer(segment_count, out duration_seconds, out bandwidth_gbps);
    }
    duration_seconds = 0.0;
    bandwidth_gbps = 0.0;
    return false;
  }

  internal static bool RunHardwareCalibration(out HardwareProfile profile) {
    if (symbols_?.RunHardwareCalibration != null) {
      return symbols_.RunHardwareCalibration(out profile);
    }
    profile = default;
    return false;
  }

  internal static bool GetHardwareProfile(out HardwareProfile profile) {
    if (symbols_?.GetHardwareProfile != null) {
      return symbols_.GetHardwareProfile(out profile);
    }
    profile = default;
    return false;
  }

  internal static void UpdateSceneFeedback(KspSceneType scene_type, double render_time_ms, double physics_time_ms) =>
      symbols_?.UpdateSceneFeedback?.Invoke((int)scene_type, render_time_ms, physics_time_ms);

  internal static void GetWorkloadBalanceState(out WorkloadBalanceState state) {
    if (symbols_?.GetWorkloadBalanceState != null) {
      symbols_.GetWorkloadBalanceState(out state);
      return;
    }
    state = default;
  }

  internal static void SetWorkloadBypass(bool master_bypass, int module_mask = ~0) =>
      symbols_?.SetWorkloadBypass?.Invoke(master_bypass, module_mask);

  internal static void SetManualWorkloadOverride(bool enable_manual, double manual_gpu_ratio) =>
      symbols_?.SetManualWorkloadOverride?.Invoke(enable_manual, manual_gpu_ratio);

  internal static void PartitionVesselBatch(int total_vessels, out int cpu_count, out int gpu_count) {
    if (symbols_?.PartitionVesselBatch != null) {
      symbols_.PartitionVesselBatch(total_vessels, out cpu_count, out gpu_count);
      return;
    }
    cpu_count = total_vessels;
    gpu_count = 0;
  }

  internal static bool GpuGetDeviceInfo(int index, out GpuDeviceInfo info) {
    if (symbols_?.GpuGetDeviceInfo != null) {
      return symbols_.GpuGetDeviceInfo(index, out info);
    }
    info = default;
    return false;
  }

  internal static void GpuSetDeviceRole(int adapter_index, GpuDeviceRole role) =>
      symbols_?.GpuSetDeviceRole?.Invoke(adapter_index, (int)role);

  internal static void GpuGetMultiDeviceSummary(
      out int device_count, out int active_count, out double total_compute_score) {
    if (symbols_?.GpuGetMultiDeviceSummary != null) {
      symbols_.GpuGetMultiDeviceSummary(out device_count, out active_count, out total_compute_score);
      return;
    }
    device_count = 0;
    active_count = 0;
    total_compute_score = 0.0;
  }

  internal static void PartitionMultiDeviceBatch(
      int total_work, int[] out_device_counts, int max_devices = 8) {
    if (symbols_?.PartitionMultiDeviceBatch != null) {
      symbols_.PartitionMultiDeviceBatch(total_work, out_device_counts, max_devices);
      return;
    }
    if (out_device_counts != null && out_device_counts.Length > 0) {
      Array.Clear(out_device_counts, 0, out_device_counts.Length);
      out_device_counts[0] = total_work; // CPU fallback
    }
  }

  internal static void GetCpuModuleThreadingStatus(
      this IntPtr plugin, out CpuModuleThreadingStatus status) {
    if (symbols_?.GetCpuModuleThreadingStatus != null) {
      symbols_.GetCpuModuleThreadingStatus(plugin, out status);
    } else {
      status = default;
    }
  }

  internal static void SetFrameBudgetMilliseconds(double ms) {
    symbols_?.SetFrameBudgetMilliseconds?.Invoke(ms);
  }

  internal static bool AdvanceInactiveVesselsBatchGpu(
      this IntPtr plugin, double time_step_seconds, int sub_steps) {
    if (symbols_?.AdvanceInactiveVesselsBatchGpu != null) {
      return symbols_.AdvanceInactiveVesselsBatchGpu(plugin, time_step_seconds, sub_steps);
    }
    return false;
  }

  internal static bool OptimizeFlightPlanShooting(
      in ShootingContextGpu context, out OptimalCandidateBurn out_optimal) {
    if (symbols_?.OptimizeFlightPlanShooting != null) {
      return symbols_.OptimizeFlightPlanShooting(in context, out out_optimal);
    }
    out_optimal = default;
    return false;
  }

  internal static void ApplyPartForcesBatch(
      this IntPtr plugin,
      PartForceEntry[] entries,
      int count) {
    symbols_?.ApplyPartForcesBatch?.Invoke(plugin, entries, count);
  }

  internal static bool PluginSaveToFile(
      this IntPtr plugin,
      string file_path,
      string compressor) {
    if (symbols_?.PluginSaveToFile != null) {
      return symbols_.PluginSaveToFile(plugin, file_path, compressor);
    }
    return false;
  }

  internal static bool PluginLoadFromFile(
      string file_path,
      string compressor,
      out IntPtr plugin_reader) {
    if (symbols_?.PluginLoadFromFile != null) {
      return symbols_.PluginLoadFromFile(
          file_path, compressor, out plugin_reader);
    }
    plugin_reader = IntPtr.Zero;
    return false;
  }

  internal static bool SaveMasterEphemerisCache(string file_path) {
    if (symbols_?.SaveMasterEphemerisCache != null) {
      return symbols_.SaveMasterEphemerisCache(file_path);
    }
    return false;
  }

  internal static bool LoadMasterEphemerisCache(string file_path) {
    if (symbols_?.LoadMasterEphemerisCache != null) {
      return symbols_.LoadMasterEphemerisCache(file_path);
    }
    return false;
  }

  internal static void LoadSymbols() {
    symbols_ = new Symbols();
  }
}

} // namespace ksp_plugin_adapter
} // namespace principia
