using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEngine.Profiling;
using static principia.ksp_plugin_adapter.FrameType;

namespace principia {
namespace ksp_plugin_adapter {

[KSPScenario(createOptions: ScenarioCreationOptions.AddToAllGames,
             tgtScenes: new GameScenes[]{GameScenes.FLIGHT,
                                         GameScenes.MAINMENU,
                                         GameScenes.SPACECENTER,
                                         GameScenes.TRACKSTATION})]
public partial class PrincipiaPluginAdapter : ScenarioModule,
                                              SupervisedWindowRenderer.
                                              ISupervisor {
  // From https://forum.kerbalspaceprogram.com/index.php?/topic/84273--/,
  // edited 2017-03-09.  Where the name of the layer is not CamelCase, the
  // actual name is commented.
  public enum UnityLayers {
    TransparentFX = 1,
    IgnoreRaycast = 2,  // Ignore Raycast
    Water = 3,
    UI = 5,
    PartListIcons = 8,  // PartsList_Icons
    Atmosphere = 9,
    ScaledScenery = 10,  // Scaled Scenery
    UIDialog = 11,
    UIVectors = 12,
    UIMask = 13,
    Screens = 14,
    LocalScenery = 15,  // Local Scenery
    Kerbals = 16,
    EVA = 17,
    SkySphere = 18,
    PhysicalObjects = 19,
    InternalSpace = 20,  // Internal Space
    PartTriggers = 21,  // Part Triggers
    KerbalInstructors = 22,
    AeroFXIgnore = 23,
    MapFX = 24,
    UIAdditional = 25,
    WheelCollidersIgnore = 26,
    WheelColliders = 27,  // wheelColliders
    TerrainColliders = 28,
    DragRender = 29,
    SurfaceFX = 30,
    Vectors = 31,
  };

  private const string principia_serialized_plugin = "serialized_plugin";
  private const string principia_sidecar_file = "principia_sidecar_file";
  private const string principia_sidecar_version = "principia_sidecar_version";
  private const string principia_sidecar_timestamp = "principia_sidecar_timestamp";
  private const string principia_sidecar_chunks = "principia_sidecar_chunks";
  private const uint principia_sidecar_current_version = 1;

  private static string PluginStatesDirectory =>
      Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "Principia", "PluginStates");

  private static void EnsurePluginStatesDirectoryExists() {
    string dir = PluginStatesDirectory;
    if (!Directory.Exists(dir)) {
      Directory.CreateDirectory(dir);
    }
  }

  private const string principia_initial_state_config_name =
      "principia_initial_state";
  private const string principia_gravity_model_config_name =
      "principia_gravity_model";
  private const string principia_numerics_blueprint_config_name =
      "principia_numerics_blueprint";
  private const string principia_override_version_check_config_name =
      "principia_override_version_check";
  private const string principia_flags = "principia_flags";
  private const string principia_draw_styles_config_name =
      "principia_draw_styles";
  private const string principia_celestial_lod_config_name =
      "principia_celestial_lod";

  private KSP.UI.Screens.ApplicationLauncherButton toolbar_button_;
  // Whether the user has hidden the UI.
  private bool hide_all_gui_ = false;
  // Whether we are in a scene/building where we wish to show our UI.
  private bool in_principia_scene_ = true;
  // Whether we are in the main menu.
  private readonly bool in_main_menu_ =
      HighLogic.LoadedScene == GameScenes.MAINMENU;

  private IntPtr plugin_ = IntPtr.Zero;
  private IntPtr plugin_reader_ = IntPtr.Zero;

  internal IntPtr Plugin() {
    return plugin_;
  }

  // Whether to compress saves.
  [KSPField(isPersistant = true)]
  private string serialization_compression_ = "";
  [KSPField(isPersistant = true)]
  private string serialization_encoding_ = "hexadecimal";

  // Whether the plotting frame must be set to something convenient at the next
  // opportunity.
  private bool must_set_plotting_frame_ = false;

  private bool time_is_advancing_;
  internal bool is_time_advancing => time_is_advancing_;
  internal bool flight_plan_dirty {
    get => flight_plan_dirty_;
    set {
      flight_plan_dirty_ = value;
      if (value) {
        MapMarkerEventGovernor.InvalidateFlightPlan();
      }
    }
  }
  private bool flight_plan_dirty_ = false;

  // Used to detect changes of SOI and skip two frames during which we use the
  // EulerSolver.
  CelestialBody last_main_body_;
  private int main_body_change_countdown_ = 1;
  private bool celestial_terrains_were_validated_ = false;

  private PlanetariumCameraAdjuster planetarium_camera_adjuster_;

  private RenderingActions map_renderer_;
  private RenderingActions galaxy_cube_rotator_;

  private KSP.UI.Screens.Flight.NavBall navball_;
  private UnityEngine.Material navball_material_;
  private UnityEngine.Texture compass_navball_texture_;
  private UnityEngine.Texture inertial_navball_texture_;
  private UnityEngine.Texture barycentric_navball_texture_;
  private UnityEngine.Texture body_direction_navball_texture_;
  private UnityEngine.Texture surface_navball_texture_;
  private UnityEngine.Texture target_navball_texture_;
  private bool navball_changed_ = true;
  private FlightGlobals.SpeedDisplayModes? previous_display_mode_;

  // TODO(egg): move all of that to the plotter.
  internal UnityEngine.Color history_colour = XKCDColors.Lime;
  internal GLLines.Style history_style = GLLines.Style.Faded;
  internal UnityEngine.Color prediction_colour = XKCDColors.Fuchsia;
  internal GLLines.Style prediction_style = GLLines.Style.Solid;
  internal UnityEngine.Color flight_plan_colour = XKCDColors.PeriwinkleBlue;
  internal GLLines.Style flight_plan_style = GLLines.Style.Dashed;
  internal UnityEngine.Color burn_colour = XKCDColors.Pink;
  internal GLLines.Style burn_style = GLLines.Style.Solid;
  internal UnityEngine.Color target_history_colour = XKCDColors.Goldenrod;
  internal GLLines.Style target_history_style = GLLines.Style.Faded;
  internal UnityEngine.Color target_prediction_colour = XKCDColors.LightMauve;
  internal GLLines.Style target_prediction_style = GLLines.Style.Solid;

  private readonly Plotter plotter_;
  private RollingEphemerisDriver rolling_ephemeris_driver_;
  internal RollingEphemerisDriver rolling_ephemeris_driver =>
      rolling_ephemeris_driver_;
  private Vessel last_rsas_vessel_ = null;

  private readonly List<IntPtr> vessel_futures_ = new List<IntPtr>();
  private readonly EventVoidHolder event_void_holder_ = new EventVoidHolder();

  private readonly Dictionary<Guid, string> vessel_guid_cache_ =
      new Dictionary<Guid, string>();
  private readonly HashSet<Vessel> collided_vessels_cache_ =
      new HashSet<Vessel>();
  private static readonly Vector3d unit_x_ = new Vector3d(1, 0, 0);
  private static readonly Vector3d unit_y_ = new Vector3d(0, 1, 0);
  private static readonly Vector3d unit_z_ = new Vector3d(0, 0, 1);

  private readonly Dictionary<Part, CollisionReporter> collision_reporters_ =
      new Dictionary<Part, CollisionReporter>();
  private readonly Dictionary<int, int> celestial_parent_cache_ =
      new Dictionary<int, int>();
  private CelestialBody last_main_body_for_rotation_;
  private Orbit scratch_orbit_;
  private List<CelestialBody> topological_bodies_;

  private string GetVesselGuid(Vessel vessel) {
    if (vessel == null) {
      return null;
    }
    if (!vessel_guid_cache_.TryGetValue(vessel.id, out string guid_str)) {
      guid_str = vessel.id.ToString();
      vessel_guid_cache_[vessel.id] = guid_str;
    }
    return guid_str;
  }

  private void BuildTopologicalBodies() {
    if (Planetarium.fetch?.Sun?.orbitingBodies == null) {
      return;
    }
    var bodies = new List<CelestialBody>();
    Stack<CelestialBody> stack = new Stack<CelestialBody>();
    foreach (CelestialBody child in Planetarium.fetch.Sun.orbitingBodies) {
      stack.Push(child);
    }
    while (stack.Count > 0) {
      CelestialBody body = stack.Pop();
      bodies.Add(body);
      foreach (CelestialBody child in body.orbitingBodies) {
        stack.Push(child);
      }
    }
    topological_bodies_ = bodies;
  }

  // The RSAS is the component of the stock KSP autopilot that deals with
  // orienting the vessel towards a specific direction (e.g. prograde).
  // It is, as usual for KSP, an ineffable acronym; it is however likely derived
  // from the name of the SAS, the component of the autopilot that deals with
  // stabilizing the vessel's attitude without fixing it to any particular
  // target.  Note that SAS has several known meanings, none of which are
  // noteworthy.  The interested reader can refer to
  // http://wiki.kerbalspaceprogram.com/wiki/SAS.
  private bool override_rsas_target_ = false;
  private Vector3d rsas_target_;
  private bool reset_rsas_target_ = false;

  private int? last_guidance_manœuvre_ = null;

  private static Dictionary<CelestialBody, Orbit> unmodified_orbits_;
  private static Dictionary<CelestialBody, double>
      unmodified_initial_rotations_;
  private readonly Dictionary<int, MotionSnapshot> celestial_last_motion_ =
      new Dictionary<int, MotionSnapshot>();

  private Krakensbane krakensbane_;

  private Krakensbane krakensbane {
    get {
      if (krakensbane_ == null) {
        krakensbane_ = (Krakensbane)FindObjectOfType(typeof(Krakensbane));
      }
      return krakensbane_;
    }
  }

  private KSP.UI.Screens.SpaceTracking space_tracking_;

  private KSP.UI.Screens.SpaceTracking space_tracking {
    get {
      if (space_tracking_ == null) {
        if (HighLogic.LoadedScene != GameScenes.TRACKSTATION) {
          return null;
        }
        space_tracking_ = (KSP.UI.Screens.SpaceTracking)FindObjectOfType(
            typeof(KSP.UI.Screens.SpaceTracking));
      }
      return space_tracking_;
    }
  }

  private KSP.UI.Screens.DebugToolbar.DebugScreen debug_screen_;
  private KSP.UI.Screens.DebugToolbar.Screens.Cheats.HackGravity hack_gravity_;

  private KSP.UI.Screens.DebugToolbar.Screens.Cheats.HackGravity hack_gravity {
    get {
      if (hack_gravity_ == null) {
        if (debug_screen_ == null) {
          // Force a debug screen to be instantiated, by showing it.
          KSP.UI.Screens.DebugToolbar.DebugScreenSpawner.ShowDebugScreen();
          debug_screen_ =
              (KSP.UI.Screens.DebugToolbar.DebugScreen)FindObjectOfType(
                  typeof(KSP.UI.Screens.DebugToolbar.DebugScreen));
          // Now hide it.
          debug_screen_.Hide();
        }
        // Since we have the debug screen, we can restrict our search for the
        // gravity-hacking control within it, so as not to slow things down to
        // a grind until it is instantiated.
        hack_gravity_ = debug_screen_.contentTransform.
            GetComponentInChildren<
                KSP.UI.Screens.DebugToolbar.Screens.Cheats.HackGravity>();
      }
      return hack_gravity_;
    }
  }

  // TODO(egg): these should be moved to the C++; there it can be made a vector
  // because we can test whether we have the part or not. Set in
  // FashionablyLate, before the FlightIntegrator clears the forces.  Used in
  // WaitForFixedUpdate.
  private readonly Dictionary<uint, Vector3d> part_id_to_intrinsic_torque_ =
      new Dictionary<uint, Vector3d>();
  private readonly Dictionary<uint, Vector3d> part_id_to_intrinsic_force_ =
      new Dictionary<uint, Vector3d>();

  private readonly Dictionary<Vessel, Vector3d>
      parachuting_kerbal_angular_velocities_ =
          new Dictionary<Vessel, Vector3d>();

  // Work around the launch backflip issue encountered while releasing
  // Frobenius.
  // The issue is that the position of a  `ForceHolder` collected in
  // `FashionablyLate` or constructed in `JaiFailliAttendre` is made invalid by
  // changes to world coordinates in `FloatingOrigin` by the time it is used in
  // `WaitForFixedUpdate`.
  // TODO(egg): This should be cleaned up a bit, e.g., by making the plugin and
  // interface take the `World` lever arm directly, since the lever arm is what
  // we use in `principia::ksp_plugin::Part`, and it is what we compute below:
  // this would obviate the need to keep track of the `Part` and to adjust its
  // position.
  readonly struct PartCentredForceHolder {
    public PartCentredForceHolder(Vector3d force, Vector3d lever_arm) {
      force_in_world_coordinates_ = force;
      lever_arm_in_world_coordinates_ = lever_arm;
    }

    public static PartCentredForceHolder FromPartForceHolder(
        Part part,
        Part.ForceHolder holder) {
      return new PartCentredForceHolder(
          holder.force,
          holder.pos - part.rb.worldCenterOfMass);
    }

    public Vector3d force => force_in_world_coordinates_;
    public Vector3d lever_arm => lever_arm_in_world_coordinates_;

    private readonly Vector3d force_in_world_coordinates_;
    private readonly Vector3d lever_arm_in_world_coordinates_;
  }

  private readonly Dictionary<uint, List<PartCentredForceHolder>>
      part_id_to_intrinsic_forces_ =
          new Dictionary<uint, List<PartCentredForceHolder>>();
  private readonly Stack<List<PartCentredForceHolder>>
      intrinsic_forces_pool_ = new Stack<List<PartCentredForceHolder>>();

  private List<PartCentredForceHolder> AcquireForceList() {
    if (intrinsic_forces_pool_.Count > 0) {
      var list = intrinsic_forces_pool_.Pop();
      list.Clear();
      return list;
    }
    return new List<PartCentredForceHolder>(4);
  }

  private void ReleaseAllForceLists() {
    foreach (var list in part_id_to_intrinsic_forces_.Values) {
      list.Clear();
      intrinsic_forces_pool_.Push(list);
    }
    part_id_to_intrinsic_forces_.Clear();
  }

  // The degrees of freedom at BetterLateThanNever.  Those are used to insert
  // new parts with the correct initial state.
  private readonly Dictionary<uint, QP> part_id_to_degrees_of_freedom_ =
      new Dictionary<uint, QP>();

  private readonly MapNodePool map_node_pool_;
  private readonly HashSet<KSP.UI.Screens.Mapview.MapNode> hooked_map_nodes_ =
      new HashSet<KSP.UI.Screens.Mapview.MapNode>();
  private double last_stock_trajectories_cleanup_time_ = 0.0;
  private string last_collision_vessel_guid_ = null;
  private readonly TQP[] single_collision_marker_array_ = new TQP[1];
  private string last_markers_vessel_guid_ = null;
  private string last_markers_target_guid_ = null;
  private readonly Dictionary<UnityEngine.Transform, UnityEngine.MeshRenderer>
      navball_vector_renderers_ =
          new Dictionary<UnityEngine.Transform, UnityEngine.MeshRenderer>();
  private readonly Dictionary<UnityEngine.Transform, UnityEngine.Material>
      navball_vector_materials_ =
          new Dictionary<UnityEngine.Transform, UnityEngine.Material>();
  private double last_prediction_integration_realtime_ = 0.0;
  private double last_prediction_integration_ut_ = -1.0;
  private string last_prediction_integration_guid_ = null;
  private string last_prediction_integration_target_guid_ = null;
  private ManeuverNode guidance_node_;

  // These need to be fields for communication between `LateUpdate` and
  // `pre_cull`.
  private TQP? prediction_collision_ = null;
  private TQP? flight_plan_collision_ = null;

  private readonly List<ManœuvreMarker> manœuvre_marker_pool_ =
      new List<ManœuvreMarker>();

  // UI for the apocalypse notification.
  [KSPField(isPersistant = true)]
  private readonly Dialog apocalypse_dialog_ = new Dialog(persist_state: true);

  // UI for the bad installation notification.
  private readonly bool is_bad_installation_ = false;  // Don't persist.
  [KSPField(isPersistant = true)]
  private readonly Dialog bad_installation_dialog_ =
      new Dialog(persist_state: false);
  private MigrationMonitor migration_monitor_ = null;

  // The game windows.
  [KSPField(isPersistant = true)]
  private readonly FlightPlanner flight_planner_;
  [KSPField(isPersistant = true)]
  private readonly OrbitAnalyser orbit_analyser_;
  [KSPField(isPersistant = true)]
  private readonly GpuDiagnosticsWindow gpu_diagnostics_window_;
  [KSPField(isPersistant = true)]
  internal readonly ReferenceFrameSelector<PlottingFrameParameters>
      plotting_frame_selector_;
  [KSPField(isPersistant = true)]
  private readonly MainWindow main_window_;

  public bool show_celestial_trajectory(CelestialBody celestial) {
    return plotting_frame_selector_.pinned[celestial] ||
           !main_window_.frames_that_hide_unpinned_celestials.Contains(
               plotting_frame_selector_.FrameParameters());
  }

  public event Action LockClearing;
  public event Action WindowsDisposal;
  public event Action WindowsRendering;

  PrincipiaPluginAdapter() {
    // We create this directory here so we do not need to worry about cross-
    // platform problems in C++.
    Directory.CreateDirectory("glog/Principia");
    string load_error = Loader.LoadPrincipiaDllAndInitGoogleLogging();
    if (load_error == null) {
      is_bad_installation_ = false;
      bad_installation_dialog_.Hide();
    } else {
      is_bad_installation_ = true;
      Interface.GetVersion(build_date : out string _,
                           version    : out string version,
                           platform   : out string _);
      bad_installation_dialog_.message = L10N.CacheFormat(
          "#Principia_DLLFailedToLoad",
          version,
          load_error);
      bad_installation_dialog_.Show();
    }
#if KSP_VERSION_1_12_5
    if (!(Versioning.version_major == 1 &&
          (Versioning.version_minor == 8 && Versioning.Revision == 1) ||
          (Versioning.version_minor == 9 && Versioning.Revision == 1) ||
          (Versioning.version_minor == 10 && Versioning.Revision == 1) ||
          (Versioning.version_minor == 11 && Versioning.Revision <= 2) ||
          (Versioning.version_minor == 12 && (Versioning.Revision >= 2 &&
                                              Versioning.Revision <= 5)))) {
      string expected_version =
          "1.8.1, 1.9.1, 1.10.1, 1.11.x, 1.12.2, 1.12.3, 1.12.4, and 1.12.5";
#elif KSP_VERSION_1_7_3
    if (!(Versioning.version_major == 1 &&
          (Versioning.version_minor == 5 && Versioning.Revision == 1) ||
          (Versioning.version_minor == 6 && Versioning.Revision == 1) ||
          (Versioning.version_minor == 7 && Versioning.Revision <= 3))) {
      string expected_version = "1.7.3, 1.7.2, 1.7.1, 1.7.0, 1.6.1, and 1.5.1";
#endif
      string message = $@"Unexpected KSP version {Versioning.version_major}.{
            Versioning.version_minor}.{
            Versioning.Revision}; this build targets {expected_version}.";
      bool version_check_overridden = false;
      ConfigNode override_version_check =
          GameDatabase.Instance.GetAtMostOneNode(
              principia_override_version_check_config_name);
      if (override_version_check != null) {
        foreach (var version in override_version_check.GetValues("version")) {
          if (version ==
              $@"{Versioning.version_major}.{Versioning.version_minor}.{
                Versioning.Revision}") {
            version_check_overridden = true;
            break;
          }
        }
      }
      if (version_check_overridden) {
        Log.Error(message);
      } else {
        Log.Fatal(message);
      }
    }

    map_node_pool_ = new MapNodePool(
        this,
        visibility_modifiers: () => new MapNodePool.VisibilityModifiers(
            show_unpinned: !main_window_.frames_that_hide_unpinned_markers.
                               Contains(
                                   plotting_frame_selector_.FrameParameters()),
            can_hover: !ManœuvreMarker.has_interacting_marker)
        );
    flight_planner_ = new FlightPlanner(this, PredictedVessel);
    orbit_analyser_ = new CurrentOrbitAnalyser(this, PredictedVessel);
    gpu_diagnostics_window_ = new GpuDiagnosticsWindow(this);
    plotting_frame_selector_ =
        new ReferenceFrameSelector<PlottingFrameParameters>(
            this,
            UpdatePlottingFrame,
            L10N.CacheFormat("#Principia_PlottingFrame"));
    plotter_ = new Plotter(this);
    main_window_ = new MainWindow(this,
                                  flight_planner_,
                                  orbit_analyser_,
                                  gpu_diagnostics_window_,
                                  plotting_frame_selector_,
                                  PredictedVessel);
  }

  ~PrincipiaPluginAdapter() {
    // We should not get here without deleting the plugin, but just for safety.
    Interface.DeletePlugin(ref plugin_);
  }

  public bool PluginRunning() {
    if (plugin_ != IntPtr.Zero && rolling_ephemeris_driver_ == null) {
      rolling_ephemeris_driver_ = new RollingEphemerisDriver(plugin_, this);
    }
    return plugin_ != IntPtr.Zero;
  }

  private Vessel PredictedVessel() {
    if (!PluginRunning()) {
      return null;
    }
    Vessel vessel =
        FlightGlobals.ActiveVessel ?? space_tracking?.SelectedVessel;
    string vessel_guid = GetVesselGuid(vessel);
    if (vessel_guid != null && plugin_.HasVessel(vessel_guid)) {
      return vessel;
    } else {
      return null;
    }
  }

  // If there is a target vessel, and that vessel is known to the plugin,
  // returns it.  Otherwise returns null.
  private Vessel TargetVessel() {
    Vessel target = FlightGlobals.fetch.VesselTarget?.GetVessel();
    string target_id = GetVesselGuid(target);
    if (target_id != null && plugin_.HasVessel(target_id)) {
      return target;
    } else {
      return null;
    }
  }

  // Same as above but returns the GUID.
  internal string TargetVesselGuid() {
    Vessel target = TargetVessel();
    return target != null ? GetVesselGuid(target) : null;
  }


  private delegate void BodyProcessor(CelestialBody body);

  private delegate void VesselProcessor(Vessel vessel);

  // Applies `process_body` to all bodies but the sun in the tree of celestial
  // bodies, in topological order.
  private void ApplyToBodyTree(BodyProcessor process_body) {
    if (topological_bodies_ == null || topological_bodies_.Count == 0) {
      BuildTopologicalBodies();
    }
    if (topological_bodies_ == null) {
      return;
    }
    for (int i = 0; i < topological_bodies_.Count; ++i) {
      process_body(topological_bodies_[i]);
    }
  }

  private void ApplyToVesselsOnRails(double universal_time) {
    UnityEngine.Physics.autoSyncTransforms = false;
    var vessels = FlightGlobals.Vessels;

    for (int i = 0; i < vessels.Count; ++i) {
      var vessel = vessels[i];
      if (!is_manageable_on_rails(vessel)) {
        continue;
      }
      UpdateVessel(vessel, universal_time);
    }
    UnityEngine.Physics.SyncTransforms();
    UnityEngine.Physics.autoSyncTransforms = true;
  }

  private void ApplyToVesselsOnRails(VesselProcessor process_vessel) {
    // `process_vessels` may touch `Transform`s, so disable syncing.
    UnityEngine.Physics.autoSyncTransforms = false;
    var vessels = FlightGlobals.Vessels;

    for (int i = 0; i < vessels.Count; ++i) {
      var vessel = vessels[i];
      if (!is_manageable_on_rails(vessel)) {
        continue;
      }
      process_vessel(vessel);
    }
    UnityEngine.Physics.SyncTransforms();
    UnityEngine.Physics.autoSyncTransforms = true;
  }

  // Dynamic capacity tracking buffers to support RSS Origin 2 and large planetary systems (400+ bodies).
  private float[] celestial_last_update_realtime_ = new float[1024];
  private int[] celestial_last_update_frame_ = new int[1024];

  private void EnsureCelestialTrackingCapacity(int index) {
    if (index >= celestial_last_update_realtime_.Length) {
      int new_size = Math.Max(index + 256, celestial_last_update_realtime_.Length * 2);
      Array.Resize(ref celestial_last_update_realtime_, new_size);
      Array.Resize(ref celestial_last_update_frame_, new_size);
    }
  }

  internal struct CelestialLodParameters {
    public float tier1_interval;
    public float tier2_interval;
    public float tier3_interval;
    public double high_salience_threshold;
    public double medium_salience_threshold;
    public double relative_scale_threshold;
    public double home_relative_scale_threshold;

    public static CelestialLodParameters Default => new CelestialLodParameters {
      tier1_interval = 0.10f,
      tier2_interval = 0.50f,
      tier3_interval = 2.00f,
      high_salience_threshold = 3e-4,
      medium_salience_threshold = 3e-6,
      relative_scale_threshold = 0.015,
      home_relative_scale_threshold = 0.03,
    };

    public static CelestialLodParameters FromConfigNode(ConfigNode node) {
      var p = Default;
      if (node == null) return p;
      if (node.HasValue("tier1_update_interval") &&
          float.TryParse(node.GetValue("tier1_update_interval"), out float t1)) {
        p.tier1_interval = t1;
      }
      if (node.HasValue("tier2_update_interval") &&
          float.TryParse(node.GetValue("tier2_update_interval"), out float t2)) {
        p.tier2_interval = t2;
      }
      if (node.HasValue("tier3_update_interval") &&
          float.TryParse(node.GetValue("tier3_update_interval"), out float t3)) {
        p.tier3_interval = t3;
      }
      if (node.HasValue("high_salience_threshold") &&
          double.TryParse(node.GetValue("high_salience_threshold"), out double hs)) {
        p.high_salience_threshold = hs;
      }
      if (node.HasValue("medium_salience_threshold") &&
          double.TryParse(node.GetValue("medium_salience_threshold"), out double ms)) {
        p.medium_salience_threshold = ms;
      }
      if (node.HasValue("relative_scale_threshold") &&
          double.TryParse(node.GetValue("relative_scale_threshold"), out double rs)) {
        p.relative_scale_threshold = rs;
      }
      if (node.HasValue("home_relative_scale_threshold") &&
          double.TryParse(node.GetValue("home_relative_scale_threshold"), out double hrs)) {
        p.home_relative_scale_threshold = hrs;
      }
      return p;
    }
  }

  internal struct CelestialStaticLodProfile {
    public int system_depth;
    public double relative_radius_ratio;
    public double home_radius_ratio;
    public double radius_squared;
    public bool is_major_body;
    public bool is_initialized;
  }

  private CelestialLodParameters celestial_lod_params_ = CelestialLodParameters.Default;
  private CelestialStaticLodProfile[] celestial_static_profiles_ = new CelestialStaticLodProfile[1024];
  private Vector3d cached_camera_world_position_ = Vector3d.zero;
  private bool camera_world_position_valid_ = false;

  private void EnsureCelestialStaticProfilesCapacity(int index) {
    if (index >= celestial_static_profiles_.Length) {
      int new_size = Math.Max(index + 256, celestial_static_profiles_.Length * 2);
      Array.Resize(ref celestial_static_profiles_, new_size);
    }
  }

  private CelestialStaticLodProfile GetOrComputeCelestialStaticProfile(CelestialBody body) {
    int index = body.flightGlobalsIndex;
    if (index >= 0) {
      EnsureCelestialStaticProfilesCapacity(index);
      if (celestial_static_profiles_[index].is_initialized) {
        return celestial_static_profiles_[index];
      }
    }

    int depth = 0;
    CelestialBody curr = body;
    while (curr.orbit != null && curr.orbit.referenceBody != null && curr.orbit.referenceBody != curr) {
      depth++;
      curr = curr.orbit.referenceBody;
    }

    double rel_ratio = 1.0;
    if (body.orbit != null && body.orbit.referenceBody != null && body.orbit.referenceBody.Radius > 0) {
      rel_ratio = body.Radius / body.orbit.referenceBody.Radius;
    }

    CelestialBody home = FlightGlobals.GetHomeBody();
    double home_radius = home != null ? home.Radius : 6371000.0;
    double home_ratio = body.Radius / home_radius;

    bool is_major = (depth <= 1) ||
                    (rel_ratio >= celestial_lod_params_.relative_scale_threshold) ||
                    (home_ratio >= celestial_lod_params_.home_relative_scale_threshold);

    var profile = new CelestialStaticLodProfile {
      system_depth = depth,
      relative_radius_ratio = rel_ratio,
      home_radius_ratio = home_ratio,
      radius_squared = body.Radius * body.Radius,
      is_major_body = is_major,
      is_initialized = true,
    };

    if (index >= 0) {
      celestial_static_profiles_[index] = profile;
    }
    return profile;
  }

  private void UpdateCameraWorldPositionForLod() {
    camera_world_position_valid_ = false;
    if (MapView.MapIsEnabled && PlanetariumCamera.fetch != null) {
      cached_camera_world_position_ = ScaledSpace.ScaledToLocalSpace(PlanetariumCamera.fetch.transform.position);
      camera_world_position_valid_ = true;
    } else if (FlightCamera.fetch?.mainCamera != null) {
      cached_camera_world_position_ = FlightCamera.fetch.mainCamera.transform.position;
      camera_world_position_valid_ = true;
    }
  }

  private void LoadCelestialLodParameters(ConfigNode lod_node) {
    if (lod_node == null) {
      celestial_lod_params_ = CelestialLodParameters.Default;
      Log.Info("Using default adaptive celestial LOD parameters");
      return;
    }
    celestial_lod_params_ = CelestialLodParameters.FromConfigNode(lod_node);
    Log.Info($"Loaded custom celestial LOD parameters: tier1={celestial_lod_params_.tier1_interval}s, " +
             $"tier2={celestial_lod_params_.tier2_interval}s, tier3={celestial_lod_params_.tier3_interval}s");
  }

  private bool IsPriorityCelestialBody(CelestialBody body) {
    if (body == null) {
      return false;
    }
    if (body.orbit == null || body.flightGlobalsIndex == 0) {
      return true;
    }
    CelestialBody current_main = FlightGlobals.currentMainBody;
    if (body == current_main) {
      return true;
    }
    if (current_main != null) {
      if (body == current_main.orbit?.referenceBody) {
        return true;
      }
      if (body.orbit?.referenceBody == current_main) {
        return true;
      }
    }
    if (body == FlightGlobals.ActiveVessel?.mainBody) {
      return true;
    }
    if (body == TargetVessel()?.mainBody) {
      return true;
    }
    if (body == FlightGlobals.fetch?.VesselTarget as CelestialBody) {
      return true;
    }
    if (MapView.MapIsEnabled && PlanetariumCamera.fetch?.target?.celestialBody == body) {
      return true;
    }
    return false;
  }

  private float GetCelestialUpdateInterval(CelestialBody body) {
    // 1. Observer / Camera Salience (Apparent Angular Size):
    // If the observer is focused or looking closely at this body (even if it's a 100m asteroid or comet),
    // dynamically upgrade its update frequency to prevent any visual stutter.
    if (camera_world_position_valid_) {
      var profile = GetOrComputeCelestialStaticProfile(body);
      Vector3d diff = body.position - cached_camera_world_position_;
      double dist_sq = diff.sqrMagnitude;
      if (dist_sq > 1.0) {
        double salience = profile.radius_squared / dist_sq;
        if (salience >= celestial_lod_params_.high_salience_threshold) {
          return celestial_lod_params_.tier1_interval;
        }
        if (salience >= celestial_lod_params_.medium_salience_threshold) {
          return celestial_lod_params_.tier2_interval;
        }
      }
    }

    // 2. Topological and System Proximity:
    CelestialBody sun = Planetarium.fetch?.Sun;
    CelestialBody ref_body = body.orbit?.referenceBody;
    if (ref_body == sun) {
      return celestial_lod_params_.tier1_interval;
    }
    CelestialBody camera_target = MapView.MapIsEnabled ? PlanetariumCamera.fetch?.target?.celestialBody : null;
    if (camera_target != null && ref_body == camera_target) {
      return celestial_lod_params_.tier1_interval;
    }
    CelestialBody active_main = FlightGlobals.ActiveVessel?.mainBody;
    if (active_main != null && (ref_body == active_main || ref_body == active_main.orbit?.referenceBody)) {
      return celestial_lod_params_.tier1_interval;
    }

    // 3. Scale-Invariant Classification:
    var static_profile = GetOrComputeCelestialStaticProfile(body);
    if (static_profile.is_major_body) {
      return celestial_lod_params_.tier2_interval;
    }

    // 4. Distant minor bodies, irregular moons, asteroids, comets, and KBOs:
    return celestial_lod_params_.tier3_interval;
  }

  private bool ShouldUpdateCelestialBody(CelestialBody body, float current_realtime, int current_frame) {
    int index = body.flightGlobalsIndex;
    if (index >= 0) {
      EnsureCelestialTrackingCapacity(index);
    }
    if (IsPriorityCelestialBody(body)) {
      if (index >= 0) {
        celestial_last_update_realtime_[index] = current_realtime;
        celestial_last_update_frame_[index] = current_frame;
      }
      return true;
    }
    if (index >= 0) {
      float interval = GetCelestialUpdateInterval(body);
      if (celestial_last_update_realtime_[index] == 0f) {
        // Low-discrepancy quasi-random stagger using golden ratio conjugate (Weyl sequence)
        // to uniformly disperse celestial update deadlines and eliminate frame spiking.
        const double golden_ratio_conjugate = 0.618033988749895;
        float initial_phase = (float)(((index * golden_ratio_conjugate) % 1.0) * interval);
        celestial_last_update_realtime_[index] = current_realtime - initial_phase;
      }
      if (current_realtime - celestial_last_update_realtime_[index] >= interval) {
        celestial_last_update_realtime_[index] = current_realtime;
        celestial_last_update_frame_[index] = current_frame;
        return true;
      }
      return false;
    }
    return true;
  }

  private void UpdateBody(CelestialBody body, double universal_time) {
    int body_index = body.flightGlobalsIndex;
    int parent_index = body.orbit.referenceBody.flightGlobalsIndex;
    if (!celestial_parent_cache_.TryGetValue(body_index, out int cached_parent) ||
        cached_parent != parent_index) {
      plugin_.UpdateCelestialHierarchy(body_index, parent_index);
      celestial_parent_cache_[body_index] = parent_index;
    }
    QP from_parent = plugin_.CelestialFromParent(body_index);
    Orbit original = body.orbit;
    if (scratch_orbit_ == null) {
      scratch_orbit_ = new Orbit(original.inclination,
                                 original.eccentricity,
                                 original.semiMajorAxis,
                                 original.LAN,
                                 original.argumentOfPeriapsis,
                                 original.meanAnomalyAtEpoch,
                                 original.epoch,
                                 original.referenceBody);
    }
    scratch_orbit_.inclination = original.inclination;
    scratch_orbit_.eccentricity = original.eccentricity;
    scratch_orbit_.semiMajorAxis = original.semiMajorAxis;
    scratch_orbit_.LAN = original.LAN;
    scratch_orbit_.argumentOfPeriapsis = original.argumentOfPeriapsis;
    scratch_orbit_.meanAnomalyAtEpoch = original.meanAnomalyAtEpoch;
    scratch_orbit_.epoch = original.epoch;
    scratch_orbit_.referenceBody = original.referenceBody;

    scratch_orbit_.UpdateFromStateVectors((Vector3d)from_parent.q,
                                          (Vector3d)from_parent.p,
                                          scratch_orbit_.referenceBody,
                                          universal_time);
    body.orbit.inclination = scratch_orbit_.inclination;
    body.orbit.eccentricity = scratch_orbit_.eccentricity;
    body.orbit.semiMajorAxis = scratch_orbit_.semiMajorAxis;
    body.orbit.LAN = scratch_orbit_.LAN;
    body.orbit.argumentOfPeriapsis = scratch_orbit_.argumentOfPeriapsis;
    body.orbit.meanAnomalyAtEpoch = scratch_orbit_.meanAnomalyAtEpoch;
    body.orbit.epoch = scratch_orbit_.epoch;
    body.orbit.referenceBody = scratch_orbit_.referenceBody;
    body.orbit.Init();
    body.orbit.UpdateFromUT(universal_time);
    Vector3d body_pos = (Vector3d)from_parent.q;
    Vector3d body_vel = (Vector3d)from_parent.p;
    body.orbit.UpdateFromStateVectors(body_pos,
                                      body_vel,
                                      scratch_orbit_.referenceBody,
                                      universal_time);
    body.CBUpdate();

    if (celestial_last_motion_.TryGetValue(body_index, out var last_motion) &&
        universal_time > last_motion.time_ut) {
      var slice = DensePolynomialSlice.FromEndpoints(
          last_motion.time_ut,
          universal_time,
          last_motion.position,
          last_motion.velocity,
          body_pos,
          body_vel,
          UnityEngine.QuaternionD.identity,
          UnityEngine.QuaternionD.identity);
      TripleBufferStateBus.PublishBodySlice(body_index, ref slice);
    }
    celestial_last_motion_[body_index] = new MotionSnapshot{
        time_ut = universal_time,
        position = body_pos,
        velocity = body_vel,
        rotation = UnityEngine.QuaternionD.identity,
        angular_velocity = Vector3d.zero
    };
  }

  public static bool IsVesselManeuvering(Vessel vessel) {
    if (vessel == null || vessel.packed) {
      return false;
    }
    if (vessel.ctrlState.mainThrottle > 1e-3f) {
      return true;
    }
    if (vessel.geeForce_immediate > 1e-3) {
      return true;
    }
    if (vessel.ActionGroups != null &&
        vessel.ActionGroups[KSPActionGroup.RCS] &&
        (Math.Abs(vessel.ctrlState.X) > 1e-3f ||
         Math.Abs(vessel.ctrlState.Y) > 1e-3f ||
         Math.Abs(vessel.ctrlState.Z) > 1e-3f)) {
      return true;
    }
    return false;
  }

  private void UpdatePredictions() {
    Vessel main_vessel = PredictedVessel();
    plotting_frame_selector_.active_vessel = main_vessel;

    if (MapView.MapIsEnabled) {
      string main_vessel_guid = GetVesselGuid(main_vessel);
      string target_guid = !plotting_frame_selector_.target_frame_selected
          ? TargetVesselGuid()
          : null;

      double current_realtime = UnityEngine.Time.realtimeSinceStartup;
      double current_ut = Planetarium.GetUniversalTime();
      bool is_maneuvering = IsVesselManeuvering(main_vessel);
      bool is_warping = TimeWarp.CurrentRate > 1.0f;

      // In MapView, numerical prediction integration takes ~1.5-2.5 ms per call in C++.
      // When maneuvering: refresh at 10 Hz (0.100s).
      // When coasting / on rails / warping: refresh at 1 Hz (1.000s) or if UT jumped > 10s.
      double interval = is_maneuvering ? 0.100 : 1.000;
      bool should_update =
          main_vessel_guid != last_prediction_integration_guid_ ||
          target_guid != last_prediction_integration_target_guid_ ||
          (current_realtime - last_prediction_integration_realtime_ > interval &&
           (is_maneuvering || !is_warping || Math.Abs(current_ut - last_prediction_integration_ut_) > 10.0));

      if (should_update) {
        last_prediction_integration_realtime_ = current_realtime;
        last_prediction_integration_ut_ = current_ut;
        last_prediction_integration_guid_ = main_vessel_guid;
        last_prediction_integration_target_guid_ = target_guid;

        if (target_guid != null) {
          if (main_vessel_guid == null) {
            plugin_.UpdatePrediction(new string[]{ target_guid });
            MapMarkerEventGovernor.InvalidateTarget();
          } else {
            // TODO(phl): It's not nice that we are overriding the target vessel
            // parameters.
            AdaptiveStepParameters adaptive_step_parameters =
                plugin_.VesselGetPredictionAdaptiveStepParameters(
                    main_vessel_guid);
            plugin_.VesselSetPredictionAdaptiveStepParameters(
                target_guid,
                adaptive_step_parameters);
            plugin_.UpdatePrediction(
                new string[]{ main_vessel_guid, target_guid });
            MapMarkerEventGovernor.InvalidatePrediction();
            MapMarkerEventGovernor.InvalidateTarget();
            plotter_?.InvalidatePrediction();
          }
        } else if (main_vessel_guid != null) {
          plugin_.UpdatePrediction(new string[]{ main_vessel_guid });
          MapMarkerEventGovernor.InvalidatePrediction();
          plotter_?.InvalidatePrediction();
        }
      }
    }
  }

  private void UpdateVessel(Vessel vessel, double universal_time) {
    string vessel_guid = GetVesselGuid(vessel);
    if (plugin_.HasVessel(vessel_guid)) {
      QP from_parent = plugin_.VesselFromParent(
          vessel.mainBody.flightGlobalsIndex,
          vessel_guid);
      vessel.orbit.UpdateFromStateVectors(pos : (Vector3d)from_parent.q,
                                          vel : (Vector3d)from_parent.p,
                                          refBody : vessel.orbit.referenceBody,
                                          UT : universal_time);
      if (HeterogeneousDynamicGovernor.EnableAutonomousPhysicsDecoupling) {
        NativeStateSnapshotChannel.PublishSnapshot(
            vessel_guid,
            new MotionSnapshot{
                time_ut = universal_time,
                position = (Vector3d)from_parent.q,
                velocity = (Vector3d)from_parent.p,
                rotation = vessel.transform.rotation,
                angular_velocity = Vector3d.zero
            });
      }
      if (vessel.loaded) {
        Part root_part = vessel.rootPart;
        UnityEngine.Rigidbody root_part_rb = root_part.rb;
        var origin = new Origin{
            reference_part_is_at_origin = true,
            reference_part_is_unmoving = true,
            main_body_centre_in_world =
                (XYZ)FlightGlobals.ActiveVessel.mainBody.position,
            reference_part_id = root_part.flightID
        };
        var v_parts = vessel.parts;
        for (int pi = 0; pi < v_parts.Count; ++pi) {
          var part = v_parts[pi];
          if (part.rb == null || !plugin_.PartIsTruthful(part.flightID)) {
            continue;
          }
          UnityEngine.Rigidbody part_rb = part.rb;
          // TODO(egg): What if the plugin doesn't have the part? this seems
          // brittle.
          // NOTE(egg): I am not sure what the origin is here, as we are
          // running before the floating origin and krakensbane.  Do everything
          // with respect to the root part, since the overall linear motion of
          // the vessel is handled with by the orbit anyway.
          // TODO(egg): check that the vessel is moved *after* this.  Shouldn't
          // we be calling vessel.orbitDriver.updateFromParameters() after
          // setting the orbit anyway?
          QPRW part_actual_motion =
              plugin_.PartGetActualRigidMotion(part.flightID, origin);
          QP part_actual_degrees_of_freedom = part_actual_motion.qp;
          var part_position = (Vector3d)part_actual_degrees_of_freedom.q;
          var part_velocity = (Vector3d)part_actual_degrees_of_freedom.p;
          var part_rotation = (UnityEngine.QuaternionD)part_actual_motion.r;
          var part_angular_velocity = (Vector3d)part_actual_motion.w;
          part_rb.position = root_part_rb.position + part_position;
          part_rb.rotation = part_rotation;
          part_rb.transform.SetPositionAndRotation(
              root_part_rb.position + part_position,
              part_rotation);
          part_rb.velocity = root_part_rb.velocity + part_velocity;
          part_rb.angularVelocity = part_angular_velocity;
        }
      }
    }
  }

  private bool time_is_advancing(double universal_time) {
    double plugin_time = plugin_.CurrentTime();
    if (plugin_time > universal_time) {
      // TODO(egg): Make this resistant to bad floating points up to 2ULPs,
      // and make it fatal again.
      Log.Error("Closed Timelike Curve: " + plugin_time + " > " +
                universal_time +
                " plugin-universal=" + (plugin_time - universal_time));
      return false;
    } else if (plugin_time == universal_time) {
      return false;
    }
    return true;
  }

  private Part closest_physical_parent(Part part) {
    return part.rb ? part : closest_physical_parent(part.parent);
  }

  // Whether the given kerbal is clambering, i.e., climbing something that's not
  // a ladder.
  private bool is_clambering(KerbalEVA kerbal) {
    return kerbal.fsm.CurrentState == kerbal.st_clamber_acquireP1 ||
           kerbal.fsm.CurrentState == kerbal.st_clamber_acquireP2 ||
           kerbal.fsm.CurrentState == kerbal.st_clamber_acquireP3;
  }

  // Æthelred Kerman.
  private bool is_unready_kerbal(Vessel vessel) {
    return vessel.isEVA && vessel.evaController?.Ready == false;
  }

  private bool is_parachuting_kerbal(Vessel vessel) {
    return vessel.isEVA && vessel.evaController.IsChuteState;
  }

  private bool is_manageable(Vessel vessel) {
    if (vessel.state == Vessel.State.DEAD) {
      return false;
    }
    if (vessel.id == Guid.Empty) {
      return false;
    }
    if (!(vessel.situation == Vessel.Situations.SUB_ORBITAL ||
          vessel.situation == Vessel.Situations.ORBITING ||
          vessel.situation == Vessel.Situations.ESCAPING ||
          vessel.situation == Vessel.Situations.FLYING)) {
      return false;
    }
    if (!vessel.packed &&
        JustAboveTheGround(vessel,
                           out double height,
                           out double vertical_speed)) {
      return false;
    }
    if (is_unready_kerbal(vessel)) {
      return false;
    }
    if (vessel.loaded && hack_gravity?.toggle.isOn == true) {
      return false;
    }
    if (!vessel.packed) {
      bool any_faithful = false;
      var parts = vessel.parts;
      for (int i = 0; i < parts.Count; ++i) {
        if (PartIsFaithful(parts[i])) {
          any_faithful = true;
          break;
        }
      }
      if (!any_faithful) {
        return false;
      }
    }
    if (vessel.orbit.pos == Vector3d.zero) {
      return false;
    }
    return true;
  }

  public static bool is_lit_solid_booster(Part part) {
    var modules = part.Modules;
    for (int mi = 0; mi < modules.Count; ++mi) {
      if (modules[mi] is ModuleEngines module_engines &&
          module_engines.EngineIgnited &&
          module_engines.engineType == EngineType.SolidBooster) {
        return true;
      }
    }
    return false;
  }

  private string UnmanageabilityReasons(Vessel vessel) {
    List<string> reasons = new List<string>(capacity : 3);
    if (vessel.state == Vessel.State.DEAD) {
      reasons.Add("vessel is dead");
    }
    if (vessel.id == Guid.Empty) {
      reasons.Add("vessel has an empty GUID");
    }
    if (!(vessel.situation == Vessel.Situations.SUB_ORBITAL ||
          vessel.situation == Vessel.Situations.ORBITING ||
          vessel.situation == Vessel.Situations.ESCAPING ||
          vessel.situation == Vessel.Situations.FLYING)) {
      reasons.Add("vessel situation is " + vessel.situation);
    }
    if (!vessel.packed &&
        JustAboveTheGround(vessel,
                           out double height,
                           out double vertical_speed)) {
      reasons.Add("vessel is " +
                  height + " m above ground with a vertical speed of " +
                  vertical_speed + " m/s");
    }
    if (is_unready_kerbal(vessel)) {
      reasons.Add("vessel is an unready Kerbal");
    }
    if (vessel.loaded && hack_gravity?.toggle.isOn == true) {
      reasons.Add("vessel is loaded and gravity is being hacked");
    }
    if (!vessel.packed &&
        vessel.parts.All(part => !PartIsFaithful(part))) {
      reasons.Add("vessel is completely unfaithful");
    }
    if (vessel.orbit.pos == Vector3d.zero) {
      reasons.Add("vessel is at the centre of the star");
    }
    if (reasons.Count == 0) {
      return null;
    } else {
      return string.Join(", ", reasons.ToArray());
    }
  }

  private bool is_manageable_on_rails(Vessel vessel) {
    return vessel.packed && is_manageable(vessel);
  }

  private bool has_active_manageable_vessel() {
    Vessel active_vessel = FlightGlobals.ActiveVessel;
    return active_vessel != null && is_manageable(active_vessel);
  }

  private static bool JustAboveTheGround(Vessel vessel,
                                         out double height,
                                         out double vertical_speed) {
    height = vessel.altitude - vessel.terrainAltitude;
    vertical_speed = vessel.verticalSpeed;
    double Δt = Planetarium.TimeScale * Planetarium.fetch.fixedDeltaTime;
    return height + vertical_speed * Δt < 0;
  }

  private static bool IsNaN(Vector3d v) {
    return double.IsNaN(v.x + v.y + v.z);
  }

  private static bool IsNaN(XYZ xyz) {
    return double.IsNaN(xyz.x + xyz.y + xyz.z);
  }

  // It seems that parts sometimes have NaN position, velocity or angular
  // velocity, presumably because they are being destroyed.  Just skip these
  // unfaithful parts as if they had no rigid body.
  private static bool PartIsFaithful(Part part) {
    return part.rb != null &&
           !IsNaN(part.rb.position) &&
           !IsNaN(part.rb.velocity) &&
           !IsNaN(part.rb.angularVelocity);
  }

  private void OverrideRSASTarget(FlightCtrlState state) {
    if (override_rsas_target_ && FlightGlobals.ActiveVessel.Autopilot.Enabled) {
      FlightGlobals.ActiveVessel.Autopilot.SAS.SetTargetOrientation(
          rsas_target_,
          reset_rsas_target_);
      FlightGlobals.ActiveVessel.Autopilot.SAS.ConnectFlyByWire(
          reset_rsas_target_);
    }
    reset_rsas_target_ = false;
  }

  // Returns false and nulls `texture` if the file does not exist.
  public static bool LoadTextureIfExists(out UnityEngine.Texture texture,
                                         string path) {
    string full_path =
        KSPUtil.ApplicationRootPath + Path.DirectorySeparatorChar +
        "GameData" + Path.DirectorySeparatorChar +
        "Principia" + Path.DirectorySeparatorChar +
        "assets" + Path.DirectorySeparatorChar +
        path;
    if (File.Exists(full_path)) {
      var texture2d = new UnityEngine.Texture2D(2, 2);
      bool success = UnityEngine.ImageConversion.LoadImage(
          texture2d,
          File.ReadAllBytes(full_path));
      if (!success) {
        Log.Fatal("Failed to load texture " + full_path);
      }
      texture = texture2d;
      return true;
    } else {
      texture = null;
      return false;
    }
  }

  public static void LoadTextureOrDie(out UnityEngine.Texture texture,
                                      string path) {
    bool success = LoadTextureIfExists(out texture, path);
    if (!success) {
      Log.Fatal("Missing texture " + path);
    }
  }

  public static double RadiusAt(CelestialBody centre,
                                double latitude,
                                double longitude) {
    double altitude = 0;
    try {
      altitude = centre.TerrainAltitude(latitude,
                                        longitude,
                                        allowNegative: !centre.ocean);
    } catch (Exception e) {
      Log.Fatal("Terrain system raised exception " + e.ToString() +
                " when computing altitude at latitude " +
                latitude +
                ", longitude " +
                longitude +
                " for celestial " +
                centre.name +
                "; you are probably using a mod incompatible with Principia");
    }
    if (double.IsNaN(altitude)) {
      Log.Fatal("Terrain system returned NaN for altitude at latitude " +
                latitude +
                ", longitude " +
                longitude +
                " for celestial " +
                centre.name +
                "; you are probably using a mod incompatible with Principia");
    }
    if (double.IsNaN(centre.Radius)) {
      Log.Fatal("Radius is NaN for celestial " + centre.name);
    }
    return altitude + centre.Radius;
  }

  [MethodImpl(MethodImplOptions.NoOptimization)]
  public static void ValidateCelestialTerrain(CelestialBody centre,
                                              Random random) {
    for (int i = 0; i < 5; ++i) {
      double latitude = random.NextDouble() * 360.0 - 180.0;
      double longitude = random.NextDouble() * 360.0 - 180.0;
      RadiusAt(centre, latitude: latitude, longitude: longitude);
    }
  }

  #region ScenarioModule lifecycle

  // These functions override virtual ones from `ScenarioModule`, but it seems
  // that they're actually called by reflection, so that bad things happen
  // if you don't have, e.g., a function called `OnAwake()` that calls
  // `base.OnAwake()`.  It doesn't matter whether the functions are public or
  // private, overriding or hiding though.

  public override void OnAwake() {
    base.OnAwake();
    if (is_bad_installation_ || in_main_menu_) {
      return;
    }
    // While we're here, we might as well log.
    Log.Info("principia.ksp_plugin_adapter.PrincipiaPluginAdapter.OnAwake()");
    PrincipiaGPGPUContext.Initialize();
    VacuumBypassGovernor.Initialize();

    LoadTextureIfExists(out compass_navball_texture_, "navball_compass.png");
    LoadTextureOrDie(out inertial_navball_texture_, "navball_inertial.png");
    LoadTextureOrDie(out barycentric_navball_texture_,
                     "navball_barycentric.png");
    LoadTextureOrDie(out body_direction_navball_texture_,
                     "navball_body_direction.png");
    LoadTextureOrDie(out surface_navball_texture_, "navball_surface.png");
    LoadTextureOrDie(out target_navball_texture_, "navball_target.png");

    ConfigNode draw_styles =
        GameDatabase.Instance.GetAtMostOneNode(
            principia_draw_styles_config_name);
    LoadDrawStyles(draw_styles);

    ConfigNode celestial_lod =
        GameDatabase.Instance.GetAtMostOneNode(
            principia_celestial_lod_config_name);
    LoadCelestialLodParameters(celestial_lod);

    if (unmodified_orbits_ == null) {
      unmodified_orbits_ = new Dictionary<CelestialBody, Orbit>();
      unmodified_initial_rotations_ = new Dictionary<CelestialBody, double>();
      foreach (CelestialBody celestial in FlightGlobals.Bodies) {
        unmodified_initial_rotations_.Add(celestial, celestial.initialRotation);
        if (celestial.orbit != null) {
          unmodified_orbits_.Add(
              celestial,
              new Orbit(inc   : celestial.orbit.inclination,
                        e     : celestial.orbit.eccentricity,
                        sma   : celestial.orbit.semiMajorAxis,
                        lan   : celestial.orbit.LAN,
                        argPe : celestial.orbit.argumentOfPeriapsis,
                        mEp   : celestial.orbit.meanAnomalyAtEpoch,
                        t     : celestial.orbit.epoch,
                        body  : celestial.orbit.referenceBody));
        }
      }
    }

    event_void_holder_.Add(GameEvents.onShowUI,
                           () => { hide_all_gui_ = false; });
    event_void_holder_.Add(GameEvents.onHideUI,
                           () => { hide_all_gui_ = true; });
    event_void_holder_.Add(GameEvents.onGUIAdministrationFacilitySpawn,
                           () => { in_principia_scene_ = false; });
    event_void_holder_.Add(GameEvents.onGUIAdministrationFacilityDespawn,
                           () => { in_principia_scene_ = true; });
    event_void_holder_.Add(GameEvents.onGUIAstronautComplexSpawn,
                           () => { in_principia_scene_ = false; });
    event_void_holder_.Add(GameEvents.onGUIAstronautComplexDespawn,
                           () => { in_principia_scene_ = true; });
    event_void_holder_.Add(GameEvents.onGUIMissionControlSpawn,
                           () => { in_principia_scene_ = false; });
    event_void_holder_.Add(GameEvents.onGUIMissionControlDespawn,
                           () => { in_principia_scene_ = true; });
    event_void_holder_.Add(GameEvents.onGUIRnDComplexSpawn,
                           () => { in_principia_scene_ = false; });
    event_void_holder_.Add(GameEvents.onGUIRnDComplexDespawn,
                           () => { in_principia_scene_ = true; });
    GameEvents.onPartDie.Add(OnPartDie);
    GameEvents.onTimeWarpRateChanged.Add(OnTimeWarpRateChanged);
    GameEvents.onVesselChange.Add(OnVesselChange);
    GameEvents.onVesselSOIChanged.Add(OnVesselSOIChanged);

    // Timing0, -8008 on the script execution order page.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly,
                                 ObscenelyEarly);
    // TimingPre, -101 on the script execution order page.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Precalc, Precalc);
    // Timing1, -99 on the script execution order page.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Early, Early);
    // Timing2, -1 on the script execution order page.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Earlyish, Earlyish);
    // Timing3, 7.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.FashionablyLate,
                                 FashionablyLate);
    // TimingFI, 9.  Note that we cannot call the callback FlightIntegrator as
    // that would collide with the type.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.FlightIntegrator,
                                 JaiFailliAttendre);
    // Timing4, 19.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Late, Late);
    // Custom timing, 301.
    planetarium_camera_adjuster_ =
        gameObject.AddComponent<PlanetariumCameraAdjuster>();
    planetarium_camera_adjuster_.adapter = this;
    // Timing5, 8008.
    TimingManager.FixedUpdateAdd(TimingManager.TimingStage.BetterLateThanNever,
                                 BetterLateThanNever);
    TimingManager.LateUpdateAdd(TimingManager.TimingStage.BetterLateThanNever,
                                BetterLateThanNeverLateUpdate);
  }

  public override void OnSave(ConfigNode node) {
    base.OnSave(node);
    if (plugin_reader_ != IntPtr.Zero) {
      throw new Exception(
          "Attempted to save while plugin reader is still running; " +
          "throwing exception to cancel the save and avoid save corruption.");
    }
    if (PluginRunning()) {
      try {
        string sidecar_file = SidecarStorage.SaveSidecar(
            plugin_,
            serialization_compression_,
            out int chunks,
            out long total_bytes);
        node.AddValue(principia_sidecar_file, sidecar_file);
        node.AddValue(principia_sidecar_version, (int)principia_sidecar_current_version);
        node.AddValue(principia_sidecar_timestamp, DateTime.UtcNow.ToString("o"));
        node.AddValue(principia_sidecar_chunks, chunks);
        Log.Info($"Principia state saved to sidecar {sidecar_file} ({chunks} chunks, {total_bytes} bytes)");
        return;
      } catch (Exception e) {
        Log.Error("Failed to save Principia sidecar, falling back to legacy SFS embedding: " + e);
      }

      IntPtr serializer = IntPtr.Zero;
      int chunks_legacy = 0;
      for (;;) {
        string serialization = plugin_.SerializePlugin(
            ref serializer,
            serialization_compression_,
            serialization_encoding_);
        if (serialization == null) {
          break;
        }
        node.AddValue(principia_serialized_plugin, serialization);
        ++chunks_legacy;
      }
      Log.Info("Legacy serialization has " + chunks_legacy + " chunks");
    }
  }

  public override void OnLoad(ConfigNode node) {
    base.OnLoad(node);
    if (is_bad_installation_ || in_main_menu_) {
      return;
    }
    if (node.HasValue(principia_sidecar_file)) {
      Cleanup();
      RemoveBuggyTidalLocking();

      string sidecar_filename = node.GetValue(principia_sidecar_file);
      IntPtr deserializer = IntPtr.Zero;
      bool loaded = SidecarStorage.LoadSidecar(
          sidecar_filename,
          ref deserializer,
          ref plugin_reader_,
          serialization_compression_);
      if (!loaded && node.HasValue(principia_serialized_plugin)) {
        Log.Warning("Sidecar file missing, falling back to embedded SFS chunks");
        string[] serializations = node.GetValues(principia_serialized_plugin);
        foreach (string serialization in serializations) {
          Interface.DeserializePlugin(serialization,
                                      ref deserializer,
                                      ref plugin_reader_,
                                      serialization_compression_,
                                      serialization_encoding_);
        }
        Interface.DeserializePlugin("",
                                    ref deserializer,
                                    ref plugin_reader_,
                                    serialization_compression_,
                                    serialization_encoding_);
        loaded = true;
      }
      if (loaded) {
        FinishPluginLoad();
      } else {
        Log.Fatal("Could not load Principia state from sidecar: " + sidecar_filename);
      }
    } else if (node.HasValue(principia_serialized_plugin)) {
      Cleanup();
      RemoveBuggyTidalLocking();

      IntPtr deserializer = IntPtr.Zero;
      string[] serializations = node.GetValues(principia_serialized_plugin);
      Log.Info("Serialization has " + serializations.Length + " chunks");
      foreach (string serialization in serializations) {
        Interface.DeserializePlugin(serialization,
                                    ref deserializer,
                                    ref plugin_reader_,
                                    serialization_compression_,
                                    serialization_encoding_);
      }
      Interface.DeserializePlugin("",
                                  ref deserializer,
                                  ref plugin_reader_,
                                  serialization_compression_,
                                  serialization_encoding_);
      FinishPluginLoad();
    } else {
      Log.Warning("No principia state found, creating one");
      ResetPlugin();
    }

    // Check that the user didn't mess with the solar system.
    var ksp_celestial_names = new SortedSet<string>();
    var ksp_not_in_principia = new SortedSet<string>();
    foreach (CelestialBody celestial in FlightGlobals.Bodies) {
      ksp_celestial_names.Add(celestial.name);
      ksp_not_in_principia.Add(celestial.name);
    }
    using (DisposableIterator all_celestial_names =
           plugin_.CelestialGetAllNames()) {
      var principia_celestial_names = new SortedSet<string>();
      var principia_not_in_ksp = new SortedSet<string>();
      for (;
           !all_celestial_names.IteratorAtEnd();
           all_celestial_names.IteratorIncrement()) {
        string celestial_name = all_celestial_names.IteratorGetCelestialName();
        principia_celestial_names.Add(celestial_name);
        principia_not_in_ksp.Add(celestial_name);
      }
      ksp_not_in_principia.ExceptWith(principia_celestial_names);
      if (ksp_not_in_principia.Count > 0) {
        Log.Error("Principia does not know about the following KSP celestial " +
                  "bodies: " +
                  string.Join(", ", ksp_not_in_principia.ToArray()));
      }
      principia_not_in_ksp.ExceptWith(ksp_celestial_names);
      if (principia_not_in_ksp.Count > 0) {
        Log.Error("KSP does not know about the following Principia celestial " +
                  "bodies: " +
                  string.Join(", ", principia_not_in_ksp.ToArray()));
      }
      if (ksp_not_in_principia.Count > 0 || principia_not_in_ksp.Count > 0) {
        Log.Fatal("KSP and Principia disagree on the solar system; " +
                  "you have probably changed the solar system since creating " +
                  "the Principia save.");
      }
    }
  }

  private void FinishPluginLoad() {
    if (plugin_reader_.PluginReaderWillBeSlow()) {
      migration_monitor_ = new MigrationMonitor(plugin_reader_);
      FlightAutoSave.fetch.bypassAutoSave = true;
      KeepPaused();
    } else {
      plugin_ = Interface.PluginReaderAwait(ref plugin_reader_);
    }
    if (serialization_compression_ == "") {
      serialization_compression_ = "gipfeli";
    }
    if (serialization_encoding_ == "hexadecimal") {
      serialization_encoding_ = "base64";
    }

    previous_display_mode_ = null;
    must_set_plotting_frame_ = true;
    if (plugin_ != IntPtr.Zero && rolling_ephemeris_driver_ == null) {
      rolling_ephemeris_driver_ = new RollingEphemerisDriver(plugin_, this);
    }
    PrincipiaGPGPUContext.EnsureInitialized();
  }

  #endregion

  #region Unity Lifecycle

  // See the Unity manual on execution order for more information.
  // http://docs.unity3d.com/Manual/ExecutionOrder.html

  private void OnGUI() {
    if (is_bad_installation_ || in_main_menu_) {
      bad_installation_dialog_.RenderWindow();
      return;
    }
    if (plugin_reader_ != IntPtr.Zero) {
      if (UnityEngine.Event.current.type == UnityEngine.EventType.Layout) {
        plugin_ = Interface.PluginReaderGet(ref plugin_reader_);
        if (plugin_ == IntPtr.Zero) {
          KeepPaused();
        } else {
          FlightDriver.SetPause(pauseState: false);
          migration_monitor_.Hide();
          migration_monitor_ = null;
          FlightAutoSave.fetch.bypassAutoSave = false;
          LockClearing?.Invoke();
          if (rolling_ephemeris_driver_ == null) {
            rolling_ephemeris_driver_ = new RollingEphemerisDriver(plugin_, this);
          }
        }
      }
    }
    if (migration_monitor_ != null) {
      migration_monitor_.Show();
      migration_monitor_.RenderWindow();
      return;
    }

    apocalypse_dialog_.RenderWindow();
    PerformanceProbe.DrawProfilingHud();

    if (KSP.UI.Screens.ApplicationLauncher.Ready && toolbar_button_ == null) {
      LoadTextureOrDie(out UnityEngine.Texture toolbar_button_texture,
                       "toolbar_button.png");
      toolbar_button_ =
          KSP.UI.Screens.ApplicationLauncher.Instance.AddModApplication(
              onTrue          : () => main_window_.Show(),
              onFalse         : () => main_window_.Hide(),
              onHover         : null,
              onHoverOut      : null,
              onEnable        : null,
              onDisable       : null,
              visibleInScenes : KSP.UI.Screens.ApplicationLauncher.AppScenes.
                  ALWAYS,
              texture         : toolbar_button_texture);
    }
    // Make sure the state of the toolbar button remains consistent with the
    // state of the window.
    if (main_window_.Shown()) {
      toolbar_button_?.SetTrue(makeCall : false);
    } else {
      toolbar_button_?.SetFalse(makeCall : false);
    }

    if (hide_all_gui_ || !in_principia_scene_) {
      LockClearing?.Invoke();
    } else if (WindowsRendering.GetInvocationList().Length > 0) {
      WindowsRendering();
    } else {
      // There are no windows to render.
      LockClearing?.Invoke();
    }
  }

  private void LateUpdate() {
    using (PerformanceProbe.Measure(ProbeCategory.UnityLateUpdate)) {
      if (!PluginRunning()) {
        return;
      }
      string main_vessel_guid = GetVesselGuid(PredictedVessel());
      RenderTrajectoryCollisions(main_vessel_guid,
                                 out prediction_collision_,
                                 out flight_plan_collision_);

      if (map_renderer_ == null) {
        map_renderer_ = PlanetariumCamera.Camera.gameObject.
            AddComponent<RenderingActions>();
        map_renderer_.pre_cull = () => {
          using (PerformanceProbe.Measure(ProbeCategory.TrajectoryPlotting)) {
            RenderTrajectories();
          }
          RenderManœuvreMarkers();
        };
      }

      if (galaxy_cube_rotator_ == null) {
        galaxy_cube_rotator_ = ScaledCamera.Instance.galaxyCamera.gameObject.
            AddComponent<RenderingActions>();
        galaxy_cube_rotator_.pre_cull = RotateGalaxyCube;
      }

      // Handle clicks on planets.
      if (MapView.MapIsEnabled) {
        HandleMapViewClicks();
      }

      RenderNavballAndAccessories();

      if (MapView.MapIsEnabled) {
        XYZ sun_world_position = (XYZ)Planetarium.fetch.Sun.position;
        string target_id = FlightGlobals.ActiveVessel != null &&
                           !plotting_frame_selector_.target_frame_selected
                               ? TargetVesselGuid()
                               : null;

        if (main_vessel_guid != last_markers_vessel_guid_) {
          last_markers_vessel_guid_ = main_vessel_guid;
          MapMarkerEventGovernor.InvalidatePrediction();
          MapMarkerEventGovernor.InvalidateFlightPlan();
        }
        if (target_id != last_markers_target_guid_) {
          last_markers_target_guid_ = target_id;
          MapMarkerEventGovernor.InvalidateTarget();
        }

        // Event & Dynamics Driven marker evaluation:
        // When coasting / idle, HasDirtyMarkers is false -> exactly ZERO root-finding passes, 0.000 ms overhead.
        // When dynamics change or prediction integrates, evaluate at most ONE dirty category per frame.
        if (MapMarkerEventGovernor.HasDirtyMarkers) {
          if (MapMarkerEventGovernor.PredictionApsidesDirty) {
            RenderPredictionApsides(main_vessel_guid,
                                    prediction_collision_,
                                    sun_world_position);
            MapMarkerEventGovernor.PredictionApsidesDirty = false;
          } else if (MapMarkerEventGovernor.PredictionNodesDirty) {
            RenderPredictionNodes(main_vessel_guid,
                                  prediction_collision_,
                                  sun_world_position);
            MapMarkerEventGovernor.PredictionNodesDirty = false;
          } else if (MapMarkerEventGovernor.FlightPlanApsidesDirty) {
            if (main_vessel_guid != null &&
                plugin_.FlightPlanExists(main_vessel_guid) &&
                !RollingEphemerisDriver.ShouldSuppressPlot(main_vessel_guid)) {
              RenderFlightPlanApsides(main_vessel_guid,
                                      flight_plan_collision_,
                                      sun_world_position);
            }
            MapMarkerEventGovernor.FlightPlanApsidesDirty = false;
          } else if (MapMarkerEventGovernor.FlightPlanNodesDirty) {
            if (main_vessel_guid != null &&
                plugin_.FlightPlanExists(main_vessel_guid) &&
                !RollingEphemerisDriver.ShouldSuppressPlot(main_vessel_guid)) {
              RenderFlightPlanNodes(main_vessel_guid,
                                    flight_plan_collision_,
                                    sun_world_position);
            }
            MapMarkerEventGovernor.FlightPlanNodesDirty = false;
          } else if (MapMarkerEventGovernor.TargetApsidesDirty) {
            if (target_id != null && !plotting_frame_selector_.target_frame_selected) {
              RenderPredictionApsides(target_id, null, sun_world_position);
            }
            MapMarkerEventGovernor.TargetApsidesDirty = false;
          } else if (MapMarkerEventGovernor.TargetNodesDirty) {
            if (target_id != null && !plotting_frame_selector_.target_frame_selected) {
              RenderPredictionNodes(target_id, null, sun_world_position);
            }
            MapMarkerEventGovernor.TargetNodesDirty = false;
          }
        }
      }
      float frame_ms = UnityEngine.Time.unscaledDeltaTime * 1000.0f;
      HeterogeneousDynamicGovernor.UpdateFrameMetrics(
          render_time_ms: frame_ms,
          compute_time_ms: GpuFlightPlanOptimizer.LastOptimizationDurationMs);
    }
    PerformanceProbe.UpdateFrameMetrics();
  }

  private void RenderNavballAndAccessories() {
    override_rsas_target_ = false;
    Vessel active_vessel = FlightGlobals.ActiveVessel;
    if (active_vessel != null) {
      RenderNavball(active_vessel);
      if (!PluginRunning()) {
        return;
      }
      string active_vessel_guid = GetVesselGuid(active_vessel);

      // Design for compatibility with FAR: if we are in surface mode in an
      // atmosphere, FAR gives options to display the IAS, EAS, and Mach number,
      // in any of a number of units.  In that case, we must force the reference
      // frame to be that of the surface *of the relevant body*, in order to be
      // consistent with the speed display.
      bool ferram_owns_the_speed_display =
          FlightGlobals.speedDisplayMode ==
          FlightGlobals.SpeedDisplayModes.Surface &&
          active_vessel.atmDensity > 0;
      if (ferram_owns_the_speed_display) {
        plotting_frame_selector_.SetToSurfaceFrameOf(active_vessel.mainBody);
      }

      if (!plotting_frame_selector_.target_frame_selected &&
          FlightGlobals.speedDisplayMode ==
          FlightGlobals.SpeedDisplayModes.Target) {
        KSP.UI.Screens.Flight.SpeedDisplay.Instance.textTitle.text =
            L10N.CacheFormat("#Principia_SpeedDisplayModeTarget");
      }

      if (FlightGlobals.speedDisplayMode ==
          FlightGlobals.SpeedDisplayModes.Orbit ||
          FlightGlobals.speedDisplayMode ==
          FlightGlobals.SpeedDisplayModes.Surface ||
          plotting_frame_selector_.target_frame_selected) {
        bool plugin_has_active_manageable_vessel =
            has_active_manageable_vessel() &&
            plugin_.HasVessel(active_vessel_guid);

        KSP.UI.Screens.Flight.SpeedDisplay speed_display =
            KSP.UI.Screens.Flight.SpeedDisplay.Instance;
        if (speed_display?.textTitle != null &&
            speed_display?.textSpeed != null &&
            !ferram_owns_the_speed_display) {
          speed_display.textTitle.text = plotting_frame_selector_.NavballName();
          var active_vessel_velocity = plugin_has_active_manageable_vessel
                                           ? (Vector3d)plugin_.VesselVelocity(
                                               active_vessel_guid)
                                           : (Vector3d)plugin_.
                                               UnmanageableVesselVelocity(
                                                   new QP{
                                                       q = (XYZ)active_vessel.
                                                           orbit.pos,
                                                       p = (XYZ)active_vessel.
                                                           orbit.vel
                                                   },
                                                   active_vessel.orbit.
                                                       referenceBody.
                                                       flightGlobalsIndex);
          speed_display.textSpeed.text = L10N.CacheFormat(
              "#Principia_SpeedDisplayText",
              active_vessel_velocity.magnitude.ToString("F1"));
        }

        if (!plugin_has_active_manageable_vessel) {
          // TODO(egg): orient the Frenet trihedron even in the case where the
          // active vessel is unmanageable, similarly to the speed display
          // above.
          return;
        }

        // Orient the Frenet trihedron.
        var prograde = (Vector3d)plugin_.VesselTangent(active_vessel_guid);
        var radial = (Vector3d)plugin_.VesselNormal(active_vessel_guid);
        // Yes, the astrodynamicist's normal is the mathematician's binormal.
        // Don't ask.
        var normal = (Vector3d)plugin_.VesselBinormal(active_vessel_guid);

        SetNavballVector(navball_.progradeVector, prograde);
        SetNavballVector(navball_.radialInVector, radial);
        SetNavballVector(navball_.normalVector, normal);
        SetNavballVector(navball_.retrogradeVector, -prograde);
        SetNavballVector(navball_.radialOutVector, -radial);
        SetNavballVector(navball_.antiNormalVector, -normal);

        // Make the autopilot target our Frenet trihedron.
        if (active_vessel != last_rsas_vessel_) {
          last_rsas_vessel_ = active_vessel;
          active_vessel.OnPreAutopilotUpdate -= OverrideRSASTarget;
          active_vessel.OnPreAutopilotUpdate += OverrideRSASTarget;
        }
        if (active_vessel.Autopilot.Enabled) {
          override_rsas_target_ = true;
          switch (active_vessel.Autopilot.Mode) {
            case VesselAutopilot.AutopilotMode.Prograde:
              rsas_target_ = prograde;
              break;
            case VesselAutopilot.AutopilotMode.Retrograde:
              rsas_target_ = -prograde;
              break;
            // NOTE(egg): For reasons that are unlikely to become clear again,
            // the button labeled with the radial in icon sets the autopilot
            // mode to `RadialOut`, and vice-versa.  As a result, we must set
            // the target to the outwards radial (negative normal) vector if the
            // mode is `RadialIn`.  Contrast with the navball vectors above,
            // which do not exhibit this inconsistency (thus where
            // `radialInVector` is set to `radial`).
            case VesselAutopilot.AutopilotMode.RadialIn:
              rsas_target_ = -radial;
              break;
            case VesselAutopilot.AutopilotMode.RadialOut:
              rsas_target_ = radial;
              break;
            case VesselAutopilot.AutopilotMode.Normal:
              rsas_target_ = normal;
              break;
            case VesselAutopilot.AutopilotMode.Antinormal:
              rsas_target_ = -normal;
              break;
            default:
              override_rsas_target_ = false;
              break;
          }
        }
      }
    }
  }

  private void FixedUpdate() {
    using (PerformanceProbe.Measure(ProbeCategory.UnityFixedUpdate)) {
      if (is_bad_installation_ || in_main_menu_) {
        return;
      }
      if (GameSettings.ORBIT_WARP_DOWN_AT_SOI) {
        Log.Info("Setting GameSettings.ORBIT_WARP_DOWN_AT_SOI to false");
        GameSettings.ORBIT_WARP_DOWN_AT_SOI = false;
      }
      if (must_set_plotting_frame_) {
        must_set_plotting_frame_ = false;
        plotting_frame_selector_.UpdateMainBody();
        previous_display_mode_ = null;
      }

      parachuting_kerbal_angular_velocities_.Clear();

      if (PluginRunning()) {
        plugin_.SetMainBody(
            (FlightGlobals.currentMainBody ?? FlightGlobals.GetHomeBody()).
            flightGlobalsIndex);

        var para_vessels = FlightGlobals.Vessels;
        for (int pvi = 0; pvi < para_vessels.Count; ++pvi) {
          var vessel = para_vessels[pvi];
          if (!vessel.packed && is_manageable(vessel) && is_parachuting_kerbal(vessel)) {
            parachuting_kerbal_angular_velocities_.Add(
                vessel,
                vessel.rootPart.rb.angularVelocity);
          }
        }

        // TODO(egg): Set the degrees of freedom of the origin of `World` (by
        // toying with Krakensbane and FloatingOrigin) here.

        // Now we let the game and Unity do their thing.  Among other things,
        // the FashionablyLate callbacks, including ReportNonConservativeForces,
        // then the FlightIntegrator's FixedUpdate will run, then the Vessel's,
        // and eventually the physics simulation.
        StartCoroutine(WaitedForFixedUpdate());
      }
    }
  }

  private void OnDisable() {
    if (is_bad_installation_ || in_main_menu_) {
      return;
    }
    Log.Info("principia.ksp_plugin_adapter.PrincipiaPluginAdapter.OnDisable()");
    if (toolbar_button_ != null) {
      KSP.UI.Screens.ApplicationLauncher.Instance.RemoveModApplication(
          toolbar_button_);
    }
    Cleanup();
    WindowsDisposal();
    event_void_holder_.RemoveAll();
    TimingManager.FixedUpdateRemove(TimingManager.TimingStage.ObscenelyEarly,
                                    ObscenelyEarly);
    TimingManager.FixedUpdateRemove(TimingManager.TimingStage.Precalc, Precalc);
    TimingManager.FixedUpdateRemove(TimingManager.TimingStage.Early, Early);
    TimingManager.FixedUpdateRemove(TimingManager.TimingStage.Earlyish,
                                    Earlyish);
    TimingManager.FixedUpdateRemove(TimingManager.TimingStage.FashionablyLate,
                                    FashionablyLate);
    TimingManager.FixedUpdateRemove(TimingManager.TimingStage.FlightIntegrator,
                                    JaiFailliAttendre);
    TimingManager.FixedUpdateRemove(TimingManager.TimingStage.Late, Late);
    TimingManager.FixedUpdateRemove(
        TimingManager.TimingStage.BetterLateThanNever,
        BetterLateThanNever);
    TimingManager.LateUpdateRemove(
        TimingManager.TimingStage.BetterLateThanNever,
        BetterLateThanNeverLateUpdate);
    GameEvents.onPartDie.Remove(OnPartDie);
    GameEvents.onTimeWarpRateChanged.Remove(OnTimeWarpRateChanged);
    GameEvents.onVesselChange.Remove(OnVesselChange);
    GameEvents.onVesselSOIChanged.Remove(OnVesselSOIChanged);
  }

  private void OnVesselChange(Vessel vessel) {
    MapMarkerEventGovernor.InvalidateAll();
    map_node_pool_.Clear();
  }

  private void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> e) {
    MapMarkerEventGovernor.InvalidateAll();
    map_node_pool_.Clear();
  }

  private void OnTimeWarpRateChanged() {
    rolling_ephemeris_driver_?.OnTimeWarpRateChanged();
  }

  private void OnPartDie(Part part) {
    if (part != null) {
      collision_reporters_.Remove(part);
    }
  }

  #endregion

  private static readonly UnityEngine.WaitForFixedUpdate wait_for_fixed_update_ =
      new UnityEngine.WaitForFixedUpdate();

  private System.Collections.IEnumerator WaitedForFixedUpdate() {
    yield return wait_for_fixed_update_;

    try {
      // Unity's physics has just finished doing its thing.  If we correct the
      // positions here, nobody will know that they're not the ones obtained by
      // Unity.

      if (!time_is_advancing_) {
        yield break;
      }

      using (PerformanceProbe.Measure(ProbeCategory.UnityWaitedForFixedUpdate)) {
        // We are going to touch plenty of `Transform`s, so we will prevent
        // Unity from syncing with the physics system all the time.
        UnityEngine.Physics.autoSyncTransforms = false;

      double Δt = Planetarium.TimeScale * Planetarium.fetch.fixedDeltaTime;

      DualClockBridge.UpdateOriginTracking(krakensbane.FrameVel, FloatingOrigin.Offset);
      BatchStateSync.ResetFrameCounters();

      QP main_body_degrees_of_freedom = new QP{
          q = (XYZ)((FlightGlobals.currentMainBody ??
                     FlightGlobals.GetHomeBody()).position +
                    Δt * krakensbane.FrameVel),
          p = (XYZ)(-krakensbane.FrameVel)
      };

      // NOTE(egg): Inserting vessels and parts has to occur in
      // `WaitForFixedUpdate`, since some may be destroyed (by collisions) during
      // the physics step.  See also #1281.
      foreach (Vessel vessel in FlightGlobals.Vessels) {
        int main_body_index = vessel.mainBody.flightGlobalsIndex;
        string vessel_guid = GetVesselGuid(vessel);
        if (!is_manageable(vessel)) {
          if (plugin_.HasVessel(vessel_guid)) {
            Log.Info("Removing vessel " + vessel.vesselName + "(" + vessel.id +
                     ")" + " because " + UnmanageabilityReasons(vessel));
          }
          continue;
        }

        plugin_.InsertOrKeepVessel(vessel_guid,
                                   vessel.vesselName,
                                   main_body_index,
                                   !vessel.packed,
                                   out bool inserted);
        if (!vessel.packed) {
          Profiler.BeginSample("InsertOrKeepLoadedPart");
          var parts = vessel.parts;
          for (int pi = 0; pi < parts.Count; ++pi) {
            var part = parts[pi];
            if (!PartIsFaithful(part)) {
              continue;
            }
            UnityEngine.Rigidbody part_rb = part.rb;
            uint part_id = part.flightID;
            if (!part_id_to_degrees_of_freedom_.TryGetValue(
                    part_id,
                    out QP degrees_of_freedom)) {
              // Assumptions about the invariants of KSP will invariably fail.
              // This is a graceful fallback.
              Log.Error("Unpacked part " + part.name + " (" +
                        part_id.ToString("X") +
                        ") appeared between BetterLateThanNever and " +
                        "WaitForFixedUpdate.  Linearly extrapolating its " +
                        "position at the previous frame.");
              degrees_of_freedom = new QP{
                  q = (XYZ)((Vector3d)part_rb.position -
                            Δt * (Vector3d)part_rb.velocity),
                  p = (XYZ)part_rb.velocity
              };
            }
            // In the first few frames after spawning a Kerbal, its physicsMass is
            // 0; we use its rb.mass instead.
            // NOTE(egg): the physics engine does not move the celestials, so it
            // is fine to use `main_body_degrees_of_freedom` here rather than to
            // store it during `FixedUpdate` or one of its timings.
            var insert_state = new PartBatchInsertState{
                part_id = part_id,
                mass_in_tonnes = part.physicsMass == 0 ? part_rb.mass : part.physicsMass,
                centre_of_mass = (XYZ)part_rb.centerOfMass,
                moments_of_inertia_in_tonnes = (XYZ)part_rb.inertiaTensor,
                principal_axes_rotation = (WXYZ)part_rb.inertiaTensorRotation,
                main_body_index = main_body_index,
                main_body_world_degrees_of_freedom = main_body_degrees_of_freedom,
                part_world_degrees_of_freedom = degrees_of_freedom,
                part_rotation = (WXYZ)part_rb.rotation,
                part_angular_velocity = (XYZ)part_rb.angularVelocity,
                delta_t = Δt
            };
            insert_state.SetPartName(part.name);
            insert_state.SetVesselGuid(vessel_guid);

            PartBatchFlags flags = PartBatchFlags.None;
            if (is_lit_solid_booster(part)) {
              flags |= PartBatchFlags.SolidRocketMotor;
            }
            if (part_id_to_intrinsic_torque_.TryGetValue(
                    part_id,
                    out Vector3d intrinsic_torque)) {
              flags |= PartBatchFlags.HasIntrinsicTorque;
              insert_state.intrinsic_torque = (XYZ)intrinsic_torque;
            }
            if (part_id_to_intrinsic_force_.TryGetValue(part_id,
                  out Vector3d intrinsic_force)) {
              // When a Kerbal is doing an EVA and holding on to a ladder, the
              // ladder imbues them with their weight at the location of the
              // vessel to which the ladder is attached.  This leads to fantastic
              // effects where doing an EVA accelerates the vessel, see #1415.
              // Just say no to stupidity.
              if (!(vessel.isEVA && vessel.evaController.OnALadder)) {
                flags |= PartBatchFlags.HasIntrinsicForce;
                insert_state.intrinsic_force = (XYZ)intrinsic_force;
              }
            }
            insert_state.flags = (uint)flags;
            BatchStateSync.AddInsertState(ref insert_state);
            if (part_id_to_intrinsic_forces_.TryGetValue(part_id,
                    out var intrinsic_forces)) {
              for (int ifi = 0; ifi < intrinsic_forces.Count; ++ifi) {
                var part_centred_force = intrinsic_forces[ifi];
                plugin_.PartApplyIntrinsicForceAtPosition(
                    part_id,
                    (XYZ)part_centred_force.force,
                    (XYZ)part_centred_force.lever_arm);
              }
            }
          }
          Profiler.EndSample();
        } else if (inserted) {
          Profiler.BeginSample("InsertUnloadedPart");
          var parts = vessel.protoVessel.protoPartSnapshots;
          // For reasons that are unclear, the asteroid spawning code sometimes
          // generates the same flightID twice; we regenerate the flightID on
          // any asteroid we find.
          if (vessel.vesselType == VesselType.SpaceObject &&
              parts.Count == 1 &&
              parts[0].partName == "PotatoRoid" &&
              parts[0].flightID == parts[0].missionID) {
            var part = parts[0];
            uint old_id = part.flightID;
            part.flightID = ShipConstruction.GetUniqueFlightID(
                HighLogic.CurrentGame.flightState);
            Log.Info("Regenerating the part ID of " + vessel.name + ": " +
                     part.flightID + " (was " + old_id + ")");
          }
          // For reasons that are even more unclear, the comet spawning code
          // sometimes generates a zero flightID; when that happens, we
          // regenerate the flightID (#4574).
          if (vessel.vesselType == VesselType.SpaceObject &&
              parts.Count == 1 &&
              parts[0].partName == "PotatoComet" &&
              parts[0].flightID == 0) {
            var part = parts[0];
            uint old_id = part.flightID;
            part.flightID = ShipConstruction.GetUniqueFlightID(
                HighLogic.CurrentGame.flightState);
            Log.Info("Regenerating the part ID of " + vessel.name + ": " +
                     part.flightID + " (was " + old_id + ")");
          }
          foreach (ProtoPartSnapshot part in parts) {
            plugin_.InsertUnloadedPart(part.flightID,
                                       part.partName,
                                       vessel_guid,
                                       new QP{
                                           q = (XYZ)vessel.orbit.pos,
                                           p = (XYZ)vessel.orbit.vel
                                       });
          }
          Profiler.EndSample();
        }
      }

      BatchStateSync.DispatchInsertBatch(plugin_);

      plugin_.PrepareToReportCollisions();

      // The collisions are reported by the
      // CollisionReporter.OnCollisionEnter|Stay events, which occurred while we
      // yielded.
      // Here, the `CollisionReporter.collisions` are the collisions that occurred
      // in the physics simulation.
      bool had_collisions_this_step = false;
      var report_vessels = FlightGlobals.Vessels;
      for (int vi = 0; vi < report_vessels.Count; ++vi) {
        var vessel1 = report_vessels[vi];
        if (vessel1.packed || !is_manageable(vessel1)) {
          continue;
        }
        string vessel1_guid = GetVesselGuid(vessel1);
        if (plugin_.HasVessel(vessel1_guid)) {
          if (vessel1.isEVA &&
              (vessel1.evaController.OnALadder ||
               is_clambering(vessel1.evaController))) {
            had_collisions_this_step = true;
            var vessel2 = vessel1.evaController.LadderPart?.vessel;
            if (vessel2 != null && !vessel2.packed && is_manageable(vessel2)) {
              Log.Info("Reporting climbing a ladder");
              plugin_.ReportPartCollision(
                  vessel1.rootPart.flightID,
                  closest_physical_parent(
                      vessel1.evaController.LadderPart).flightID);
            } else {
              // vessel1 is either clambering (quite likely on the ground or on a
              // grounded vessel), or climbing a ladder on an unmanaged vessel.
              Log.Info("Reporting " +
                       (is_clambering(vessel1.evaController)
                            ? "clambering"
                            : "climbing an unmanaged ladder"));
              plugin_.ReportGroundCollision(vessel1.rootPart.flightID);
            }
          }

          Profiler.BeginSample("ReportGroundCollision");
          var v1_parts = vessel1.parts;
          for (int pi = 0; pi < v1_parts.Count; ++pi) {
            var part1 = v1_parts[pi];
            if (!PartIsFaithful(part1)) {
              continue;
            }
            bool wheel_grounded = false;
            var part_modules = part1.Modules;
            for (int mi = 0; mi < part_modules.Count; ++mi) {
              if (part_modules[mi] is ModuleWheelBase wheel && wheel.isGrounded) {
                wheel_grounded = true;
                break;
              }
            }
            if (wheel_grounded) {
              had_collisions_this_step = true;
              Log.Info("Reporting grounded wheel");
              plugin_.ReportGroundCollision(
                  closest_physical_parent(part1).flightID);
            }
            if (!collision_reporters_.TryGetValue(part1, out var collision_reporter) ||
                collision_reporter == null) {
              collision_reporter = part1.gameObject.GetComponent<CollisionReporter>();
              if (collision_reporter != null) {
                collision_reporters_[part1] = collision_reporter;
              }
            }
            if (collision_reporter == null) {
              // This would only happen if `part1` had been added after
              // `BetterLateThanNever`, but we never know what the game will throw
              // at us.
              continue;
            }
            foreach (var collision in collision_reporter.collisions) {
              had_collisions_this_step = true;
              var collider = collision.collider;
              if (collider == null) {
                // This happens, albeit quite rarely, see #1447.  When it happens,
                // the null collider remains in `currentCollisions` until the next
                // scene change, so we do not log, otherwise we would spam.
                continue;
              }
              if (collider.gameObject.layer == (int)UnityLayers.LocalScenery) {
                Log.Info("Reporting collision with local scenery " +
                         collider.name);
                plugin_.ReportGroundCollision(
                    closest_physical_parent(part1).flightID);
              }
              var part2 = collider.gameObject.GetComponentUpwards<Part>();
              var vessel2 = part2?.vessel;
              if (vessel2 == vessel1) {
                // All parts in a vessel are in the same pile up, so there is no
                // point in reporting this collision; this also causes issues
                // where disappearing kerbals collide with themselves.
                // NOTE(egg): It is unclear whether this is needed now that we
                // have the `CollisionReporter`.
                continue;
              }
              if (part1.State == PartStates.DEAD ||
                  part2?.State == PartStates.DEAD) {
                continue;
              }
              if (vessel2 != null) {
                if (is_unready_kerbal(vessel2)) {
                  // A Kerbal that just started an EVA and is not ready yet.  If
                  // we let it collide with `vessel1`, it will cause that vessel
                  // to become unmanageable and be removed from the plugin.  Of
                  // course, the vessel will come back once the Kerbal is ready,
                  // but at that point we'll have to trust the position given by
                  // KSP, and in practice that might cause the vessel to jump
                  // by hundreds of metres (#2590).  The bottom line is: we
                  // better ignore the collision, the Kerbal will soon become
                  // ready anyway.
                } else if (is_manageable(vessel2)) {
                  Log.Info(
                      $@"Reporting collision with collider {collider.name} ({
                        (UnityLayers)collider.gameObject.layer})");
                  plugin_.ReportPartCollision(
                      closest_physical_parent(part1).flightID,
                      closest_physical_parent(part2).flightID);
                } else {
                  Log.Info("Reporting collision with the unmanageable vessel " +
                           vessel2.vesselName + " (reason: " +
                           UnmanageabilityReasons(vessel2) + ")");
                  plugin_.ReportGroundCollision(
                      closest_physical_parent(part1).flightID);
                }
              }
            }
          }
          Profiler.EndSample();
        }
      }

      plugin_.FreeVesselsAndPartsAndCollectPileUps(Δt);

      Profiler.BeginSample("PartSetApparentRigidMotion");
      var apparent_vessels = FlightGlobals.Vessels;
      for (int avi = 0; avi < apparent_vessels.Count; ++avi) {
        var vessel = apparent_vessels[avi];
        if (vessel.packed) {
          continue;
        }
        string vessel_guid = GetVesselGuid(vessel);
        if (!plugin_.HasVessel(vessel_guid)) {
          continue;
        }
        var vessel_parts = vessel.parts;
        for (int pi = 0; pi < vessel_parts.Count; ++pi) {
          var part = vessel_parts[pi];
          if (!PartIsFaithful(part)) {
            continue;
          }
          if (main_body_change_countdown_ == 0 &&
              last_main_body_ == FlightGlobals.ActiveVessel?.mainBody) {
            UnityEngine.Rigidbody part_rb = part.rb;
            BatchStateSync.AddApparentMotion(
                part.flightID,
                new QP{q = (XYZ)part_rb.position,
                       p = (XYZ)part_rb.velocity},
                (WXYZ)part_rb.rotation,
                (XYZ)part_rb.angularVelocity);
          }
        }
      }
      BatchStateSync.DispatchApparentMotionBatch(plugin_);
      Profiler.EndSample();

      // Advance the lagging vessels and kill those which collided with a
      // celestial.
      {
        plugin_.CatchUpLaggingVessels(out DisposableIterator collided_vessels);
        using (collided_vessels) {
          for (;
               !collided_vessels.IteratorAtEnd();
               collided_vessels.IteratorIncrement()) {
            var vessel_guid = new Guid(collided_vessels.IteratorGetVesselGuid());
            Vessel vessel = FlightGlobals.FindVessel(vessel_guid);
            vessel?.Die();
          }
        }
      }

      if (!PhysicsCircuitBreaker.ShouldThrottleSecondaryPhysics()) {
        UpdatePredictions();
      }

      // We don't want to do too many things here, since all the KSP classes
      // still think they're in the preceding step.  We only nudge the Unity
      // transforms of loaded vessels & their parts.
      if (has_active_manageable_vessel() &&
          !FlightGlobals.ActiveVessel.packed &&
          plugin_.HasVessel(GetVesselGuid(FlightGlobals.ActiveVessel))) {
        Vector3d q_correction_at_root_part = Vector3d.zero;
        Vector3d v_correction_at_root_part = Vector3d.zero;
        CelestialBody main_body = FlightGlobals.ActiveVessel.mainBody;
        Part root_part = FlightGlobals.ActiveVessel.rootPart;
        Origin origin = new Origin{
            reference_part_is_at_origin  = FloatingOrigin.fetch.continuous,
            reference_part_is_unmoving =
                krakensbane_.FrameVel != Vector3d.zero,
            main_body_centre_in_world = (XYZ)main_body.position,
            reference_part_id = root_part.flightID
        };

        Profiler.BeginSample("PartGetActualRigidMotion");
        var motion_vessels = FlightGlobals.Vessels;
        for (int mvi = 0; mvi < motion_vessels.Count; ++mvi) {
          var vessel = motion_vessels[mvi];
          if (vessel.packed) {
            continue;
          }
          if (PhysicsCircuitBreaker.ShouldThrottleSecondaryPhysics() &&
              vessel != FlightGlobals.ActiveVessel) {
            continue;
          }
          string motion_vessel_guid = GetVesselGuid(vessel);
          if (!plugin_.HasVessel(motion_vessel_guid)) {
            continue;
          }

          var vessel_parts = vessel.parts;
          bool bypass_child_parts = HeterogeneousDynamicGovernor.EnableRigidMotionBypass &&
                                    VacuumBypassGovernor.IsVesselInStableVacuum(vessel) &&
                                    !VacuumBypassGovernor.ShouldForceCalibration(vessel.id) &&
                                    vessel.rootPart != null;

          Vector3d vessel_q_correction = Vector3d.zero;
          Vector3d vessel_v_correction = Vector3d.zero;

          if (bypass_child_parts) {
            Part vessel_root = vessel.rootPart;
            Vector3d old_root_pos = Vector3d.zero;
            UnityEngine.QuaternionD old_root_rot = UnityEngine.QuaternionD.identity;
            var root_pos = Vector3d.zero;
            var root_vel = Vector3d.zero;
            var root_rot = UnityEngine.QuaternionD.identity;
            var root_ang_vel = Vector3d.zero;

            if (PartIsFaithful(vessel_root)) {
              UnityEngine.Rigidbody root_rb = vessel_root.rb;
              old_root_pos = root_rb.position;
              old_root_rot = root_rb.rotation;

              QPRW root_motion = plugin_.PartGetActualRigidMotion(vessel_root.flightID, origin);
              QP root_dof = root_motion.qp;
              root_pos = (Vector3d)root_dof.q;
              root_vel = (Vector3d)root_dof.p;
              root_rot = (UnityEngine.QuaternionD)root_motion.r;
              root_ang_vel = (Vector3d)root_motion.w;

              vessel_q_correction = root_pos - old_root_pos;
              vessel_v_correction = root_vel - root_rb.velocity;

              if (vessel_root == root_part) {
                q_correction_at_root_part = vessel_q_correction;
                v_correction_at_root_part = vessel_v_correction;
              }

              root_rb.position = root_pos;
              root_rb.rotation = root_rot;
              root_rb.transform.SetPositionAndRotation(root_pos, root_rot);
              root_rb.velocity = root_vel;
              root_rb.angularVelocity = root_ang_vel;
              if (HeterogeneousDynamicGovernor.EnableAutonomousPhysicsDecoupling) {
                NativeStateSnapshotChannel.PublishSnapshot(
                    motion_vessel_guid,
                    new MotionSnapshot{
                        time_ut = plugin_.CurrentTime(),
                        position = root_pos,
                        velocity = root_vel,
                        rotation = root_rot,
                        angular_velocity = root_ang_vel
                    });
              }
              HeterogeneousDynamicGovernor.RecordRigidMotionExecution(1);
            }

            UnityEngine.QuaternionD delta_rot = root_rot * UnityEngine.QuaternionD.Inverse(old_root_rot);
            for (int vpi = 0; vpi < vessel_parts.Count; ++vpi) {
              var part = vessel_parts[vpi];
              if (!PartIsFaithful(part) || part == vessel_root) {
                continue;
              }
              UnityEngine.Rigidbody part_rb = part.rb;
              Vector3d r_rel = (Vector3d)part_rb.position - old_root_pos;
              Vector3d r_rel_rot = delta_rot * r_rel;
              Vector3d new_part_pos = root_pos + r_rel_rot;
              UnityEngine.QuaternionD new_part_rot = delta_rot * (UnityEngine.QuaternionD)part_rb.rotation;

              part_rb.position = new_part_pos;
              part_rb.rotation = new_part_rot;
              part_rb.transform.SetPositionAndRotation(new_part_pos, new_part_rot);
              part_rb.angularVelocity = root_ang_vel;
              part_rb.velocity = root_vel + Vector3d.Cross(root_ang_vel, r_rel_rot);
              HeterogeneousDynamicGovernor.RecordRigidMotionBypass(1);
            }
          } else {
            for (int vpi = 0; vpi < vessel_parts.Count; ++vpi) {
              var part = vessel_parts[vpi];
              if (!PartIsFaithful(part)) {
                continue;
              }
              BatchStateSync.AddActualMotionQuery(part.flightID);
            }
            int actual_count = BatchStateSync.QueryActualMotionBatch(
                plugin_,
                origin,
                out uint[] query_ids,
                out QPRW[] motions);
            int motion_idx = 0;
            for (int vpi = 0; vpi < vessel_parts.Count; ++vpi) {
              var part = vessel_parts[vpi];
              if (!PartIsFaithful(part)) {
                continue;
              }
              UnityEngine.Rigidbody part_rb = part.rb;
              QPRW part_actual_motion = motions[motion_idx++];
              QP part_actual_degrees_of_freedom = part_actual_motion.qp;
              var part_position = (Vector3d)part_actual_degrees_of_freedom.q;
              var part_velocity = (Vector3d)part_actual_degrees_of_freedom.p;
              var part_rotation = (UnityEngine.QuaternionD)part_actual_motion.r;
              var part_angular_velocity = (Vector3d)part_actual_motion.w;
              if (part == root_part) {
                q_correction_at_root_part = part_position - part_rb.position;
                v_correction_at_root_part = part_velocity - part_rb.velocity;
                if (HeterogeneousDynamicGovernor.EnableAutonomousPhysicsDecoupling) {
                  NativeStateSnapshotChannel.PublishSnapshot(
                      motion_vessel_guid,
                      new MotionSnapshot{
                          time_ut = plugin_.CurrentTime(),
                          position = part_position,
                          velocity = part_velocity,
                          rotation = part_rotation,
                          angular_velocity = part_angular_velocity
                      });
                }
              }

              part_rb.position = part_position;
              part_rb.rotation = part_rotation;
              part_rb.transform.SetPositionAndRotation(
                  part_position,
                  part_rotation);

              part_rb.velocity = part_velocity;
              part_rb.angularVelocity = part_angular_velocity;
              HeterogeneousDynamicGovernor.RecordRigidMotionExecution(1);
            }
          }
        }
        Profiler.EndSample();
        Profiler.EndSample();

        var phys_objects = FlightGlobals.physicalObjects;
        for (int poi = 0; poi < phys_objects.Count; ++poi) {
          var physical_object = phys_objects[poi];
          if (physical_object == null || physical_object.rb == null) {
            continue;
          }
          // TODO(egg): This is no longer sensible.
          UnityEngine.Rigidbody physical_object_rb = physical_object.rb;
          physical_object_rb.position += q_correction_at_root_part;
          physical_object_rb.transform.position += q_correction_at_root_part;
          physical_object_rb.velocity += v_correction_at_root_part;
        }
        QP main_body_dof = plugin_.CelestialWorldDegreesOfFreedom(
            main_body.flightGlobalsIndex,
            origin,
            plugin_.CurrentTime());
        krakensbane.FrameVel = -(Vector3d)main_body_dof.p;
        Vector3d offset = (Vector3d)main_body_dof.q - main_body.position;
        // We cannot use FloatingOrigin.SetOffset to move the world here, because
        // as far as I can tell, that does not move the bubble relative to the
        // rest of the universe.
        var bodies = FlightGlobals.Bodies;
        for (int bi = 0; bi < bodies.Count; ++bi) {
          bodies[bi].position += offset;
        }
        var offset_vessels = FlightGlobals.Vessels;
        for (int ovi = 0; ovi < offset_vessels.Count; ++ovi) {
          var vessel = offset_vessels[ovi];
          if (is_manageable_on_rails(vessel)) {
            vessel.SetPosition(vessel.transform.position + offset);
          }
        }
        // NOTE(egg): this is almost certainly incorrect, since we give the
        // bodies their positions at the next instant, whereas KSP still expects
        // them at the previous instant, and will propagate them at the beginning
        // of the next frame...
      }

      if (last_main_body_ != FlightGlobals.ActiveVessel?.mainBody) {
        main_body_change_countdown_ = 1;
        last_main_body_ = FlightGlobals.ActiveVessel?.mainBody;
      } else if (main_body_change_countdown_ > 0) {
        --main_body_change_countdown_;
      }

      bool should_sync_transforms = true;
      Vessel active = FlightGlobals.ActiveVessel;
      if (VacuumBypassGovernor.EnableVacuumBypass &&
          active != null &&
          !active.LandedOrSplashed &&
          !had_collisions_this_step &&
          VacuumBypassGovernor.IsVesselInStableVacuum(active)) {
        should_sync_transforms = (UnityEngine.Time.frameCount % VacuumBypassGovernor.CalibrationIntervalFrames == 0);
      }

      if (PhysicsCircuitBreaker.ShouldThrottleSecondaryPhysics()) {
        should_sync_transforms = false;
      }

      if (should_sync_transforms) {
        UnityEngine.Physics.SyncTransforms();
      }
      DualClockBridge.RecordPhysicsStep(plugin_.CurrentTime(), Δt);
      UnityEngine.Physics.autoSyncTransforms = true;
      }
    } catch (Exception e) {
      Log.Fatal(e.ToString());
    }
  }

  private static class PhysicsCircuitBreaker {
    public static bool EnableCircuitBreaker => HeterogeneousDynamicGovernor.EnableCircuitBreaker;
    public static float PhysicsBudgetMs => HeterogeneousDynamicGovernor.PhysicsFrameBudgetMs;

    private static int last_frame_count_ = -1;
    private static int fixed_updates_in_frame_ = 0;
    private static readonly System.Diagnostics.Stopwatch frame_stopwatch_ =
        new System.Diagnostics.Stopwatch();

    public static int FixedUpdatesInFrame => fixed_updates_in_frame_;
    public static long TrippedCyclesCount => HeterogeneousDynamicGovernor.CircuitBreakerTrips;

    public static void OnPhysicsStepBegin() {
      int current_frame = UnityEngine.Time.frameCount;
      if (current_frame != last_frame_count_) {
        last_frame_count_ = current_frame;
        fixed_updates_in_frame_ = 1;
        frame_stopwatch_.Reset();
        frame_stopwatch_.Start();
      } else {
        fixed_updates_in_frame_++;
      }
    }

    public static int MaxAllowedFixedUpdates =>
        UnityEngine.Mathf.Max(4, UnityEngine.Mathf.CeilToInt(UnityEngine.Time.maximumDeltaTime / UnityEngine.Mathf.Max(0.001f, UnityEngine.Time.fixedDeltaTime)));

    public static bool ShouldThrottleSecondaryPhysics() {
      if (!EnableCircuitBreaker) {
        return false;
      }
      int max_allowed = MaxAllowedFixedUpdates;
      if (fixed_updates_in_frame_ > 1) {
        float elapsed_ms = (float)frame_stopwatch_.Elapsed.TotalMilliseconds;
        if (elapsed_ms >= PhysicsBudgetMs || fixed_updates_in_frame_ > max_allowed) {
          HeterogeneousDynamicGovernor.RecordCircuitBreakerTrip();
          return true;
        }
      }
      return false;
    }
  }

  private void ObscenelyEarly() {
    PhysicsCircuitBreaker.OnPhysicsStepBegin();
    var vessels = FlightGlobals.Vessels;
    for (int i = vessels.Count - 1; i >= 0; --i) {
      var vessel = vessels[i];
      if (vessel.loaded && vessel.precalc != null) {
        vessel.precalc.enabled = false;
      }
    }
  }

  private void Precalc() {
    using (PerformanceProbe.Measure(ProbeCategory.UnityPrecalc)) {
      if (!PluginRunning()) {
        return;
      }
      bool throttle_secondary = PhysicsCircuitBreaker.ShouldThrottleSecondaryPhysics();
      if (FlightGlobals.ActiveVessel?.situation == Vessel.Situations.PRELAUNCH &&
          FlightGlobals.ActiveVessel?.orbitDriver?.lastMode ==
          OrbitDriver.UpdateMode.TRACK_Phys &&
          FlightGlobals.ActiveVessel?.orbitDriver?.updateMode ==
          OrbitDriver.UpdateMode.IDLE) {
        Log.Info("Skipping AdvanceTime and SetBodyFrames while waiting for the " +
                 "vessel to be fully ready (see #1421).");
      } else {
        double universal_time = Planetarium.GetUniversalTime();
        time_is_advancing_ = time_is_advancing(universal_time);
        if (time_is_advancing_) {
          if (vessel_futures_.Count > 0) {
            HarvestVesselsAndApplyOnRails(non_blocking: false);
          }
          using (PerformanceProbe.Measure(ProbeCategory.PhysicsAdvance)) {
            plugin_.AdvanceTime(universal_time, Planetarium.InverseRotAngle);
          }
          if (!apocalypse_dialog_.Shown()) {
            if (plugin_.HasEncounteredApocalypse(out string revelation)) {
              apocalypse_dialog_.message = revelation;
              apocalypse_dialog_.Show();
            }
          }
          var vessels = FlightGlobals.Vessels;
          for (int i = 0; i < vessels.Count; ++i) {
            var vessel = vessels[i];
            if (vessel.packed) {
              string vessel_guid = GetVesselGuid(vessel);
              if (plugin_.HasVessel(vessel_guid)) {
                vessel_futures_.Add(
                    plugin_.FutureCatchUpVessel(vessel_guid));
              }
            }
          }
        }
        if (!throttle_secondary) {
          SetBodyFrames();
        }
      }
      // Unfortunately there is no way to get scheduled between Planetarium and
      // VesselPrecalculate, so we get scheduled after VesselPrecalculate, set the
      // body frames for our weird tilt, and run VesselPrecalculate manually.
      // Sob.
      // NOTE(egg): we cannot use foreach here, and we must iterate downwards,
      // since vessel.precalc.FixedUpdate may remove its vessel.
      var all_vessels = FlightGlobals.Vessels;
      for (int i = all_vessels.Count - 1; i >= 0; --i) {
        var vessel = all_vessels[i];
        if (vessel.precalc == null) {
          continue;
        }
        if (vessel.loaded) {
          if (throttle_secondary && vessel != FlightGlobals.ActiveVessel) {
            continue;
          }
          if (!VacuumBypassGovernor.ShouldBypassPrecalc(vessel)) {
            vessel.precalc.enabled = true;
            // In stock this is equivalent to `FixedUpdate()`.  With
            // ModularFlightIntegrator's ModularVesselPrecalculate, which gets run
            // in TimingPre like us, this comes with a flag that ensures it only gets
            // run once.
            vessel.precalc.MainPhysics(true);
          }
        }
      }
    }
  }

  private void Early() {
    if (!PluginRunning()) {
      return;
    }
    // Stage 3 Decoupling (Phase 1):
    // If staged harvesting is enabled, do NOT block the main thread at -99.
    // Allow the C++ worker thread pool to continue concurrent integration through
    // aerodynamics and PhysX (~8-15 ms window). Futures are harvested at BetterLateThanNever (+8008).
    if (HeterogeneousDynamicGovernor.EnableStagedHarvesting) {
      return;
    }
    HarvestVesselsAndApplyOnRails();
  }

  private void HarvestVesselsAndApplyOnRails(bool non_blocking = false) {
    if (!PluginRunning() || vessel_futures_.Count == 0) {
      return;
    }
    using (PerformanceProbe.Measure(ProbeCategory.PhysicsAdvance)) {
      collided_vessels_cache_.Clear();
      int unready_count = 0;
      double current_ut = Planetarium.GetUniversalTime();

      for (int fi = 0; fi < vessel_futures_.Count; ++fi) {
        var future = vessel_futures_[fi];
        if (non_blocking) {
          if (plugin_.FutureTryHarvestVesselCatchUp(
                  ref future,
                  out DisposableIterator collided_vessels)) {
            using (collided_vessels) {
              for (;
                   !collided_vessels.IteratorAtEnd();
                   collided_vessels.IteratorIncrement()) {
                var vessel_guid = new Guid(collided_vessels.IteratorGetVesselGuid());
                Vessel vessel = FlightGlobals.FindVessel(vessel_guid);
                collided_vessels_cache_.Add(vessel);
              }
            }
          } else {
            // Task not ready yet in background: keep future for next frame harvest!
            vessel_futures_[unready_count++] = future;
          }
        } else {
          plugin_.FutureWaitForVesselToCatchUp(ref future,
                                               out DisposableIterator collided_vessels);
          using (collided_vessels) {
            for (;
                 !collided_vessels.IteratorAtEnd();
                 collided_vessels.IteratorIncrement()) {
              var vessel_guid = new Guid(collided_vessels.IteratorGetVesselGuid());
              Vessel vessel = FlightGlobals.FindVessel(vessel_guid);
              collided_vessels_cache_.Add(vessel);
            }
          }
        }
      }

      if (non_blocking) {
        if (unready_count < vessel_futures_.Count) {
          vessel_futures_.RemoveRange(unready_count, vessel_futures_.Count - unready_count);
        }
      } else {
        vessel_futures_.Clear();
      }

      foreach (var vessel in collided_vessels_cache_) {
        vessel?.Die();
      }
      collided_vessels_cache_.Clear();

      ApplyToVesselsOnRails(current_ut);
      HeterogeneousDynamicGovernor.RecordStagedHarvest();
    }
  }

  private void Earlyish() {
    if (PhysicsCircuitBreaker.ShouldThrottleSecondaryPhysics()) {
      return;
    }
    rolling_ephemeris_driver_?.Update();
  }

  private void FashionablyLate() {
    // We fetch the forces from the census of nonconservatives here;
    // part.forces, part.force, and part.torque are cleared by the
    // FlightIntegrator's FixedUpdate (while we are yielding).
    if (PluginRunning()) {
      if (has_active_manageable_vessel() && FlightGlobals.ActiveVessel.packed) {
        if (PhysicsGlobals.GraviticForceMultiplier != 0) {  // sic.
          Log.Info("Killing stock gravity");
          PhysicsGlobals.GraviticForceMultiplier = 0;
        }
      } else if (PhysicsGlobals.GraviticForceMultiplier == 0) {
        Log.Info("Reinstating stock gravity");
        PhysicsGlobals.GraviticForceMultiplier = 1;
      }
      part_id_to_intrinsic_torque_.Clear();
      part_id_to_intrinsic_force_.Clear();
      ReleaseAllForceLists();
      var vessels = FlightGlobals.Vessels;
      for (int i = 0; i < vessels.Count; ++i) {
        var vessel = vessels[i];
        if (vessel.packed || !is_manageable(vessel)) {
          continue;
        }
        if (VacuumBypassGovernor.IsVesselInStableVacuum(vessel)) {
          if (!VacuumBypassGovernor.ShouldForceCalibration(vessel.id)) {
            VacuumBypassGovernor.RecordBypassedForces();
            continue;
          }
        }
        VacuumBypassGovernor.RecordExecutedForces();
        var parts = vessel.parts;
        for (int j = 0; j < parts.Count; ++j) {
          var part = parts[j];
          if (!PartIsFaithful(part)) {
            continue;
          }
          Vector3d parachute_torque = Vector3d.zero;
          if (part == vessel.rootPart &&
              is_parachuting_kerbal(vessel) &&
              parachuting_kerbal_angular_velocities_.TryGetValue(
                  vessel,
                  out Vector3d old_angular_velocity)) {
            // Instead of applying forces some distance from the centre of mass,
            // or applying torques, EVA parachutes directly set the angular
            // velocity of the Kerbal.  We register a torque equivalent to the
            // change in angular momentum; failing to do so would cause the
            // parachute to act on the linear motion but not the attitude,
            // so that Kerbals would keep spinning under their parachute in
            // stock, and would orient themselves head-down (and parachute-down)
            // with FAR.  See #2607.
            // To quote ferram4, “consider it all cursed”.
            Vector3d Δω_world = part.rb.angularVelocity - old_angular_velocity;
            var ΔL_world =
                (Vector3d)Interface.PartAngularMomentumFromAngularVelocity(
                    world_angular_velocity: (XYZ)Δω_world,
                    moments_of_inertia_in_tonnes:
                    (XYZ)part.rb.inertiaTensor,
                    principal_axes_rotation:
                    (WXYZ)part.rb.inertiaTensorRotation,
                    part_rotation: (WXYZ)part.rb.rotation);
            double Δt = Planetarium.TimeScale *
                        Planetarium.fetch.fixedDeltaTime;
            parachute_torque = ΔL_world / Δt;
          }
          if (part.torque != Vector3d.zero ||
              parachute_torque != Vector3d.zero) {
            part_id_to_intrinsic_torque_.Add(part.flightID,
                                             part.torque + parachute_torque);
          }
          if (part.force != Vector3d.zero) {
            part_id_to_intrinsic_force_.Add(part.flightID, part.force);
          }
          int forces_count = part.forces.Count;
          if (forces_count > 0) {
            if (!part_id_to_intrinsic_forces_.TryGetValue(part.flightID, out var list)) {
              list = AcquireForceList();
              part_id_to_intrinsic_forces_.Add(part.flightID, list);
            }
            for (int k = 0; k < forces_count; ++k) {
              list.Add(PartCentredForceHolder.FromPartForceHolder(part, part.forces[k]));
            }
          }
        }
      }
    }
  }

  private void JaiFailliAttendre() {
    // We fetch the forces from stock aerodynamics, which does not use
    // `Part.AddForce` etc.
    if (PluginRunning()) {
      double Δt = Planetarium.TimeScale * Planetarium.fetch.fixedDeltaTime;
      var vessels = FlightGlobals.Vessels;
      for (int i = 0; i < vessels.Count; ++i) {
        var vessel = vessels[i];
        if (vessel.packed || !is_manageable(vessel)) {
          continue;
        }
        if (VacuumBypassGovernor.ShouldBypassAero(vessel)) {
          continue;
        }
        var parts = vessel.parts;
        for (int j = 0; j < parts.Count; ++j) {
          var part = parts[j];
          if (!PartIsFaithful(part)) {
            continue;
          }
          if (part.atmDensity <= 0) {
            continue;
          }
          Part physical_parent = closest_physical_parent(part);
          if (part.bodyLiftLocalVector != UnityEngine.Vector3.zero ||
              part.dragVector != UnityEngine.Vector3.zero) {
            if (!part_id_to_intrinsic_forces_.TryGetValue(physical_parent.flightID, out var list)) {
              list = AcquireForceList();
              part_id_to_intrinsic_forces_.Add(physical_parent.flightID, list);
            }
            if (part.bodyLiftLocalVector != UnityEngine.Vector3.zero) {
              list.Add(new PartCentredForceHolder(
                  part.partTransform.TransformDirection(part.bodyLiftLocalVector),
                  part.partTransform.TransformPoint(part.bodyLiftLocalPosition) - physical_parent.rb.worldCenterOfMass));
            }
            if (part.dragVector != UnityEngine.Vector3.zero) {
              Vector3d drag_pos = physical_parent != part &&
                                  PhysicsGlobals.ApplyDragToNonPhysicsPartsAtParentCoM
                                      ? physical_parent.rb.worldCenterOfMass
                                      : part.partTransform.TransformPoint(part.CoPOffset);
              list.Add(new PartCentredForceHolder(
                  -part.dragVectorDir * part.dragScalar,
                  drag_pos - physical_parent.rb.worldCenterOfMass));
            }
          }

          // KSP sets `part.rb.angularDrag` and lets Unity/PhysX compute
          // dampening.  However, it doesn't tell us about it so we end up with
          // uncontrolled oscillations.  Therefore, we must create the damping
          // torque out of thin air here.  Note that in FAR
          // `part.rb.angularDrag` is 0 and we are properly given the torque
          // through `Part.AddTorque` so this code has no effect.
          // The documentation in https://documentation.help/NVIDIA-PhysX-SDK-Guide/RigidDynamics.html#damping.
          // is wrong because `angularDrag * angularVelocity` is not physically
          // a torque, it represents an exponential decay of the angular
          // velocity as is clear from the code at https://github.com/NVIDIAGameWorks/PhysX-3.4/blob/5e42a5f112351a223c19c17bb331e6c55037b8eb/PhysX_3.4/Source/LowLevelDynamics/src/DyBodyCoreIntegrator.h#L72-L75.
          // This is entirely nonphysical, but we endeavour to preserve the KSP
          // aerodynamics, so we compute a torque the effect of which is the
          // expected exponential decay.
          // See #3697 and #4166.
          var drag_torque =
              (UnityEngine.Vector3)Interface.PartDragTorqueFromAngularVelocity(
                  angular_drag_per_second: part.rb.angularDrag,
                  delta_t: Δt,
                  world_angular_velocity: (XYZ)part.rb.angularVelocity,
                  moments_of_inertia_in_tonnes: (XYZ)part.rb.inertiaTensor,
                  principal_axes_rotation: (WXYZ)part.rb.inertiaTensorRotation,
                  part_rotation: (WXYZ)part.rb.rotation);
          if (drag_torque != UnityEngine.Vector3.zero) {
            if (part_id_to_intrinsic_torque_.ContainsKey(
                    physical_parent.flightID)) {
              part_id_to_intrinsic_torque_[physical_parent.flightID] +=
                  drag_torque;
            } else {
              part_id_to_intrinsic_torque_.Add(physical_parent.flightID,
                                               drag_torque);
            }
          }
        }
      }
    }
  }

  private void Late() {
    if (PluginRunning()) {
      if (FlightGlobals.currentMainBody?.inverseRotation == true) {
        plugin_.SetWorldRotationalReferenceFrame(
            FlightGlobals.currentMainBody.flightGlobalsIndex);
      } else {
        plugin_.ClearWorldRotationalReferenceFrame();
      }
    }
  }

  private void BetterLateThanNever() {
    using (PerformanceProbe.Measure(ProbeCategory.UnityBetterLate)) {
      if (PluginRunning()) {
        HarvestVesselsAndApplyOnRails(non_blocking: true);
        part_id_to_degrees_of_freedom_.Clear();
        var manageable_vessels = FlightGlobals.Vessels;
        for (int i = 0; i < manageable_vessels.Count; ++i) {
          var vessel = manageable_vessels[i];
          if (vessel.packed || !is_manageable(vessel)) {
            continue;
          }
          var parts = vessel.parts;
          for (int j = 0; j < parts.Count; ++j) {
            var part = parts[j];
            if (PartIsFaithful(part)) {
              // TODO(egg): use the centre of mass.
              part_id_to_degrees_of_freedom_.Add(part.flightID,
                                                 new QP{
                                                     q = (XYZ)part.rb.position,
                                                     p = (XYZ)part.rb.velocity
                                                 });
            }
          }
        }
      }
      if (!PhysicsCircuitBreaker.ShouldThrottleSecondaryPhysics()) {
        var all_vessels = FlightGlobals.Vessels;
        for (int i = 0; i < all_vessels.Count; ++i) {
          var vessel = all_vessels[i];
          if (vessel.packed) {
            continue;
          }
          var parts = vessel.parts;
          for (int j = 0; j < parts.Count; ++j) {
            var part = parts[j];
            if (!collision_reporters_.TryGetValue(part, out var reporter) || reporter == null) {
              reporter = part.gameObject.GetComponent<CollisionReporter>();
              if (reporter == null) {
                reporter = part.gameObject.AddComponent<CollisionReporter>();
              }
              collision_reporters_[part] = reporter;
            }
          }
        }
      }
    }
  }

  private void ProcessCommandPacket(in CommandPacket cmd) {
    switch (cmd.type) {
      case CommandType.SetManœuvre:
      case CommandType.DeleteManœuvre:
        flight_plan_dirty = true;
        break;
      case CommandType.SetPlottingFrame:
        break;
      case CommandType.TriggerPrediction:
        plotter_?.InvalidateAll();
        break;
      default:
        break;
    }
  }

  private void BetterLateThanNeverLateUpdate() {
    // Phase 2 & 3 Decoupled Rendering: instantaneous evaluation from lock-free triple buffer & dual-clock bridge.
    double render_ut = Planetarium.GetUniversalTime();
    DualClockBridge.RecordRenderFrame(render_ut);

    if (HeterogeneousDynamicGovernor.EnableAutonomousPhysicsDecoupling &&
        HeterogeneousDynamicGovernor.EnableAutonomousSnapshotSampling) {
      var active = FlightGlobals.ActiveVessel;
      if (active != null) {
        string active_guid = GetVesselGuid(active);
        NativeStateSnapshotChannel.TrySampleSnapshot(
            active_guid,
            render_ut,
            out Vector3d sample_pos,
            out UnityEngine.QuaternionD sample_rot);
      }
    }

    // Drain and process SPSC commands from UI/background threads
    while (SpscCommandBus.TryPollCommand(out var cmd)) {
      ProcessCommandPacket(in cmd);
    }

    // While we draw the trajectories directly (and thus do so after everything
    // else has been rendered), we rely on the game to render its map nodes.
    // Since the screen position is determined in `MapNode.NodeUpdate`, it must
    // be called before rendering occurs, but after the cameras have moved;
    // otherwise, the map nodes will lag behind when the camera is moved.
    // The only timing that satisfies these constraints is BetterLateThanNever
    // in LateUpdate.
    map_node_pool_.Update();
  }

  private void SetBodyFrames() {
    if (PluginRunning()) {
      bool previous_auto_sync = UnityEngine.Physics.autoSyncTransforms;
      UnityEngine.Physics.autoSyncTransforms = false;
      try {
        var main_body = FlightGlobals.currentMainBody;
        if (main_body != null && main_body != last_main_body_for_rotation_) {
          main_body.rotationPeriod =
              plugin_.CelestialRotationPeriod(
                  main_body.flightGlobalsIndex);
          main_body.initialRotation =
              plugin_.CelestialInitialRotationInDegrees(
                  main_body.flightGlobalsIndex);
          last_main_body_for_rotation_ = main_body;
        }
        if (topological_bodies_ == null || topological_bodies_.Count == 0) {
          BuildTopologicalBodies();
        }
        float current_realtime = UnityEngine.Time.realtimeSinceStartup;
        int current_frame = UnityEngine.Time.frameCount;
        UpdateCameraWorldPositionForLod();
        if (topological_bodies_ != null) {
          double universal_time = Planetarium.GetUniversalTime();
          for (int i = 0; i < topological_bodies_.Count; ++i) {
            var body = topological_bodies_[i];
            if (ShouldUpdateCelestialBody(body, current_realtime, current_frame)) {
              UpdateBody(body, universal_time);
            }
          }
        }

        var all_bodies = FlightGlobals.Bodies;
        SceneGraphDecoupler.RecordPassTierCounts(all_bodies, cached_camera_world_position_);
        for (int i = 0; i < all_bodies.Count; ++i) {
          var body = all_bodies[i];
          int idx = body.flightGlobalsIndex;
          bool is_priority = IsPriorityCelestialBody(body);
          bool updated_this_frame = is_priority ||
              (idx >= 0 && idx < celestial_last_update_frame_.Length &&
               (celestial_last_update_frame_[idx] == 0 ||
                celestial_last_update_frame_[idx] == current_frame));

          // Only compute CelestialRotation across P/Invoke and update Transform when the body
          // was actually updated this frame or is a priority body, AND when SceneGraphDecoupler
          // determines that the Unity GameObject Transform should be written (avoiding PQS dirty sweeps).
          if (updated_this_frame &&
              SceneGraphDecoupler.ShouldUpdateTransform(
                  body, cached_camera_world_position_, current_realtime, current_frame)) {
            var swizzly_body_world_to_world =
                ((UnityEngine.QuaternionD)plugin_.CelestialRotation(
                      body.flightGlobalsIndex)).swizzle;
            body.BodyFrame = new Planetarium.CelestialFrame{
                X = swizzly_body_world_to_world * unit_x_,
                Y = swizzly_body_world_to_world * unit_y_,
                Z = swizzly_body_world_to_world * unit_z_
            };
            body.rotation = body.BodyFrame.Rotation.swizzle;
            body.bodyTransform.rotation = body.rotation;
          }
        }
      } finally {
        UnityEngine.Physics.autoSyncTransforms = previous_auto_sync;
      }
    }
  }

  private void RenderGuidance(Vessel active_vessel) {
    string vessel_guid = GetVesselGuid(active_vessel);
    if (plugin_.HasVessel(vessel_guid) &&
        plugin_.FlightPlanExists(vessel_guid)) {
      // Here the vessel is known to the plugin and has a flight plan.
      // This duplicates a bit of code in FlightPlanner.
      // UpdateVesselAndBurnEditors but it's probably not worth factoring out.
      double current_time = plugin_.CurrentTime();
      // Note that we don't want to look at the anomalous manœuvres as we may
      // not even be able to build a Frenet frame for them.
      int number_of_nomalous_manœuvres =
          plugin_.FlightPlanNumberOfManoeuvres(vessel_guid) -
          plugin_.FlightPlanNumberOfAnomalousManoeuvres(vessel_guid);
      int? first_future_manœuvre_index = null;
      for (int i = 0; i < number_of_nomalous_manœuvres; ++i) {
        NavigationManoeuvre manœuvre =
            plugin_.FlightPlanGetManoeuvre(vessel_guid, i);
        if (current_time < manœuvre.final_time) {
          first_future_manœuvre_index = i;
          break;
        }
      }
      if (first_future_manœuvre_index.HasValue) {
        // Here the flight plan has a manœuvre in the future.
        XYZ guidance = plugin_.FlightPlanGetGuidance(
            vessel_guid,
            first_future_manœuvre_index.Value);
        Burn burn = plugin_.FlightPlanGetManoeuvre(
            vessel_guid,
            first_future_manœuvre_index.Value).burn;
        // Clear the guidance node for one frame after scheduled burnout so
        // that guidance methods (stock SAS, MechJeb, etc.) that follow it
        // bail out instead of guiding to the next burn with engines still
        // firing.
        bool skip_guidance = last_guidance_manœuvre_ != null &&
                             last_guidance_manœuvre_ !=
                             first_future_manœuvre_index;
        last_guidance_manœuvre_ = first_future_manœuvre_index;
        if (!skip_guidance &&
            flight_planner_.show_guidance &&
            !IsNaN(guidance)) {
          // The user wants to show the guidance node, and that node was
          // properly computed by the C++ code.
          PatchedConicSolver solver = active_vessel.patchedConicSolver;
          if (guidance_node_ == null ||
              !solver.maneuverNodes.Contains(guidance_node_)) {
            while (solver.maneuverNodes.Count > 0) {
              solver.maneuverNodes.Last().RemoveSelf();
            }
            guidance_node_ = solver.AddManeuverNode(burn.initial_time);
          } else {
            while (solver.maneuverNodes.Count > 1) {
              if (solver.maneuverNodes.First() == guidance_node_) {
                solver.maneuverNodes.Last().RemoveSelf();
              } else {
                solver.maneuverNodes.First().RemoveSelf();
              }
            }
          }
          var stock_orbit = guidance_node_.patch;
          Vector3d stock_velocity_at_node_time = stock_orbit.
              getOrbitalVelocityAtUT(burn.initial_time).xzy;
          Vector3d stock_displacement_from_parent_at_node_time = stock_orbit.
              getRelativePositionAtUT(burn.initial_time).xzy;
          UnityEngine.Quaternion stock_frenet_frame_to_world =
              UnityEngine.Quaternion.LookRotation(
                  stock_velocity_at_node_time,
                  Vector3d.Cross(stock_velocity_at_node_time,
                                 stock_displacement_from_parent_at_node_time));
          guidance_node_.DeltaV = ((Vector3d)burn.intensity.xyz).magnitude *
                                  (Vector3d)(UnityEngine.Quaternion.Inverse(
                                                 stock_frenet_frame_to_world) *
                                             (Vector3d)guidance);
          guidance_node_.UT = burn.initial_time;
          solver.UpdateFlightPlan();
          // Return here after setting the guidance node.  All other paths will
          // clear the guidance node.
          return;
        }
      }
    }
    if (guidance_node_ != null) {
      // We may end up here with a guidance node created for another vessel when
      // switching vessels, see #3873.
      if (active_vessel.patchedConicSolver.maneuverNodes.Contains(
              guidance_node_)) {
        guidance_node_.RemoveSelf();
      }
      guidance_node_ = null;
    }
  }

  private void SetNavballTexture(UnityEngine.Texture texture) {
    navball_material_?.SetTexture("_MainTexture", texture);
  }

  private void RenderNavball(Vessel active_vessel) {
    if (navball_ == null) {
      navball_ = (KSP.UI.Screens.Flight.NavBall)FindObjectOfType(
          typeof(KSP.UI.Screens.Flight.NavBall));
    }
    if (navball_material_ == null && navball_ != null) {
      navball_material_ = navball_.navBall.GetComponent<UnityEngine.Renderer>()?.material;
    }
    if (navball_material_ == null) {
      return;
    }

    if (compass_navball_texture_ == null) {
      compass_navball_texture_ = navball_material_.GetTexture("_MainTexture");
    }

    if (!PluginRunning()) {
      return;
    }

    var target_vessel = TargetVessel();
    if (plotting_frame_selector_.target_vessel != target_vessel) {
       plotting_frame_selector_.target_vessel = target_vessel;
       if (plotting_frame_selector_.target_frame_selected &&
           target_vessel == null) {
         // The target is no longer manageable.
         plotting_frame_selector_.UnsetTargetFrame();
       } else if (FlightGlobals.speedDisplayMode ==
                      FlightGlobals.SpeedDisplayModes.Target &&
                  !plotting_frame_selector_.target_frame_selected) {
         // The navball was in target mode, but the target was not known to
         // Principia; now that it is, switch the reference frame accordingly.
         plotting_frame_selector_.SetTargetFrame();
       } else if (plotting_frame_selector_.target_frame_selected) {
         // The target changed.
         navball_changed_ = true;
         map_node_pool_.Clear();
         planetarium_camera_adjuster_.should_transfer_camera_coordinates = true;
       }
    }

    // Orient the ball.
    navball_.navBall.rotation =
        (UnityEngine.QuaternionD)navball_.attitudeGymbal *  // sic.
        (UnityEngine.QuaternionD)plugin_.NavballOrientation(
            (XYZ)Planetarium.fetch.Sun.position,
            (XYZ)active_vessel.ReferenceTransform.position);

    if (previous_display_mode_ != FlightGlobals.speedDisplayMode) {
      navball_changed_ = true;
      previous_display_mode_ = FlightGlobals.speedDisplayMode;
      // The navball speed display mode was changed, change the reference
      // frame accordingly.
      switch (FlightGlobals.speedDisplayMode) {
        case FlightGlobals.SpeedDisplayModes.Surface:
          plotting_frame_selector_.SetToSurfaceFrame();
          break;
        case FlightGlobals.SpeedDisplayModes.Orbit:
          plotting_frame_selector_.SetToOrbitalFrame();
          break;
        case FlightGlobals.SpeedDisplayModes.Target:
          if (target_vessel != null) {
            plotting_frame_selector_.SetTargetFrame();
          }
          break;
      }
    }

    if (navball_changed_ && previous_display_mode_ != null) {
      // Texture the ball.
      navball_changed_ = false;
      if (plotting_frame_selector_.target_frame_selected) {
        SetNavballTexture(target_navball_texture_);
        if (FlightGlobals.speedDisplayMode !=
            FlightGlobals.SpeedDisplayModes.Target) {
          FlightGlobals.SetSpeedMode(FlightGlobals.SpeedDisplayModes.Target);
        }
      } else {
        // If we are targeting an unmanageable vessel, keep the navball in
        // target mode; otherwise, put it in the mode that reflects the
        // plotting frame.
        if (!(FlightGlobals.speedDisplayMode ==
                  FlightGlobals.SpeedDisplayModes.Target &&
              plotting_frame_selector_.target_vessel == null)) {
          if (plotting_frame_selector_.IsSurfaceFrame()) {
            if (FlightGlobals.speedDisplayMode !=
                FlightGlobals.SpeedDisplayModes.Surface) {
              FlightGlobals.SetSpeedMode(
                  FlightGlobals.SpeedDisplayModes.Surface);
            }
          } else {
            if (FlightGlobals.speedDisplayMode !=
                FlightGlobals.SpeedDisplayModes.Orbit) {
              FlightGlobals.SetSpeedMode(FlightGlobals.SpeedDisplayModes.Orbit);
            }
          }
        }
        switch (plotting_frame_selector_.frame_type) {
          case BODY_SURFACE:
            SetNavballTexture(surface_navball_texture_);
            break;
          case BODY_CENTRED_NON_ROTATING:
            SetNavballTexture(inertial_navball_texture_);
            break;
          case BARYCENTRIC_ROTATING:
          case ROTATING_PULSATING:
            SetNavballTexture(barycentric_navball_texture_);
            break;
          case BODY_CENTRED_PARENT_DIRECTION:
            SetNavballTexture(body_direction_navball_texture_);
            break;
        }
      }
    }

    RenderGuidance(active_vessel);
  }

  private void SetNavballVector(UnityEngine.Transform vector,
                                Vector3d direction) {
    if (vector == null) {
      return;
    }
    vector.localPosition = (UnityEngine.QuaternionD)navball_.attitudeGymbal *
                           direction *
                           navball_.VectorUnitScale;
    bool is_visible = vector.localPosition.z > navball_.VectorUnitCutoff;
    if (vector.gameObject.activeSelf != is_visible) {
      vector.gameObject.SetActive(is_visible);
    }
    if (is_visible) {
      if (!navball_vector_materials_.TryGetValue(vector, out var mat) || mat == null) {
        if (!navball_vector_renderers_.TryGetValue(vector, out var renderer) || renderer == null) {
          renderer = vector.GetComponent<UnityEngine.MeshRenderer>();
          if (renderer != null) {
            navball_vector_renderers_[vector] = renderer;
          }
        }
        if (renderer != null) {
          mat = renderer.material;
          navball_vector_materials_[vector] = mat;
        }
      }
      if (mat != null) {
        mat.SetFloat(
            "_Opacity",
            UnityEngine.Mathf.Clamp01(UnityEngine.Vector3.Dot(
                                          vector.localPosition.normalized,
                                          UnityEngine.Vector3.forward)));
      }
    }
  }

  private void RotateGalaxyCube() {
    if (PluginRunning()) {
      var initial_rotation =
          UnityEngine.QuaternionD.Inverse(Planetarium.Rotation) *
          (UnityEngine.QuaternionD)GalaxyCubeControl.Instance.transform.
              rotation;
      GalaxyCubeControl.Instance.transform.rotation =
          (UnityEngine.QuaternionD)plugin_.CelestialSphereRotation() *
          initial_rotation;
    }
  }

  private void RemoveStockTrajectoriesIfNeeded(CelestialBody celestial) {
    if (celestial.orbitDriver == null) {
      return;
    }
    celestial.orbitDriver.Renderer.drawMode =
        main_window_.display_patched_conics &&
        plotting_frame_selector_.frame_type == BODY_CENTRED_NON_ROTATING &&
        plotting_frame_selector_.Centre() == celestial.orbit.referenceBody
            ? OrbitRenderer.DrawMode.REDRAW_AND_RECALCULATE
            : OrbitRenderer.DrawMode.OFF;
  }

  private void RemoveStockTrajectoriesIfNeeded(Vessel vessel) {
    if (vessel.patchedConicRenderer != null) {
      vessel.patchedConicRenderer.relativityMode =
          PatchRendering.RelativityMode.RELATIVE;
    }

    if ((main_window_.display_patched_conics  &&
         plotting_frame_selector_.frame_type == BODY_CENTRED_NON_ROTATING &&
         plotting_frame_selector_.Centre() ==
            vessel.orbitDriver.orbit.referenceBody) ||
        !is_manageable(vessel)) {
      vessel.orbitDriver.Renderer.drawMode =
          vessel.PatchedConicsAttached
              ? OrbitRenderer.DrawMode.OFF
              : OrbitRenderer.DrawMode.REDRAW_AND_RECALCULATE;
      vessel.orbitDriver.Renderer.drawIcons =
          ((vessel.isActiveVessel ||
            vessel == FlightGlobals.ActiveVessel?.targetObject as Vessel) &&
           !vessel.PatchedConicsAttached)
              ? OrbitRenderer.DrawIcons.ALL
              : OrbitRenderer.DrawIcons.OBJ;

      if (vessel.patchedConicRenderer != null &&
          !vessel.patchedConicRenderer.enabled) {
        vessel.patchedConicRenderer.enabled = true;
      }
    } else {
      vessel.orbitDriver.Renderer.drawMode = OrbitRenderer.DrawMode.OFF;
      vessel.orbitDriver.Renderer.drawIcons = OrbitRenderer.DrawIcons.OBJ;

      // vessel.patchedConicRenderer may be null when in career mode, if
      // patched conics have yet to be unlocked.  In that case there is
      // nothing to remove.  Note that Principia doesn't care about unlocks
      // and always displays all the information it can.
      if (vessel.patchedConicRenderer != null) {
        vessel.patchedConicRenderer.enabled = false;
        foreach (PatchRendering patch_rendering in vessel.patchedConicRenderer.
            patchRenders) {
          patch_rendering.DestroyUINodes();
          patch_rendering.DestroyVector();
        }
        foreach (PatchRendering patch_rendering in vessel.patchedConicRenderer.
            flightPlanRenders) {
          patch_rendering.DestroyUINodes();
          patch_rendering.DestroyVector();
        }
        foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes) {
          node.DetachGizmo();
          if (node.scaledSpaceTarget) {
            MapView.MapCamera.RemoveTarget(node.scaledSpaceTarget);
            node.scaledSpaceTarget.Terminate();
          }
        }
      }

      if (vessel.orbitTargeter != null) {
        vessel.orbitTargeter.enabled = false;
      }
    }
  }

  private void OnCelestialNodeClick(KSP.UI.Screens.Mapview.MapNode node,
                                    Mouse.Buttons buttons) {
    if (buttons == Mouse.Buttons.Left) {
      main_window_.SelectTargetCelestial(node.mapObject);
    }
  }

  private void OnVesselNodeClick(KSP.UI.Screens.Mapview.MapNode node,
                                 Mouse.Buttons buttons) {
    main_window_.SelectActiveVesselTarget(node.mapObject,
                                          set_planetarium_camera : buttons ==
                                            Mouse.Buttons.Left);
  }

  private void HandleMapViewClicks() {
    if (InputLockManager.IsUnlocked(ControlTypes.MAP_UI) &&
        !UnityEngine.EventSystems.EventSystem.current.
            IsPointerOverGameObject() &&
        Mouse.Left.GetClick() &&
        !ManeuverGizmo.HasMouseFocus &&
        !main_window_.selecting_active_vessel_target) {
      var ray = PlanetariumCamera.Camera.ScreenPointToRay(
          UnityEngine.Input.mousePosition);
      foreach (var celestial in FlightGlobals.Bodies) {
        double scaled_distance = Vector3d.
            Cross(ray.direction,
                  ScaledSpace.LocalToScaledSpace(celestial.position) -
                  ray.origin).magnitude;
        if (scaled_distance * ScaledSpace.ScaleFactor < celestial.Radius) {
          main_window_.SelectTargetCelestial(celestial.MapObject);
        }
      }
    }
  }

  private void RenderTrajectories() {
    if (!PluginRunning()) {
      return;
    }
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;
    bool should_cleanup_stock = current_realtime - last_stock_trajectories_cleanup_time_ > 1.0;
    if (should_cleanup_stock) {
      last_stock_trajectories_cleanup_time_ = current_realtime;
      var bodies = FlightGlobals.Bodies;
      for (int i = 0; i < bodies.Count; ++i) {
        var celestial = bodies[i];
        var uiNode = celestial?.MapObject?.uiNode;
        if (uiNode != null) {
          if (hooked_map_nodes_.Add(uiNode)) {
            uiNode.OnClick += OnCelestialNodeClick;
          }
          RemoveStockTrajectoriesIfNeeded(celestial);
        }
      }
      var vessels = FlightGlobals.Vessels;
      for (int i = 0; i < vessels.Count; ++i) {
        var vessel = vessels[i];
        var uiNode = vessel?.mapObject?.uiNode;
        if (uiNode != null) {
          if (hooked_map_nodes_.Add(uiNode)) {
            uiNode.OnClick += OnVesselNodeClick;
          }
          RemoveStockTrajectoriesIfNeeded(vessel);
        }
      }
    }
    string main_vessel_guid = GetVesselGuid(PredictedVessel());
    if (MapView.MapIsEnabled) {
      XYZ sun_world_position = (XYZ)Planetarium.fetch.Sun.position;
      // High-precision adaptive subdivision:
      // Smaller multiplier = finer angular tolerance in C++ Hermite subdivision = silky smooth curvature without polygonal kinks or folded lines ("折线").
      // Because meshes are cached in GPU VRAM and drawn via DrawMesh, higher vertex counts have 0 ms CPU cost!
      double resolution_multiplier = 0.75;
      using (DisposablePlanetarium planetarium =
             GLLines.NewPlanetarium(plugin_, sun_world_position, resolution_multiplier)) {
        plotter_.PlotTrajectories(planetarium,
                                  main_vessel_guid,
                                  main_window_.history_length,
                                  prediction_collision_?.t,
                                  flight_plan_collision_?.t);
        plotter_.PlotEquipotentials(planetarium);
      }
    }
  }

  private void RenderTrajectoryCollisions(string vessel_guid,
                                          out TQP? prediction_collision,
                                          out TQP? flight_plan_collision) {
    if (!celestial_terrains_were_validated_) {
      var random = new Random();
      foreach (CelestialBody celestial in FlightGlobals.Bodies) {
        ValidateCelestialTerrain(celestial, random);
      }
      celestial_terrains_were_validated_ = true;
    }

    if (vessel_guid != null &&
        MapView.MapIsEnabled &&
        plotting_frame_selector_.Centre() != null) {
      var centre = plotting_frame_selector_.Centre();
      if (plotting_frame_selector_.IsSurfaceFrame()) {
        bool should_recompute =
            MapMarkerEventGovernor.CollisionDirty ||
            vessel_guid != last_collision_vessel_guid_;

        if (should_recompute) {
          last_collision_vessel_guid_ = vessel_guid;
          MapMarkerEventGovernor.CollisionDirty = false;

          prediction_collision_ = RenderedPredictionCollision(vessel_guid, centre);
          flight_plan_collision_ = plugin_.FlightPlanExists(vessel_guid)
              ? RenderedFlightPlanCollision(vessel_guid, centre)
              : null;

          if (prediction_collision_.HasValue) {
            single_collision_marker_array_[0] = prediction_collision_.Value;
            map_node_pool_.RenderMarkers(single_collision_marker_array_,
                                         new MapNodePool.Provenance(
                                             vessel_guid,
                                             MapNodePool.NodeSource.Prediction,
                                             MapObject.ObjectType.PatchTransition),
                                         plotting_frame_selector_);
          }
          if (flight_plan_collision_.HasValue) {
            single_collision_marker_array_[0] = flight_plan_collision_.Value;
            map_node_pool_.RenderMarkers(single_collision_marker_array_,
                                         new MapNodePool.Provenance(
                                             vessel_guid,
                                             MapNodePool.NodeSource.FlightPlan,
                                             MapObject.ObjectType.PatchTransition),
                                         plotting_frame_selector_);
          }
        }

        prediction_collision = prediction_collision_;
        flight_plan_collision = flight_plan_collision_;
        return;
      }
    }

    prediction_collision_ = null;
    flight_plan_collision_ = null;
    prediction_collision = null;
    flight_plan_collision = null;
  }

  private void RenderManœuvreMarkers() {
    if (!PluginRunning()) {
      return;
    }
    string main_vessel_guid = GetVesselGuid(PredictedVessel());
    if (MapView.MapIsEnabled) {
      var sun_world_position = (XYZ)Planetarium.fetch.Sun.position;
      double resolution_multiplier = 1.0;
      using (DisposablePlanetarium planetarium =
        GLLines.NewPlanetarium(plugin_, sun_world_position, resolution_multiplier)) {
        int number_of_rendered_manœuvres = 0;
        if (main_vessel_guid != null &&
            plugin_.FlightPlanExists(main_vessel_guid)) {
          int number_of_anomalous_manœuvres =
              plugin_.FlightPlanNumberOfAnomalousManoeuvres(main_vessel_guid);
          int number_of_manœuvres =
              plugin_.FlightPlanNumberOfManoeuvres(main_vessel_guid);
          int number_of_segments =
              plugin_.FlightPlanNumberOfSegments(main_vessel_guid);
          for (int i = 0; i < number_of_segments; ++i) {
            bool is_burn = i % 2 == 1;
            if (is_burn) {
              using (DisposableIterator rendered_manœuvre =
                     plugin_.FlightPlanRenderedManoeuvreInitialDegreesOfFreedom(
                         main_vessel_guid,
                         sun_world_position,
                         i)) {
                if (rendered_manœuvre.IteratorAtEnd()) {
                  Log.Info("Skipping segment " + i);
                  continue;
                }
                double time_at_start =
                    rendered_manœuvre.IteratorGetDistinguishedPointsTime();
                var position_at_start =
                    (Vector3d)rendered_manœuvre.
                        IteratorGetDistinguishedPointsXYZ();
                if (flight_plan_collision_ == null ||
                    time_at_start <= flight_plan_collision_.Value.t) {
                  int manœuvre_index = i / 2;
                  if (manœuvre_index <
                      number_of_manœuvres - number_of_anomalous_manœuvres) {
                    NavigationManoeuvreFrenetTrihedron trihedron =
                        plugin_.FlightPlanGetManoeuvreFrenetTrihedron(
                            main_vessel_guid,
                            manœuvre_index);
                    if (number_of_rendered_manœuvres >=
                        manœuvre_marker_pool_.Count) {
                      manœuvre_marker_pool_.Add(
                          ManœuvreMarker.Create(main_window_, flight_planner_));
                    }
                    var initial_plotted_velocity =
                        (Vector3d)plugin_.
                            FlightPlanGetManoeuvreInitialPlottedVelocity(
                                main_vessel_guid,
                                manœuvre_index);
                    manœuvre_marker_pool_[number_of_rendered_manœuvres].Render(
                        manœuvre_index,
                        world_position: position_at_start,
                        initial_plotted_velocity,
                        trihedron);
                    ++number_of_rendered_manœuvres;
                  }
                }
              }
            }
          }
        }
        // This cleanup must occur even if there is no flight plan or no
        // predicted vessel. In the tracking station, one may switch from a
        // vessel with a plan to one without.  One may also delete a vessel.
        for (int i = number_of_rendered_manœuvres;
             i < manœuvre_marker_pool_.Count;
             ++i) {
          // Since markers are used sequentially, all subsequent ones will
          // be in the disabled state.
          if (manœuvre_marker_pool_[i].is_disabled) {
            break;
          }
          manœuvre_marker_pool_[i].Disable();
        }
      }
    }
  }

  private TQP? RenderedPredictionCollision(string vessel_guid,
                                           CelestialBody centre) {
    var executor = plugin_.CollisionNewPredictionExecutor(
        celestial_index: centre.flightGlobalsIndex,
        sun_world_position: (XYZ)Planetarium.fetch.Sun.position,
        // TODO(phl): This should be much larger, if it is limited at all.
        max_points: MapNodePool.MaxNodesPerProvenance,
        vessel_guid: vessel_guid);

    for (;;) {
      bool more = executor.CollisionGetLatitudeLongitude(
          out double trial_latitude,
          out double trial_longitude);
      if (!more) {
        if (plugin_.CollisionDeleteExecutor(ref executor, out TQP collision)) {
          return collision;
        } else {
          return null;
        }
      }
      executor.CollisionSetRadius(RadiusAt(centre,
                                           latitude: trial_latitude,
                                           longitude: trial_longitude));
    }
  }

  private TQP? RenderedFlightPlanCollision(string vessel_guid,
                                           CelestialBody centre) {
    var executor = plugin_.CollisionNewFlightPlanExecutor(
        celestial_index: centre.flightGlobalsIndex,
        sun_world_position: (XYZ)Planetarium.fetch.Sun.position,
        // TODO(phl): This should be much larger, if it is limited at all.
        max_points: MapNodePool.MaxNodesPerProvenance,
        vessel_guid: vessel_guid);

    for (;;) {
      bool more = executor.CollisionGetLatitudeLongitude(
          out double trial_latitude,
          out double trial_longitude);
      if (!more) {
        if (plugin_.CollisionDeleteExecutor(ref executor, out TQP collision)) {
          return collision;
        } else {
          return null;
        }
      }
      executor.CollisionSetRadius(RadiusAt(centre,
                                           latitude: trial_latitude,
                                           longitude: trial_longitude));
    }
  }

  private void RenderPredictionApsides(string vessel_guid,
                                       TQP? prediction_collision,
                                       XYZ sun_world_position) {
    if (vessel_guid == null || !plugin_.HasVessel(vessel_guid) ||
        plotting_frame_selector_.target_frame_selected) {
      return;
    }
    var centre = plotting_frame_selector_.Centre();
    if (centre == null) {
      return;
    }
    int centre_index = centre.flightGlobalsIndex;
    plugin_.RenderedPredictionApsides(vessel_guid,
                                      prediction_collision?.t,
                                      centre_index,
                                      sun_world_position,
                                      MapNodePool.MaxNodesPerProvenance,
                                      out DisposableIterator apoapsis_iterator,
                                      out DisposableIterator periapsis_iterator);
    using (apoapsis_iterator) {
      map_node_pool_.RenderMarkers(
          apoapsis_iterator.DistinguishedPoints(),
          new MapNodePool.Provenance(vessel_guid,
                                     MapNodePool.NodeSource.Prediction,
                                     MapObject.ObjectType.Apoapsis),
          plotting_frame_selector_);
    }
    using (periapsis_iterator) {
      map_node_pool_.RenderMarkers(
          periapsis_iterator.DistinguishedPoints(),
          new MapNodePool.Provenance(vessel_guid,
                                     MapNodePool.NodeSource.Prediction,
                                     MapObject.ObjectType.Periapsis),
          plotting_frame_selector_);
    }
  }

  private void RenderPredictionNodes(string vessel_guid,
                                     TQP? prediction_collision,
                                     XYZ sun_world_position) {
    if (vessel_guid == null || !plugin_.HasVessel(vessel_guid)) {
      return;
    }
    if (plotting_frame_selector_.target_frame_selected &&
        TargetVessel() != null) {
      plugin_.RenderedPredictionNodes(vessel_guid,
                                      t_max: null,
                                      sun_world_position,
                                      MapNodePool.MaxNodesPerProvenance,
                                      out DisposableIterator ascending_nodes_iterator,
                                      out DisposableIterator descending_nodes_iterator);
      using (ascending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            ascending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.Prediction,
                                       MapObject.ObjectType.AscendingNode),
            plotting_frame_selector_);
      }
      using (descending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            descending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.Prediction,
                                       MapObject.ObjectType.DescendingNode),
            plotting_frame_selector_);
      }

      plugin_.RenderedPredictionClosestApproaches(
          vessel_guid,
          sun_world_position,
          MapNodePool.MaxNodesPerProvenance,
          out DisposableIterator approaches_iterator);
      using (approaches_iterator) {
        map_node_pool_.RenderMarkers(
            approaches_iterator.DistinguishedPoints(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.Prediction,
                                       MapObject.ObjectType.ApproachIntersect),
            plotting_frame_selector_);
      }
    } else {
      plugin_.RenderedPredictionNodes(vessel_guid,
                                      prediction_collision?.t,
                                      sun_world_position,
                                      MapNodePool.MaxNodesPerProvenance,
                                      out DisposableIterator ascending_nodes_iterator,
                                      out DisposableIterator descending_nodes_iterator);
      using (ascending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            ascending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.Prediction,
                                       MapObject.ObjectType.AscendingNode),
            plotting_frame_selector_);
      }
      using (descending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            descending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.Prediction,
                                       MapObject.ObjectType.DescendingNode),
            plotting_frame_selector_);
      }
    }
  }

  private void RenderPredictionMarkers(string vessel_guid,
                                       TQP? prediction_collision,
                                       XYZ sun_world_position) {
    RenderPredictionApsides(vessel_guid, prediction_collision, sun_world_position);
    RenderPredictionNodes(vessel_guid, prediction_collision, sun_world_position);
  }

  private void RenderFlightPlanApsides(string vessel_guid,
                                       TQP? flight_plan_collision,
                                       XYZ sun_world_position) {
    if (vessel_guid == null || !plugin_.HasVessel(vessel_guid) ||
        !plugin_.FlightPlanExists(vessel_guid) ||
        RollingEphemerisDriver.ShouldSuppressPlot(vessel_guid) ||
        plotting_frame_selector_.target_frame_selected) {
      return;
    }
    var centre = plotting_frame_selector_.Centre();
    if (centre == null) {
      return;
    }
    int centre_index = centre.flightGlobalsIndex;
    plugin_.FlightPlanRenderedApsides(vessel_guid,
                                      flight_plan_collision?.t,
                                      centre_index,
                                      sun_world_position,
                                      MapNodePool.MaxNodesPerProvenance,
                                      out DisposableIterator apoapsis_iterator,
                                      out DisposableIterator periapsis_iterator);
    using (apoapsis_iterator) {
      map_node_pool_.RenderMarkers(
          apoapsis_iterator.DistinguishedPoints(),
          new MapNodePool.Provenance(vessel_guid,
                                     MapNodePool.NodeSource.FlightPlan,
                                     MapObject.ObjectType.Apoapsis),
          plotting_frame_selector_);
    }
    using (periapsis_iterator) {
      map_node_pool_.RenderMarkers(
          periapsis_iterator.DistinguishedPoints(),
          new MapNodePool.Provenance(vessel_guid,
                                     MapNodePool.NodeSource.FlightPlan,
                                     MapObject.ObjectType.Periapsis),
          plotting_frame_selector_);
    }
  }

  private void RenderFlightPlanNodes(string vessel_guid,
                                     TQP? flight_plan_collision,
                                     XYZ sun_world_position) {
    if (vessel_guid == null || !plugin_.HasVessel(vessel_guid) ||
        !plugin_.FlightPlanExists(vessel_guid) ||
        RollingEphemerisDriver.ShouldSuppressPlot(vessel_guid)) {
      return;
    }
    if (plotting_frame_selector_.target_frame_selected &&
        TargetVessel() != null) {
      plugin_.FlightPlanRenderedNodes(vessel_guid,
                                      t_max: null,
                                      sun_world_position,
                                      MapNodePool.MaxNodesPerProvenance,
                                      out DisposableIterator ascending_nodes_iterator,
                                      out DisposableIterator descending_nodes_iterator);
      using (ascending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            ascending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.FlightPlan,
                                       MapObject.ObjectType.AscendingNode),
            plotting_frame_selector_);
      }
      using (descending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            descending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.FlightPlan,
                                       MapObject.ObjectType.DescendingNode),
            plotting_frame_selector_);
      }

      plugin_.FlightPlanRenderedClosestApproaches(
          vessel_guid,
          sun_world_position,
          MapNodePool.MaxNodesPerProvenance,
          out DisposableIterator approaches_iterator);
      using (approaches_iterator) {
        map_node_pool_.RenderMarkers(
            approaches_iterator.DistinguishedPoints(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.FlightPlan,
                                       MapObject.ObjectType.ApproachIntersect),
            plotting_frame_selector_);
      }
    } else {
      plugin_.FlightPlanRenderedNodes(vessel_guid,
                                      flight_plan_collision?.t,
                                      sun_world_position,
                                      MapNodePool.MaxNodesPerProvenance,
                                      out DisposableIterator ascending_nodes_iterator,
                                      out DisposableIterator descending_nodes_iterator);
      using (ascending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            ascending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.FlightPlan,
                                       MapObject.ObjectType.AscendingNode),
            plotting_frame_selector_);
      }
      using (descending_nodes_iterator) {
        map_node_pool_.RenderNodes(
            descending_nodes_iterator.Nodes(),
            new MapNodePool.Provenance(vessel_guid,
                                       MapNodePool.NodeSource.FlightPlan,
                                       MapObject.ObjectType.DescendingNode),
            plotting_frame_selector_);
      }
    }
  }

  private void RenderFlightPlanMarkers(string vessel_guid,
                                       TQP? flight_plan_collision,
                                       XYZ sun_world_position) {
    RenderFlightPlanApsides(vessel_guid, flight_plan_collision, sun_world_position);
    RenderFlightPlanNodes(vessel_guid, flight_plan_collision, sun_world_position);
  }

  private void Cleanup() {
    HarvestVesselsAndApplyOnRails();
    if (map_renderer_ != null) {
      UnityEngine.Object.Destroy(map_renderer_);
      map_renderer_ = null;
    }
    map_node_pool_?.Clear();
    LockClearing?.Invoke();
    Interface.DeletePlugin(ref plugin_);
    previous_display_mode_ = null;
    navball_changed_ = true;
    plotter_?.Dispose();
    GpuFlightPlanOptimizer.ReleaseBuffers();
    GpuTrajectoryRenderer.ReleaseBuffers();
    PrincipiaGPGPUContext.Dispose();
    VacuumBypassGovernor.Dispose();
    NativeStateSnapshotChannel.Clear();
    rolling_ephemeris_driver_?.Reset();
    rolling_ephemeris_driver_ = null;
    hooked_map_nodes_?.Clear();
    ReleaseAllForceLists();
    intrinsic_forces_pool_.Clear();
    navball_vector_renderers_.Clear();
    navball_vector_materials_.Clear();
    MapMarkerEventGovernor.Reset();

    collision_reporters_?.Clear();
    celestial_parent_cache_?.Clear();
    last_main_body_for_rotation_ = null;
    vessel_guid_cache_?.Clear();
    collided_vessels_cache_?.Clear();
    topological_bodies_ = null;
    scratch_orbit_ = null;
    if (celestial_last_update_realtime_ != null) {
      Array.Clear(celestial_last_update_realtime_, 0, celestial_last_update_realtime_.Length);
    }
    if (celestial_last_update_frame_ != null) {
      Array.Clear(celestial_last_update_frame_, 0, celestial_last_update_frame_.Length);
    }

    // Load the flags.
    Interface.ClearFlags();
    if (GameDatabase.Instance != null) {
      ConfigNode.ValueList flags = GameDatabase.Instance.
          GetAtMostOneNode(principia_flags)?.values;
      if (flags != null) {
        foreach (ConfigNode.Value flag in flags) {
          Interface.SetFlag(flag.name, flag.value);
        }
      }
    }
  }

  private void UpdatePlottingFrame(PlottingFrameParameters frame_parameters,
                                   Vessel target_vessel) {
    if (!PluginRunning()) {
      return;
    }
    if (target_vessel != null) {
      // TODO(egg): We should use the analyser to pick the reference body.
      plugin_.SetTargetVessel(
          GetVesselGuid(target_vessel),
          target_vessel.orbit.referenceBody.flightGlobalsIndex);
    } else {
      plugin_.ClearTargetVessel();
      plugin_.SetPlottingFrame(frame_parameters);
      SpscCommandBus.TryPostCommand(CommandPacket.ForCelestial(CommandType.SetPlottingFrame, 0));
    }
    navball_changed_ = true;
    reset_rsas_target_ = true;
    planetarium_camera_adjuster_.should_transfer_camera_coordinates = true;
    map_node_pool_.Clear();
    MapMarkerEventGovernor.InvalidateAll();
    plotter_?.InvalidateAll();
  }

  private void LoadDrawStyles(ConfigNode draw_styles) {
    if (draw_styles == null) {
      return;
    }
    ConfigNode history_parameters = draw_styles.GetAtMostOneNode("history");
    history_parameters?.GetDrawStyle(out history_colour, out history_style);
    ConfigNode prediction_parameters =
        draw_styles.GetAtMostOneNode("prediction");
    prediction_parameters?.GetDrawStyle(out prediction_colour,
                                        out prediction_style);
    ConfigNode flight_plan_parameters =
        draw_styles.GetAtMostOneNode("flight_plan");
    flight_plan_parameters?.GetDrawStyle(out flight_plan_colour,
                                         out flight_plan_style);
    ConfigNode burn_parameters = draw_styles.GetAtMostOneNode("burn");
    burn_parameters?.GetDrawStyle(out burn_colour, out burn_style);
    ConfigNode target_history_parameters =
        draw_styles.GetAtMostOneNode("target_history");
    target_history_parameters?.GetDrawStyle(out target_history_colour,
                                            out target_history_style);
    ConfigNode target_prediction_parameters =
        draw_styles.GetAtMostOneNode("target_prediction");
    target_prediction_parameters?.GetDrawStyle(out target_prediction_colour,
                                               out target_prediction_style);
  }

  private static void InitializeIntegrators(IntPtr plugin,
                                            ConfigNode numerics_blueprint) {
    if (numerics_blueprint == null) {
      return;
    }

    ConfigNode downsampling_parameters =
        numerics_blueprint.GetAtMostOneNode("downsampling");
    if (downsampling_parameters != null) {
      plugin.InitializeDownsamplingParameters(
          ConfigNodeParsers.NewConfigurationDownsamplingParameters(
              downsampling_parameters));
    }

    ConfigNode ephemeris_parameters =
        numerics_blueprint.GetAtMostOneNode("ephemeris");
    if (ephemeris_parameters != null) {
      plugin.InitializeEphemerisParameters(
          ConfigNodeParsers.NewConfigurationAccuracyParameters(
              ephemeris_parameters),
          ConfigNodeParsers.NewConfigurationFixedStepParameters(
              ephemeris_parameters));
    }

    ConfigNode history_parameters =
        numerics_blueprint.GetAtMostOneNode("history");
    if (history_parameters != null) {
      plugin.InitializeHistoryParameters(
          ConfigNodeParsers.NewConfigurationFixedStepParameters(
              history_parameters));
    }

    ConfigNode psychohistory_parameters =
        numerics_blueprint.GetAtMostOneNode("psychohistory");
    if (psychohistory_parameters != null) {
      plugin.InitializePsychohistoryParameters(
          ConfigNodeParsers.NewConfigurationAdaptiveStepParameters(
              psychohistory_parameters));
    }
  }

  private void ResetPlugin() {
    try {
      Cleanup();
      RemoveBuggyTidalLocking();
      Dictionary<string, ConfigNode> name_to_gravity_model = null;
      ConfigNode gravity_model = GameDatabase.Instance.GetAtMostOneNode(
          principia_gravity_model_config_name);
      ConfigNode initial_state = GameDatabase.Instance.GetAtMostOneNode(
          principia_initial_state_config_name);
      ConfigNode numerics_blueprint = GameDatabase.Instance.GetAtMostOneNode(
          principia_numerics_blueprint_config_name);
      if (gravity_model != null) {
        name_to_gravity_model = gravity_model.GetNodes("body").
            ToDictionary(node => node.GetUniqueValue("name"));
      }
      if (initial_state != null) {
        if (name_to_gravity_model == null) {
          Log.Fatal("Cartesian config without gravity models");
        }
        // Note that `game_epoch` is not in the astronomy proto, as it is
        // KSP-specific: it is the `Instant` corresponding to KSP's
        // UniversalTime 0.  It is passed as an argument to
        // `generate_configuration`.
        plugin_ = Interface.NewPlugin(
            initial_state.GetUniqueValue("game_epoch"),
            initial_state.GetUniqueValue("solar_system_epoch"),
            Planetarium.InverseRotAngle);
        InitializeIntegrators(plugin_, numerics_blueprint);
        var name_to_initial_state = initial_state.GetNodes("body").
            ToDictionary(node => node.GetUniqueValue("name"));
        BodyProcessor insert_body = body => {
          Log.Info("Inserting " + body.name + "...");
          if (!name_to_gravity_model.TryGetValue(
                  body.name,
                  out ConfigNode body_gravity_model)) {
            Log.Fatal("missing gravity model for " + body.name);
          }
          if (!name_to_initial_state.TryGetValue(
                  body.name,
                  out ConfigNode body_initial_state)) {
            Log.Fatal("missing Cartesian initial state for " + body.name);
          }
          int? parent_index = body.orbit?.referenceBody.flightGlobalsIndex;
          // GetUniqueValue resp. GetAtMostOneValue corresponding to required
          // resp. optional in principia.serialization.GravityModel.Body.
          var body_parameters =
              ConfigNodeParsers.NewCartesianBodyParameters(
                  body,
                  body_gravity_model);
          // GetUniqueValue since these are all required fields in
          // principia.serialization.InitialState.Cartesian.Body.
          plugin_.InsertCelestialAbsoluteCartesian(
              celestial_index : body.flightGlobalsIndex,
              parent_index    : parent_index,
              body_parameters : body_parameters,
              x               : body_initial_state.GetUniqueValue("x"),
              y               : body_initial_state.GetUniqueValue("y"),
              z               : body_initial_state.GetUniqueValue("z"),
              vx              : body_initial_state.GetUniqueValue("vx"),
              vy              : body_initial_state.GetUniqueValue("vy"),
              vz              : body_initial_state.GetUniqueValue("vz"));
        };
        insert_body(Planetarium.fetch.Sun);
        ApplyToBodyTree(insert_body);
        plugin_.EndInitialization();
      } else {
        // We create the plugin at J2000 (a.k.a. Instant{}), rather than
        // `Planetarium.GetUniversalTime()`, in order to get a deterministic
        // initial state.
        plugin_ = Interface.NewPlugin(game_epoch: "JD2451545",
                                      solar_system_epoch: "JD2451545",
                                      Planetarium.InverseRotAngle);
        InitializeIntegrators(plugin_, numerics_blueprint);
        BodyProcessor insert_body = body => {
          Log.Info("Inserting " + body.name + "...");
          ConfigNode body_gravity_model = null;
          if (name_to_gravity_model?.TryGetValue(
                  body.name,
                  out body_gravity_model) == true) {
            Log.Info("using custom gravity model");
          }
          Orbit orbit = unmodified_orbits_.GetValueOrNull(body);
          body.initialRotation = unmodified_initial_rotations_[body];
          var body_parameters =
              ConfigNodeParsers.NewKeplerianBodyParameters(
                  body,
                  body_gravity_model);
          plugin_.InsertCelestialJacobiKeplerian(
              celestial_index    : body.flightGlobalsIndex,
              parent_index       : orbit?.referenceBody.flightGlobalsIndex,
              body_parameters    : body_parameters,
              keplerian_elements : orbit?.Elements());
        };
        insert_body(Planetarium.fetch.Sun);
        ApplyToBodyTree(insert_body);
        plugin_.EndInitialization();
      }
      if (Planetarium.GetUniversalTime() > plugin_.CurrentTime()) {
        // Make sure that the plugin has caught up with the game before existing
        // vessels are added.
        plugin_.AdvanceTime(Planetarium.GetUniversalTime(),
                            Planetarium.InverseRotAngle);
      }
      must_set_plotting_frame_ = true;
      rolling_ephemeris_driver_ = new RollingEphemerisDriver(plugin_, this);
    } catch (Exception e) {
      Log.Fatal($"Exception while resetting plugin: {e}");
    }
  }

  private void RemoveBuggyTidalLocking() {
    ApplyToBodyTree(body => body.tidallyLocked = false);
  }

  private void KeepPaused() {
    // If the game is paused in OnLoad while loading a quicksave, some of the
    // systems don’t get the message and the game continues in a half-paused
    // state.
    // Forcefully pause at every OnGUI call, regardless of whether the game is
    // paused according to FlightDriver.Pause, so as to ensure that it quickly
    // gets properly paused (and stays paused).
    FlightDriver.SetPause(pauseState: true);
  }
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
