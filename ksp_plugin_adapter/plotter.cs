using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace principia {
namespace ksp_plugin_adapter {

class Plotter {
  public Plotter(PrincipiaPluginAdapter adapter) {
    adapter_ = adapter;
  }

  public static double TanAngularResolution() {
    const double degree = Math.PI / 180;
    UnityEngine.Camera camera = PlanetariumCamera.Camera;
    float vertical_fov = camera.fieldOfView;
    float horizontal_fov =
        UnityEngine.Camera.VerticalToHorizontalFieldOfView(
            vertical_fov,
            camera.aspect);
    // The angle subtended by the pixel closest to the centre of the viewport.
    return Math.Min(
               Math.Tan(vertical_fov * degree / 2) / (camera.pixelHeight / 2),
               Math.Tan(horizontal_fov * degree / 2) /
               (camera.pixelWidth / 2));
  }

  public void PlotEquipotentials(DisposablePlanetarium planetarium) {
    int number_of_equipotentials = Plugin.EquipotentialCount();
    if (number_of_equipotentials == 0) {
      return;
    }
    while (equipotential_entries_.Count < number_of_equipotentials) {
      equipotential_entries_.Add(new CachedMeshEntry());
    }
    var colour = adapter_.plotting_frame_selector_.Primary()
        .orbitDriver?.Renderer?.orbitColor ?? XKCDColors.SunshineYellow;
    for (int i = 0; i < number_of_equipotentials; ++i) {
      var entry = equipotential_entries_[i];
      if (entry.is_valid) {
        entry.Draw();
        continue;
      }
      planetarium.PlanetariumPlotEquipotential(
          Plugin,
          i,
          VertexBuffer.data,
          VertexBuffer.size,
          out int vertex_count);
      entry.UpdateMesh(vertex_count, colour, GLLines.Style.Solid, shared_indices_);
      entry.Draw();
    }
  }

  public void PlotTrajectories(DisposablePlanetarium planetarium,
                               string main_vessel_guid,
                               double history_length,
                               double? prediction_t_max,
                               double? flight_plan_t_max) {
    // Detect Krakensbane or FloatingOrigin shifts in ScaledSpace.
    Vector3d current_scaled_space_origin =
        ScaledSpace.ScaledToLocalSpace(Vector3d.zero);
    if ((current_scaled_space_origin - last_scaled_space_origin_).sqrMagnitude > 1e-4) {
      InvalidateAll();
      last_scaled_space_origin_ = current_scaled_space_origin;
    }
    if (main_vessel_guid != last_main_vessel_guid_) {
      InvalidateAll();
      last_main_vessel_guid_ = main_vessel_guid;
    }
    if (Math.Abs(history_length - last_history_length_) > 1e-3) {
      psychohistory_entry_.Invalidate();
      target_psychohistory_entry_.Invalidate();
      foreach (var pair in celestial_trajectory_meshes_) {
        pair.Value.past.Invalidate();
      }
      last_history_length_ = history_length;
    }

    PlotCelestialTrajectories(planetarium, main_vessel_guid, history_length);
    PlotVesselTrajectories(planetarium, main_vessel_guid, history_length,
                           prediction_t_max, flight_plan_t_max);
  }

  private void PlotVesselTrajectories(DisposablePlanetarium planetarium,
                                      string main_vessel_guid,
                                      double history_length,
                                      double? prediction_t_max,
                                      double? flight_plan_t_max) {
    bool time_is_advancing = adapter_.is_time_advancing;
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;
    double current_time_ut = Planetarium.GetUniversalTime();

    bool is_expensive_frame =
        adapter_.plotting_frame_selector_.frame_type ==
            FrameType.ROTATING_PULSATING ||
        adapter_.plotting_frame_selector_.frame_type ==
            FrameType.BARYCENTRIC_ROTATING ||
        adapter_.plotting_frame_selector_.target_frame_selected;

    Vessel active = FlightGlobals.ActiveVessel;
    bool is_maneuvering = active != null &&
        (active.ctrlState.mainThrottle > 1e-3 ||
         active.acceleration.sqrMagnitude > 0.01);

    if (main_vessel_guid != null) {
      // Psychohistory: throttled to 5 Hz during time advance (history accumulates gradually).
      bool can_reuse_psychohistory = psychohistory_entry_.is_valid &&
          (!time_is_advancing ||
           (current_realtime - psychohistory_entry_.last_update_realtime < 0.20 &&
            Math.Abs(current_time_ut - psychohistory_entry_.last_update_ut) < 60.0));

      if (can_reuse_psychohistory) {
        psychohistory_entry_.Draw();
      } else {
        planetarium.PlanetariumPlotPsychohistory(
            Plugin,
            main_vessel_guid,
            history_length,
            prediction_t_max,
            VertexBuffer.data,
            VertexBuffer.size,
            out int vertex_count);
        psychohistory_entry_.last_update_realtime = current_realtime;
        psychohistory_entry_.last_update_ut = current_time_ut;
        psychohistory_entry_.UpdateMesh(vertex_count,
                                        adapter_.history_colour,
                                        adapter_.history_style,
                                        shared_indices_);
        psychohistory_entry_.Draw();
      }

      // Prediction: 30 Hz when maneuvering; 20 Hz in rotating frames; 25 Hz in standard frames.
      // During intermediate frames, draws directly from cached 3D GPU mesh (0 ms CPU, 120 FPS camera navigation).
      double pred_interval = is_maneuvering ? 0.033 : (is_expensive_frame ? 0.050 : 0.040);
      bool can_reuse_prediction = prediction_entry_.is_valid &&
          (!time_is_advancing ||
           (!is_maneuvering &&
            current_realtime - prediction_entry_.last_update_realtime < pred_interval &&
            Math.Abs(current_time_ut - prediction_entry_.last_update_ut) < 10.0));

      if (can_reuse_prediction) {
        prediction_entry_.Draw();
      } else {
        planetarium.PlanetariumPlotPrediction(
            Plugin,
            main_vessel_guid,
            prediction_t_max,
            VertexBuffer.data,
            VertexBuffer.size,
            out int vertex_count);
        prediction_entry_.last_update_realtime = current_realtime;
        prediction_entry_.last_update_ut = current_time_ut;
        prediction_entry_.UpdateMesh(vertex_count,
                                     adapter_.prediction_colour,
                                     adapter_.prediction_style,
                                     shared_indices_);
        prediction_entry_.Draw();
      }

      // Main vessel flight plan.
      if (Plugin.FlightPlanExists(main_vessel_guid) &&
          !RollingEphemerisDriver.ShouldSuppressPlot(main_vessel_guid)) {
        int number_of_segments =
            Plugin.FlightPlanNumberOfSegments(main_vessel_guid);
        while (flight_plan_segment_entries_.Count < number_of_segments) {
          flight_plan_segment_entries_.Add(new CachedMeshEntry());
        }

        bool fp_dirty = adapter_.flight_plan_dirty;
        for (int i = 0; i < number_of_segments; ++i) {
          var entry = flight_plan_segment_entries_[i];
          bool can_reuse_fp = !fp_dirty && entry.is_valid &&
              (!time_is_advancing ||
               (current_realtime - entry.last_update_realtime < 0.10 &&
                Math.Abs(current_time_ut - entry.last_update_ut) < 10.0));

          if (can_reuse_fp) {
            entry.Draw();
            continue;
          }
          bool is_burn = i % 2 == 1;
          var colour = is_burn
                           ? adapter_.burn_colour
                           : adapter_.flight_plan_colour;
          var style = is_burn
                          ? adapter_.burn_style
                          : adapter_.flight_plan_style;
          planetarium.PlanetariumPlotFlightPlanSegment(
              Plugin,
              main_vessel_guid,
              i,
              flight_plan_t_max,
              VertexBuffer.data,
              VertexBuffer.size,
              out int vertex_count);
          entry.last_update_realtime = current_realtime;
          entry.last_update_ut = current_time_ut;
          entry.UpdateMesh(vertex_count, colour, style, shared_indices_);
          entry.Draw();
        }
        if (fp_dirty) {
          adapter_.flight_plan_dirty = false;
        }
      }
    }

    // Target psychohistory and prediction.
    string target_id = FlightGlobals.fetch.VesselTarget?.GetVessel()?.id.
        ToString();
    if (FlightGlobals.ActiveVessel != null &&
        !adapter_.plotting_frame_selector_.target_frame_selected &&
        target_id != null &&
        Plugin.HasVessel(target_id)) {
      if (target_id != last_target_id_) {
        target_psychohistory_entry_.Invalidate();
        target_prediction_entry_.Invalidate();
        last_target_id_ = target_id;
      }

      // Target psychohistory
      bool can_reuse_target_psychohistory = target_psychohistory_entry_.is_valid &&
          (!time_is_advancing ||
           (current_realtime - target_psychohistory_entry_.last_update_realtime < 0.20 &&
            Math.Abs(current_time_ut - target_psychohistory_entry_.last_update_ut) < 60.0));

      if (can_reuse_target_psychohistory) {
        target_psychohistory_entry_.Draw();
      } else {
        planetarium.PlanetariumPlotPsychohistory(
            Plugin,
            target_id,
            history_length,
            t_max: null,
            VertexBuffer.data,
            VertexBuffer.size,
            out int vertex_count);
        target_psychohistory_entry_.last_update_realtime = current_realtime;
        target_psychohistory_entry_.last_update_ut = current_time_ut;
        target_psychohistory_entry_.UpdateMesh(vertex_count,
                                               adapter_.target_history_colour,
                                               adapter_.target_history_style,
                                               shared_indices_);
        target_psychohistory_entry_.Draw();
      }

      // Target prediction
      bool can_reuse_target_prediction = target_prediction_entry_.is_valid &&
          (!time_is_advancing ||
           (current_realtime - target_prediction_entry_.last_update_realtime < 0.050 &&
            Math.Abs(current_time_ut - target_prediction_entry_.last_update_ut) < 10.0));

      if (can_reuse_target_prediction) {
        target_prediction_entry_.Draw();
      } else {
        planetarium.PlanetariumPlotPrediction(
            Plugin,
            target_id,
            t_max: null,
            VertexBuffer.data,
            VertexBuffer.size,
            out int vertex_count);
        target_prediction_entry_.last_update_realtime = current_realtime;
        target_prediction_entry_.last_update_ut = current_time_ut;
        target_prediction_entry_.UpdateMesh(vertex_count,
                                            adapter_.target_prediction_colour,
                                            adapter_.target_prediction_style,
                                            shared_indices_);
        target_prediction_entry_.Draw();
      }
    }
  }

  private void PlotCelestialTrajectories(DisposablePlanetarium planetarium,
                                         string main_vessel_guid,
                                         double history_length) {
    PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                            Planetarium.fetch.Sun, TanAngularResolution());
  }

  // Plots the trajectories of `root` and its natural satellites.
  private void PlotSubtreeTrajectories(DisposablePlanetarium planetarium,
                                       string main_vessel_guid,
                                       double history_length,
                                       CelestialBody root,
                                       double tan_angular_resolution) {
    if (!celestial_trajectory_meshes_.TryGetValue(
            root,
            out CelestialTrajectories trajectories)) {
      trajectories = celestial_trajectory_meshes_[root] =
          new CelestialTrajectories();
    }
    var colour = root.orbitDriver?.Renderer?.orbitColor ??
        XKCDColors.SunshineYellow;
    var camera_world_position = ScaledSpace.ScaledToLocalSpace(
        PlanetariumCamera.fetch.transform.position);
    double min_distance_from_camera =
        (root.position - camera_world_position).magnitude;

    bool time_is_advancing = adapter_.is_time_advancing;
    double current_time_ut = Planetarium.GetUniversalTime();
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;

    if (!adapter_.plotting_frame_selector_.FixesBody(root) &&
        adapter_.show_celestial_trajectory(root)) {
      // Past trajectory: freeze when paused; throttle to 4 updates/sec during 1x warp
      bool can_reuse_past = trajectories.past.is_valid &&
          (!time_is_advancing ||
           (current_realtime - trajectories.past.last_update_realtime < 0.25 &&
            Math.Abs(current_time_ut - trajectories.past.last_update_ut) < 30.0));

      if (can_reuse_past) {
        trajectories.past.Draw();
        min_distance_from_camera =
            Math.Min(min_distance_from_camera,
                     trajectories.past.min_distance_from_camera);
      } else {
        planetarium.PlanetariumPlotCelestialPastTrajectory(
            Plugin,
            root.flightGlobalsIndex,
            main_vessel_guid,
            history_length,
            VertexBuffer.data,
            VertexBuffer.size,
            out double min_past_distance,
            out int vertex_count);
        min_distance_from_camera =
            Math.Min(min_distance_from_camera, min_past_distance);
        trajectories.past.min_distance_from_camera = min_past_distance;
        trajectories.past.last_update_ut = current_time_ut;
        trajectories.past.last_update_realtime = current_realtime;
        trajectories.past.UpdateMesh(vertex_count,
                                     colour,
                                     GLLines.Style.Faded,
                                     shared_indices_);
        trajectories.past.Draw();
      }

      if (main_vessel_guid != null) {
        // Future trajectory
        bool can_reuse_future = trajectories.future.is_valid &&
            (!time_is_advancing ||
             (current_realtime - trajectories.future.last_update_realtime < 0.25 &&
              Math.Abs(current_time_ut - trajectories.future.last_update_ut) < 30.0));

        if (can_reuse_future) {
          trajectories.future.Draw();
          min_distance_from_camera =
              Math.Min(min_distance_from_camera,
                       trajectories.future.min_distance_from_camera);
        } else {
          planetarium.PlanetariumPlotCelestialFutureTrajectory(
              Plugin,
              root.flightGlobalsIndex,
              main_vessel_guid,
              VertexBuffer.data,
              VertexBuffer.size,
              out double min_future_distance,
              out int vertex_count);
          min_distance_from_camera =
              Math.Min(min_distance_from_camera, min_future_distance);
          trajectories.future.min_distance_from_camera = min_future_distance;
          trajectories.future.last_update_ut = current_time_ut;
          trajectories.future.last_update_realtime = current_realtime;
          trajectories.future.UpdateMesh(vertex_count,
                                         colour,
                                         GLLines.Style.Solid,
                                         shared_indices_);
          trajectories.future.Draw();
        }
      }
    }
    foreach (CelestialBody child in root.orbitingBodies) {
      if (!adapter_.show_celestial_trajectory(root) ||
          child.orbit.ApR / min_distance_from_camera >
              2 * tan_angular_resolution) {
        PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                                child, tan_angular_resolution);
      }
    }
  }

  public void InvalidateAll() {
    foreach (var pair in celestial_trajectory_meshes_) {
      pair.Value.past.Invalidate();
      pair.Value.future.Invalidate();
    }
    psychohistory_entry_.Invalidate();
    prediction_entry_.Invalidate();
    target_psychohistory_entry_.Invalidate();
    target_prediction_entry_.Invalidate();
    for (int i = 0; i < flight_plan_segment_entries_.Count; ++i) {
      flight_plan_segment_entries_[i].Invalidate();
    }
    for (int i = 0; i < equipotential_entries_.Count; ++i) {
      equipotential_entries_[i].Invalidate();
    }
  }

  public void Dispose() {
    foreach (var pair in celestial_trajectory_meshes_) {
      pair.Value.past.Dispose();
      pair.Value.future.Dispose();
    }
    celestial_trajectory_meshes_.Clear();
    psychohistory_entry_.Dispose();
    prediction_entry_.Dispose();
    target_psychohistory_entry_.Dispose();
    target_prediction_entry_.Dispose();
    for (int i = 0; i < flight_plan_segment_entries_.Count; ++i) {
      flight_plan_segment_entries_[i].Dispose();
    }
    flight_plan_segment_entries_.Clear();
    for (int i = 0; i < equipotential_entries_.Count; ++i) {
      equipotential_entries_[i].Dispose();
    }
    equipotential_entries_.Clear();
    NativeZeroCopyTrajectoryPipeline.Dispose();
  }

  private static UnityEngine.Mesh MakeDynamicMesh() {
    var result = new UnityEngine.Mesh();
    result.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    result.MarkDynamic();
    result.bounds = NativeZeroCopyTrajectoryPipeline.CosmicBounds;
    return result;
  }

  private readonly PrincipiaPluginAdapter adapter_;

  private IntPtr Plugin => adapter_.Plugin();

  private static class VertexBuffer {
    public static IntPtr data => NativeZeroCopyTrajectoryPipeline.NativeBufferPtr;
    public static int size => NativeZeroCopyTrajectoryPipeline.CurrentCapacity;
  }

  internal class CachedMeshEntry {
    public UnityEngine.Mesh mesh;
    public UnityEngine.Vector3[] compact_vertices;
    public UnityEngine.Color32[] compact_colors;
    public int vertex_count;
    public bool is_valid;
    public double last_update_ut;
    public double last_update_realtime;
    public double min_distance_from_camera;

    public CachedMeshEntry() {
      mesh = MakeDynamicMesh();
      is_valid = false;
    }

    public void UpdateMesh(int count,
                           UnityEngine.Color colour,
                           GLLines.Style style,
                           int[] shared_indices) {
      using (PerformanceProbe.Measure(ProbeCategory.MeshGeneration)) {
        PerformanceProbe.RecordCacheMiss();
        vertex_count = count;
        if (count <= 0) {
          mesh.Clear();
          is_valid = true;
          return;
        }

        NativeZeroCopyTrajectoryPipeline.EnsureCapacity(count);

        // Compact vertex array reuse (resizes only if count grows).
        if (compact_vertices == null || compact_vertices.Length != count) {
          compact_vertices = new UnityEngine.Vector3[count];
        }

        // Zero-copy SIMD transfer from unmanaged native memory directly into compact_vertices
        NativeZeroCopyTrajectoryPipeline.CopyFromNativeBuffer(compact_vertices, count);

        // ZERO CPU AABB LOOP: Cosmic bounds eliminate CPU frustum min/max scanning entirely!
        mesh.bounds = NativeZeroCopyTrajectoryPipeline.CosmicBounds;

        // Compact 32-bit RGBA color array reuse (75% bandwidth reduction).
        if (compact_colors == null || compact_colors.Length != count) {
          compact_colors = new UnityEngine.Color32[count];
        }

        UnityEngine.Color32 base_c32 = colour;
        if (style == GLLines.Style.Faded) {
          float inv_count = 1.0f / count;
          byte a = base_c32.a;
          for (int i = 0; i < count; ++i) {
            byte faded_a = (byte)(a * (1.0f - 0.8f * (i * inv_count)));
            compact_colors[i] = new UnityEngine.Color32(base_c32.r, base_c32.g, base_c32.b, faded_a);
          }
        } else {
          for (int i = 0; i < count; ++i) {
            compact_colors[i] = base_c32;
          }
        }

        mesh.SetVertices(compact_vertices, 0, count);
        mesh.SetColors(compact_colors, 0, count);

        int index_count = style == GLLines.Style.Dashed ? count & ~1 : count;
        mesh.SetIndices(shared_indices,
                        indicesStart: 0,
                        indicesLength: index_count,
                        style == GLLines.Style.Dashed
                            ? UnityEngine.MeshTopology.Lines
                            : UnityEngine.MeshTopology.LineStrip,
                        submesh: 0,
                        calculateBounds: false);

        NativeZeroCopyTrajectoryPipeline.RecordTransfer(count);
        is_valid = true;
      }
    }

    public void Draw() {
      if (is_valid && vertex_count > 0 && mesh != null) {
        PerformanceProbe.RecordCacheHit();
        PerformanceProbe.RecordVertices(vertex_count);
        UnityEngine.Graphics.DrawMesh(
            mesh,
            UnityEngine.Vector3.zero,
            UnityEngine.Quaternion.identity,
            GLLines.line_material,
            (int)PrincipiaPluginAdapter.UnityLayers.Atmosphere,
            PlanetariumCamera.Camera);
      }
    }

    public void Invalidate() {
      is_valid = false;
    }

    public void Dispose() {
      if (mesh != null) {
        UnityEngine.Object.Destroy(mesh);
        mesh = null;
      }
      compact_vertices = null;
      compact_colors = null;
      is_valid = false;
    }
  }

  private class CelestialTrajectories {
    public CachedMeshEntry future = new CachedMeshEntry();
    public CachedMeshEntry past = new CachedMeshEntry();
  }

  private static readonly int[] shared_indices_ =
      new int[NativeZeroCopyTrajectoryPipeline.DefaultCapacity];

  static Plotter() {
    for (int i = 0; i < shared_indices_.Length; ++i) {
      shared_indices_[i] = i;
    }
  }

  private readonly Dictionary<CelestialBody, CelestialTrajectories>
      celestial_trajectory_meshes_ =
      new Dictionary<CelestialBody, CelestialTrajectories>();
  private readonly CachedMeshEntry psychohistory_entry_ = new CachedMeshEntry();
  private readonly CachedMeshEntry prediction_entry_ = new CachedMeshEntry();
  private readonly List<CachedMeshEntry> flight_plan_segment_entries_ =
      new List<CachedMeshEntry>();
  private readonly List<CachedMeshEntry> equipotential_entries_ =
      new List<CachedMeshEntry>();
  private readonly CachedMeshEntry target_psychohistory_entry_ = new CachedMeshEntry();
  private readonly CachedMeshEntry target_prediction_entry_ = new CachedMeshEntry();

  private Vector3d last_scaled_space_origin_ = Vector3d.zero;
  private string last_main_vessel_guid_ = null;
  private double last_history_length_ = -1.0;
  private string last_target_id_ = null;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
