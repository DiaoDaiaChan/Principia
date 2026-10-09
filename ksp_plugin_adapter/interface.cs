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
    public delegate IntPtr StateBusCreateChannelDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StateBusDestroyChannelDelegate(ref IntPtr channel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StateBusCommitWriteDelegate(IntPtr channel, [In] ref DensePolynomialSlice slice);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool StateBusAcquireReadDelegate(IntPtr channel, out DensePolynomialSlice out_slice);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr CommandBusCreateDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CommandBusDestroyDelegate(ref IntPtr queue);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool CommandBusTryPushDelegate(IntPtr queue, [In] ref CommandPacket packet);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool CommandBusTryPopDelegate(IntPtr queue, out CommandPacket out_packet);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BatchInsertOrKeepLoadedPartsDelegate(
        IntPtr plugin,
        [In] PartBatchInsertState[] states,
        int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BatchPartSetApparentRigidMotionDelegate(
        IntPtr plugin,
        [In] PartBatchApparentMotion[] states,
        int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BatchPartGetActualRigidMotionDelegate(
        IntPtr plugin,
        [In] ref Origin origin,
        [In] uint[] part_ids,
        [Out] QPRW[] motions_out,
        int count);

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
    public delegate int ExportChebyshevSegmentsDelegate(
        IntPtr planetarium,
        IntPtr plugin,
        [MarshalAs(UnmanagedType.LPStr)] string vessel_guid,
        int segment_index,
        in double t_max,
        IntPtr buffer,
        int max_segments,
        [MarshalAs(UnmanagedType.I1)] bool is_d3d11_buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ExportPredictionChebyshevSegmentsDelegate(
        IntPtr planetarium,
        IntPtr plugin,
        [MarshalAs(UnmanagedType.LPStr)] string vessel_guid,
        in double t_max,
        IntPtr buffer,
        int max_segments,
        [MarshalAs(UnmanagedType.I1)] bool is_d3d11_buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool FlightPlanExportShootingContextDelegate(
        IntPtr plugin,
        [MarshalAs(UnmanagedType.LPStr)] string vessel_guid,
        int burn_index,
        out ShootingContextGpu context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int PackInactiveVesselsBatchDelegate(
        IntPtr plugin,
        [Out] OrbitalState64[] out_states,
        int max_states);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void UnpackInactiveVesselsBatchDelegate(
        IntPtr plugin,
        [In] OrbitalState64[] in_states,
        int count);

    public StateBusCreateChannelDelegate StateBusCreateChannel =
        Loader.LoadOptionalFunction<StateBusCreateChannelDelegate>("principia__StateBusCreateChannel");

    public StateBusDestroyChannelDelegate StateBusDestroyChannel =
        Loader.LoadOptionalFunction<StateBusDestroyChannelDelegate>("principia__StateBusDestroyChannel");

    public StateBusCommitWriteDelegate StateBusCommitWrite =
        Loader.LoadOptionalFunction<StateBusCommitWriteDelegate>("principia__StateBusCommitWrite");

    public StateBusAcquireReadDelegate StateBusAcquireRead =
        Loader.LoadOptionalFunction<StateBusAcquireReadDelegate>("principia__StateBusAcquireRead");

    public CommandBusCreateDelegate CommandBusCreate =
        Loader.LoadOptionalFunction<CommandBusCreateDelegate>("principia__CommandBusCreate");

    public CommandBusDestroyDelegate CommandBusDestroy =
        Loader.LoadOptionalFunction<CommandBusDestroyDelegate>("principia__CommandBusDestroy");

    public CommandBusTryPushDelegate CommandBusTryPush =
        Loader.LoadOptionalFunction<CommandBusTryPushDelegate>("principia__CommandBusTryPush");

    public CommandBusTryPopDelegate CommandBusTryPop =
        Loader.LoadOptionalFunction<CommandBusTryPopDelegate>("principia__CommandBusTryPop");

    public BatchInsertOrKeepLoadedPartsDelegate BatchInsertOrKeepLoadedParts =
        Loader.LoadOptionalFunction<BatchInsertOrKeepLoadedPartsDelegate>("principia__BatchInsertOrKeepLoadedParts");

    public BatchPartSetApparentRigidMotionDelegate BatchPartSetApparentRigidMotion =
        Loader.LoadOptionalFunction<BatchPartSetApparentRigidMotionDelegate>("principia__BatchPartSetApparentRigidMotion");

    public BatchPartGetActualRigidMotionDelegate BatchPartGetActualRigidMotion =
        Loader.LoadOptionalFunction<BatchPartGetActualRigidMotionDelegate>("principia__BatchPartGetActualRigidMotion");

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

    public ExportChebyshevSegmentsDelegate ExportChebyshevSegments =
        Loader.LoadOptionalFunction<ExportChebyshevSegmentsDelegate>("principia__ExportChebyshevSegments");

    public ExportPredictionChebyshevSegmentsDelegate ExportPredictionChebyshevSegments =
        Loader.LoadOptionalFunction<ExportPredictionChebyshevSegmentsDelegate>("principia__ExportPredictionChebyshevSegments");

    public FlightPlanExportShootingContextDelegate FlightPlanExportShootingContext =
        Loader.LoadOptionalFunction<FlightPlanExportShootingContextDelegate>("principia__FlightPlanExportShootingContext");

    public PackInactiveVesselsBatchDelegate PackInactiveVesselsBatch =
        Loader.LoadOptionalFunction<PackInactiveVesselsBatchDelegate>("principia__PackInactiveVesselsBatch");

    public UnpackInactiveVesselsBatchDelegate UnpackInactiveVesselsBatch =
        Loader.LoadOptionalFunction<UnpackInactiveVesselsBatchDelegate>("principia__UnpackInactiveVesselsBatch");
  }

  internal static bool HasDirectVramSupport =>
      symbols_?.PlanetariumPlotPredictionDirect != null;
  internal static bool HasNonBlockingHarvestSupport =>
      symbols_?.FutureTryHarvestVesselCatchUp != null;
  internal static bool HasStateBusSupport =>
      symbols_?.StateBusCreateChannel != null;
  internal static bool HasCommandBusSupport =>
      symbols_?.CommandBusCreate != null;
  internal static bool HasBatchSyncSupport =>
      symbols_?.BatchInsertOrKeepLoadedParts != null;

  internal static IntPtr StateBusCreateChannel() {
    return symbols_?.StateBusCreateChannel != null ? symbols_.StateBusCreateChannel() : IntPtr.Zero;
  }

  internal static void StateBusDestroyChannel(ref IntPtr channel) {
    symbols_?.StateBusDestroyChannel?.Invoke(ref channel);
  }

  internal static void StateBusCommitWrite(IntPtr channel, ref DensePolynomialSlice slice) {
    symbols_?.StateBusCommitWrite?.Invoke(channel, ref slice);
  }

  internal static bool StateBusAcquireRead(IntPtr channel, out DensePolynomialSlice out_slice) {
    if (symbols_?.StateBusAcquireRead != null) {
      return symbols_.StateBusAcquireRead(channel, out out_slice);
    }
    out_slice = default(DensePolynomialSlice);
    return false;
  }

  internal static IntPtr CommandBusCreate() {
    return symbols_?.CommandBusCreate != null ? symbols_.CommandBusCreate() : IntPtr.Zero;
  }

  internal static void CommandBusDestroy(ref IntPtr queue) {
    symbols_?.CommandBusDestroy?.Invoke(ref queue);
  }

  internal static bool CommandBusTryPush(IntPtr queue, ref CommandPacket packet) {
    return symbols_?.CommandBusTryPush != null && symbols_.CommandBusTryPush(queue, ref packet);
  }

  internal static bool CommandBusTryPop(IntPtr queue, out CommandPacket out_packet) {
    if (symbols_?.CommandBusTryPop != null) {
      return symbols_.CommandBusTryPop(queue, out out_packet);
    }
    out_packet = default(CommandPacket);
    return false;
  }

  internal static void BatchInsertOrKeepLoadedParts(this IntPtr plugin, PartBatchInsertState[] states, int count) {
    symbols_?.BatchInsertOrKeepLoadedParts?.Invoke(plugin, states, count);
  }

  internal static void BatchPartSetApparentRigidMotion(this IntPtr plugin, PartBatchApparentMotion[] states, int count) {
    symbols_?.BatchPartSetApparentRigidMotion?.Invoke(plugin, states, count);
  }

  internal static void BatchPartGetActualRigidMotion(this IntPtr plugin, ref Origin origin, uint[] part_ids, QPRW[] motions_out, int count) {
    symbols_?.BatchPartGetActualRigidMotion?.Invoke(plugin, ref origin, part_ids, motions_out, count);
  }

  internal static bool HasNativeEphemerisPacer() {
    return symbols_?.EphemerisGetHorizonSeconds != null;
  }

  internal static double EphemerisGetHorizonSeconds(this IntPtr plugin) {
    return symbols_?.EphemerisGetHorizonSeconds != null ? symbols_.EphemerisGetHorizonSeconds(plugin) : 0.0;
  }

  internal static double EphemerisGetTargetExtendedTime(this IntPtr plugin) {
    return symbols_?.EphemerisGetTargetExtendedTime != null ? symbols_.EphemerisGetTargetExtendedTime(plugin) : 0.0;
  }

  internal static bool EphemerisIsProlonging(this IntPtr plugin) {
    return symbols_?.EphemerisIsProlonging != null && symbols_.EphemerisIsProlonging(plugin);
  }

  internal static void EphemerisForceProlong(this IntPtr plugin) {
    symbols_?.EphemerisForceProlong?.Invoke(plugin);
  }

  internal static void EphemerisAdjustHorizon(this IntPtr plugin, double delta_seconds) {
    symbols_?.EphemerisAdjustHorizon?.Invoke(plugin, delta_seconds);
  }

  internal static void EphemerisTogglePause(this IntPtr plugin) {
    symbols_?.EphemerisTogglePause?.Invoke(plugin);
  }

  internal static bool EphemerisIsPaused(this IntPtr plugin) {
    return symbols_?.EphemerisIsPaused != null && symbols_.EphemerisIsPaused(plugin);
  }

  internal static bool HasGpuInteropSupport =>
      symbols_?.GpuGetAdapterCount != null;

  internal static int GpuGetAdapterCount() {
    return symbols_?.GpuGetAdapterCount != null ? symbols_.GpuGetAdapterCount() : 0;
  }

  internal static bool GpuGetAdapterInfo(int index, out GpuAdapterInfo info) {
    if (symbols_?.GpuGetAdapterInfo != null) {
      return symbols_.GpuGetAdapterInfo(index, out info);
    }
    info = default(GpuAdapterInfo);
    return false;
  }

  internal static void GpuSelectComputeAdapter(int adapter_index) {
    symbols_?.GpuSelectComputeAdapter?.Invoke(adapter_index);
  }

  internal static int GpuGetSelectedAdapter() {
    return symbols_?.GpuGetSelectedAdapter != null ? symbols_.GpuGetSelectedAdapter() : -1;
  }

  internal static void GpuSetDmaMode(DmaTransferMode mode) {
    symbols_?.GpuSetDmaMode?.Invoke((int)mode);
  }

  internal static DmaTransferMode GpuGetDmaMode() {
    return symbols_?.GpuGetDmaMode != null
        ? (DmaTransferMode)symbols_.GpuGetDmaMode()
        : DmaTransferMode.StagingFallback;
  }

  internal static double GpuGetLastDmaWriteSeconds() {
    return symbols_?.GpuGetLastDmaWriteSeconds != null ? symbols_.GpuGetLastDmaWriteSeconds() : 0.0;
  }

  internal static ulong GpuGetTotalDmaBytes() {
    return symbols_?.GpuGetTotalDmaBytes != null ? symbols_.GpuGetTotalDmaBytes() : 0UL;
  }

  internal static int ExportChebyshevSegments(
      IntPtr planetarium,
      IntPtr plugin,
      string vessel_guid,
      int segment_index,
      in double t_max,
      IntPtr buffer,
      int max_segments,
      bool is_d3d11_buffer) {
    return symbols_?.ExportChebyshevSegments != null
        ? symbols_.ExportChebyshevSegments(planetarium, plugin, vessel_guid, segment_index, in t_max, buffer, max_segments, is_d3d11_buffer)
        : 0;
  }

  internal static int ExportPredictionChebyshevSegments(
      IntPtr planetarium,
      IntPtr plugin,
      string vessel_guid,
      in double t_max,
      IntPtr buffer,
      int max_segments,
      bool is_d3d11_buffer) {
    return symbols_?.ExportPredictionChebyshevSegments != null
        ? symbols_.ExportPredictionChebyshevSegments(planetarium, plugin, vessel_guid, in t_max, buffer, max_segments, is_d3d11_buffer)
        : 0;
  }

  internal static bool FlightPlanExportShootingContext(
      IntPtr plugin,
      string vessel_guid,
      int burn_index,
      out ShootingContextGpu context) {
    if (symbols_?.FlightPlanExportShootingContext != null) {
      return symbols_.FlightPlanExportShootingContext(plugin, vessel_guid, burn_index, out context);
    }
    context = default(ShootingContextGpu);
    return false;
  }

  internal static int PackInactiveVesselsBatch(
      this IntPtr plugin,
      OrbitalState64[] out_states,
      int max_states) {
    return symbols_?.PackInactiveVesselsBatch != null
        ? symbols_.PackInactiveVesselsBatch(plugin, out_states, max_states)
        : 0;
  }

  internal static void UnpackInactiveVesselsBatch(
      this IntPtr plugin,
      OrbitalState64[] in_states,
      int count) {
    symbols_?.UnpackInactiveVesselsBatch?.Invoke(plugin, in_states, count);
  }

  internal static void LoadSymbols() {
    symbols_ = new Symbols();
  }
}

} // namespace ksp_plugin_adapter
} // namespace principia
