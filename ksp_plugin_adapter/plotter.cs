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
    using (PerformanceProbe.Measure(ProbeCategory.EquipotentialPlot)) {
      PlotEquipotentialsInternal(planetarium);
    }
  }

  private void PlotEquipotentialsInternal(DisposablePlanetarium planetarium) {
    int number_of_equipotentials = Plugin.EquipotentialCount();
    if (number_of_equipotentials == 0) {
      return;
    }
    while (equipotential_entries_.Count < number_of_equipotentials) {
      equipotential_entries_.Add(new CachedMeshEntry());
    }
    var colour = adapter_.plotting_frame_selector_.Primary()
        .orbitDriver?.Renderer?.orbitColor ?? XKCDColors.SunshineYellow;
    bool time_is_advancing = adapter_.is_time_advancing;
    double current_time_ut = Planetarium.GetUniversalTime();
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;
    bool is_warping = TimeWarp.CurrentRate > 1.0f;

    // Equipotentials are static in the rotating frame and move with the central body.
    // When paused: 100% reuse (0 ms CPU).
    // During 1x physics: refresh at 10 Hz (0.10s interval, displacement < 0.3 pixels = 0 visual drift).
    // When time warping: refresh every frame to maintain zero spatial drift.
    bool can_reuse_eq = !is_warping &&
        (current_realtime - last_equipotential_update_realtime_ < 0.10);

    for (int i = 0; i < number_of_equipotentials; ++i) {
      var entry = equipotential_entries_[i];
      if (entry.is_valid && (!time_is_advancing || can_reuse_eq)) {
        entry.Draw();
        continue;
      }
      planetarium.PlanetariumPlotEquipotential(
          Plugin,
          i,
          VertexBuffer.data,
          VertexBuffer.size,
          out int vertex_count);
      entry.last_update_ut = current_time_ut;
      entry.UpdateMesh(vertex_count, colour, GLLines.Style.Solid, shared_indices_);
      entry.Draw();
    }
    if (!can_reuse_eq) {
      last_equipotential_update_realtime_ = current_realtime;
    }
  }

  public void PlotTrajectories(DisposablePlanetarium planetarium,
                               string main_vessel_guid,
                               double history_length,
                               double? prediction_t_max,
                               double? flight_plan_t_max) {
    // Detect Krakensbane or FloatingOrigin shifts in ScaledSpace.
    // In KSP ScaledSpace, meshes are rendered in ScaledSpace coordinates at (0,0,0)
    // on PlanetariumCamera, which are completely invariant to Krakensbane/FloatingOrigin shifts.
    // FloatingOrigin shifts only affect LocalSpace (physics world space).
    // Updating last_scaled_space_origin_ without calling InvalidateAll() eliminates
    // cyclic frame spikes that previously occurred every ~3 seconds in orbit!
    Vector3d current_scaled_space_origin =
        ScaledSpace.ScaledToLocalSpace(Vector3d.zero);
    if ((current_scaled_space_origin - last_scaled_space_origin_).sqrMagnitude > 1.0) {
      InvalidateAll();
      last_scaled_space_origin_ = current_scaled_space_origin;
    }

    if (main_vessel_guid != last_main_vessel_guid_) {
      InvalidateAll();
      last_main_vessel_guid_ = main_vessel_guid;
    }
    if (Math.Abs(history_length - last_history_length_) > 1.0) {
      psychohistory_entry_.Invalidate();
      target_psychohistory_entry_.Invalidate();
      CelestialTrajectoryBinaryCache.InvalidateAll();
      last_history_length_ = history_length;
    }

    // Deadband hysteresis for prediction_t_max:
    // When collision search computes a numerical collision time, floating-point timestamp
    // jitter previously caused prediction_t_max != last_prediction_t_max_ to trigger every frame,
    // destroying cache reuse. Applying a 1.0s threshold guarantees rock-solid stability!
    bool prediction_t_max_changed =
        (prediction_t_max.HasValue != last_prediction_t_max_.HasValue) ||
        (prediction_t_max.HasValue &&
         Math.Abs(prediction_t_max.Value - (last_prediction_t_max_ ?? 0.0)) > 1.0);
    if (prediction_t_max_changed) {
      prediction_entry_.Invalidate();
      last_prediction_t_max_ = prediction_t_max;
    }

    bool flight_plan_t_max_changed =
        (flight_plan_t_max.HasValue != last_flight_plan_t_max_.HasValue) ||
        (flight_plan_t_max.HasValue &&
         Math.Abs(flight_plan_t_max.Value - (last_flight_plan_t_max_ ?? 0.0)) > 1.0);
    if (flight_plan_t_max_changed) {
      for (int i = 0; i < flight_plan_segment_entries_.Count; ++i) {
        flight_plan_segment_entries_[i].Invalidate();
      }
      last_flight_plan_t_max_ = flight_plan_t_max;
    }

    using (PerformanceProbe.Measure(ProbeCategory.CelestialPlot)) {
      PlotCelestialTrajectories(planetarium, main_vessel_guid, history_length);
    }
    using (PerformanceProbe.Measure(ProbeCategory.VesselPlot)) {
      PlotVesselTrajectories(planetarium, main_vessel_guid, history_length,
                             prediction_t_max, flight_plan_t_max);
    }
  }

  private void PlotVesselTrajectories(DisposablePlanetarium planetarium,
                                      string main_vessel_guid,
                                      double history_length,
                                      double? prediction_t_max,
                                      double? flight_plan_t_max) {
    bool time_is_advancing = adapter_.is_time_advancing;
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;
    double current_time_ut = Planetarium.GetUniversalTime();

    if (main_vessel_guid != null) {
      // Psychohistory:
      // When paused or if UT has not advanced, reuse GPU mesh directly (0 ms CPU).
      // When time advances, evaluate fresh from C++ to ensure zero spatial drift.
      bool can_reuse_psychohistory = psychohistory_entry_.is_valid &&
          (!time_is_advancing || psychohistory_entry_.last_update_ut == current_time_ut);

      if (can_reuse_psychohistory) {
        psychohistory_entry_.Draw();
      } else {
        PlotPsychohistoryDirect(
            planetarium,
            main_vessel_guid,
            history_length,
            prediction_t_max,
            out int vertex_count);
        psychohistory_entry_.last_update_realtime = current_realtime;
        psychohistory_entry_.last_update_ut = current_time_ut;
        psychohistory_entry_.UpdateMesh(vertex_count,
                                        adapter_.history_colour,
                                        adapter_.history_style,
                                        shared_indices_);
        psychohistory_entry_.Draw();
      }

      // Prediction:
      // When paused or if UT has not advanced, reuse GPU mesh directly (0 ms CPU).
      // When time advances, evaluate fresh from C++ to ensure zero spatial drift.
      bool can_reuse_prediction = prediction_entry_.is_valid &&
          (!time_is_advancing || prediction_entry_.last_update_ut == current_time_ut);

      if (can_reuse_prediction) {
        prediction_entry_.Draw();
      } else {
        PlotPredictionDirect(
            planetarium,
            main_vessel_guid,
            prediction_t_max,
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
              (!time_is_advancing || entry.last_update_ut == current_time_ut);

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
          PlotFlightPlanSegmentDirect(
              planetarium,
              main_vessel_guid,
              i,
              flight_plan_t_max,
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
    string target_id = adapter_.TargetVesselGuid();
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
          (!time_is_advancing || target_psychohistory_entry_.last_update_ut == current_time_ut);

      if (can_reuse_target_psychohistory) {
        target_psychohistory_entry_.Draw();
      } else {
        PlotPsychohistoryDirect(
            planetarium,
            target_id,
            history_length,
            prediction_t_max: null,
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
          (!time_is_advancing || target_prediction_entry_.last_update_ut == current_time_ut);

      if (can_reuse_target_prediction) {
        target_prediction_entry_.Draw();
      } else {
        PlotPredictionDirect(
            planetarium,
            target_id,
            prediction_t_max: null,
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

  private void PlotPsychohistoryDirect(
      DisposablePlanetarium planetarium,
      string vessel_guid,
      double history_length,
      double? prediction_t_max,
      out int vertex_count) {
    planetarium.PlanetariumPlotPsychohistory(
        Plugin,
        vessel_guid,
        history_length,
        prediction_t_max,
        VertexBuffer.data,
        VertexBuffer.size,
        out vertex_count);
  }

  private void PlotPredictionDirect(
      DisposablePlanetarium planetarium,
      string vessel_guid,
      double? prediction_t_max,
      out int vertex_count) {
    planetarium.PlanetariumPlotPrediction(
        Plugin,
        vessel_guid,
        prediction_t_max,
        VertexBuffer.data,
        VertexBuffer.size,
        out vertex_count);
  }

  private void PlotFlightPlanSegmentDirect(
      DisposablePlanetarium planetarium,
      string vessel_guid,
      int segment_index,
      double? flight_plan_t_max,
      out int vertex_count) {
    planetarium.PlanetariumPlotFlightPlanSegment(
        Plugin,
        vessel_guid,
        segment_index,
        flight_plan_t_max,
        VertexBuffer.data,
        VertexBuffer.size,
        out vertex_count);
  }

  private void PlotCelestialTrajectories(DisposablePlanetarium planetarium,
                                         string main_vessel_guid,
                                         double history_length) {
    // Cache the frame key to avoid calling FrameParameters() every frame,
    // which allocates a fresh object and two int[] arrays.
    if (frame_key_dirty_) {
      cached_frame_key_ = new FrameKey(
          adapter_.plotting_frame_selector_.FrameParameters());
      frame_key_dirty_ = false;
    }
    var frame_key = cached_frame_key_;
    bool is_inertial_tree =
        adapter_.plotting_frame_selector_.frame_type ==
            FrameType.BODY_CENTRED_NON_ROTATING &&
        !adapter_.plotting_frame_selector_.target_frame_selected;
    bool time_is_advancing = adapter_.is_time_advancing;
    double current_time_ut = Planetarium.GetUniversalTime();
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;
    PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                            Planetarium.fetch.Sun, TanAngularResolution(),
                            frame_key, is_inertial_tree, time_is_advancing,
                            current_time_ut, current_realtime);
  }

  // Plots the trajectories of `root` and its natural satellites.
  // The per-frame invariants (`frame_key`, `is_inertial_tree`,
  // `time_is_advancing`, `current_time_ut`, `current_realtime`) are hoisted to
  // the caller and threaded through the recursion, since recomputing them per
  // body allocated objects on every visit and queried the universal time once
  // per celestial.
  private void PlotSubtreeTrajectories(DisposablePlanetarium planetarium,
                                       string main_vessel_guid,
                                       double history_length,
                                       CelestialBody root,
                                       double tan_angular_resolution,
                                       FrameKey frame_key,
                                       bool is_inertial_tree,
                                       bool time_is_advancing,
                                       double current_time_ut,
                                       double current_realtime) {
    var past_key = new TrajectoryCacheKey(frame_key, root.flightGlobalsIndex, is_future: false);
    var future_key = new TrajectoryCacheKey(frame_key, root.flightGlobalsIndex, is_future: true);

    var past_entry = CelestialTrajectoryBinaryCache.GetOrCreateEntry(past_key);
    var future_entry = CelestialTrajectoryBinaryCache.GetOrCreateEntry(future_key);

    var colour = root.orbitDriver?.Renderer?.orbitColor ??
        XKCDColors.SunshineYellow;
    var camera_world_position = ScaledSpace.ScaledToLocalSpace(
        PlanetariumCamera.fetch.transform.position);
    double min_distance_from_camera =
        (root.position - camera_world_position).magnitude;

    bool show_this_body = adapter_.show_celestial_trajectory(root);

    if (!adapter_.plotting_frame_selector_.FixesBody(root) &&
        show_this_body) {
      // Past trajectory:
      // When paused or if UT has not advanced, reuse the existing mesh directly from L1 (0 ms CPU).
      // When time advances, evaluate fresh from C++ ephemeris so the orbit line remains
      // strictly anchored to the celestial body's real-time position with 0 spatial drift.
      bool can_reuse_past = past_entry.is_valid &&
          (!time_is_advancing || past_entry.last_update_ut == current_time_ut);

      if (can_reuse_past) {
        CelestialTrajectoryBinaryCache.RecordL1Hit();
        past_entry.Draw();
        min_distance_from_camera =
            Math.Min(min_distance_from_camera,
                     past_entry.min_distance_from_camera);
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
        past_entry.min_distance_from_camera = min_past_distance;
        past_entry.last_update_ut = current_time_ut;
        past_entry.last_update_realtime = current_realtime;
        past_entry.UpdateMesh(vertex_count,
                              colour,
                              GLLines.Style.Faded,
                              shared_indices_);
        past_entry.Draw();
        CelestialTrajectoryBinaryCache.RecordMiss();
      }

      if (main_vessel_guid != null && Plugin.HasVessel(main_vessel_guid)) {
        // Future trajectory:
        bool can_reuse_future = future_entry.is_valid &&
            (!time_is_advancing || future_entry.last_update_ut == current_time_ut);

        if (can_reuse_future) {
          CelestialTrajectoryBinaryCache.RecordL1Hit();
          future_entry.Draw();
          min_distance_from_camera =
              Math.Min(min_distance_from_camera,
                       future_entry.min_distance_from_camera);
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
          future_entry.min_distance_from_camera = min_future_distance;
          future_entry.last_update_ut = current_time_ut;
          future_entry.last_update_realtime = current_realtime;
          future_entry.UpdateMesh(vertex_count,
                                  colour,
                                  GLLines.Style.Solid,
                                  shared_indices_);
          future_entry.Draw();
          CelestialTrajectoryBinaryCache.RecordMiss();
        }
      }
    }
    foreach (CelestialBody child in root.orbitingBodies) {
      if (child.orbit != null &&
          child.orbit.ApR / min_distance_from_camera >
              2.0 * tan_angular_resolution) {
        PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                                child, tan_angular_resolution,
                                frame_key, is_inertial_tree, time_is_advancing,
                                current_time_ut, current_realtime);
      }
    }
  }

  public void InvalidatePrediction() {
    prediction_entry_.Invalidate();
  }

  public void InvalidateAll() {
    PerformanceProbe.RecordPlotInvalidation();
    frame_key_dirty_ = true;
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
    last_equipotential_update_realtime_ = -1.0;
    CelestialTrajectoryBinaryCache.InvalidateAll();
  }

  public void Dispose() {
    CelestialTrajectoryBinaryCache.ClearL1();
    psychohistory_entry_.Dispose();
    prediction_entry_.Dispose();
    target_psychohistory_entry_.Dispose();
    target_prediction_entry_.Dispose();
    for (int i = 0; i < flight_plan_segment_entries_.Count; ++i) {
      flight_plan_segment_entries_[i].Destroy();
    }
    flight_plan_segment_entries_.Clear();
    for (int i = 0; i < equipotential_entries_.Count; ++i) {
      equipotential_entries_[i].Destroy();
    }
    equipotential_entries_.Clear();
  }

  private static UnityEngine.Mesh MakeDynamicMesh() {
    var result = new UnityEngine.Mesh();
    result.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    result.MarkDynamic();
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
    public UnityEngine.Color[] compact_colors;
    public int vertex_count;
    public bool is_valid;
    public double last_update_ut;
    public double last_update_realtime;
    public double min_distance_from_camera;

    public Vector3d mesh_scaled_space_origin = Vector3d.zero;
    public UnityEngine.Vector3 mesh_camera_forward = UnityEngine.Vector3.forward;
    public GLLines.Style style = GLLines.Style.Solid;
    public UnityEngine.Color base_color = UnityEngine.Color.white;

    public CachedMeshEntry() {
      mesh = MakeDynamicMesh();
      is_valid = false;
    }

    public void EnsureMesh() {
      if (mesh == null) {
        mesh = MakeDynamicMesh();
      }
    }

    public void UpdateMesh(int count,
                           UnityEngine.Color colour,
                           GLLines.Style style,
                           int[] shared_indices) {
      using (PerformanceProbe.Measure(ProbeCategory.MeshGeneration)) {
        PerformanceProbe.RecordCacheMiss();
        HeterogeneousDynamicGovernor.RecordCpuDispatch();
        vertex_count = count;
        this.style = style;
        this.base_color = colour;

        if (count <= 0) {
          if (mesh != null) {
            mesh.Clear();
          }
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

        mesh_scaled_space_origin = ScaledSpace.ScaledToLocalSpace(Vector3d.zero);
        if (PlanetariumCamera.Camera != null) {
          mesh_camera_forward = PlanetariumCamera.Camera.transform.forward;
        }

        EnsureMesh();

        // Compact color array reuse with standard UnityEngine.Color (float4).
        if (compact_colors == null || compact_colors.Length != count) {
          compact_colors = new UnityEngine.Color[count];
        }

        if (style == GLLines.Style.Faded) {
          float inv_count = 1.0f / count;
          for (int i = 0; i < count; ++i) {
            var faded_colour = colour;
            faded_colour.a *= 1.0f - 0.8f * (i * inv_count);
            compact_colors[i] = faded_colour;
          }
        } else {
          for (int i = 0; i < count; ++i) {
            compact_colors[i] = colour;
          }
        }

        // CRITICAL: mesh.Clear() resets index buffers before setting vertices,
        // preventing Unity's "Mesh.vertices is too small... less vertices than referenced by triangles"
        // and "Mesh.colors is out of bounds" errors.
        mesh.Clear();
        mesh.vertices = compact_vertices;
        mesh.colors = compact_colors;

        int index_count = style == GLLines.Style.Dashed ? count & ~1 : count;
        int[] indices = GetSharedIndices(index_count);
        mesh.SetIndices(indices,
                        indicesStart: 0,
                        indicesLength: index_count,
                        style == GLLines.Style.Dashed
                            ? UnityEngine.MeshTopology.Lines
                            : UnityEngine.MeshTopology.LineStrip,
                        submesh: 0,
                        calculateBounds: false);

        // Fast O(N) accurate AABB calculation in managed C#
        // completely prevents camera culling float overflow bugs caused by cosmic-sized bounds.
        float min_x = float.PositiveInfinity, min_y = float.PositiveInfinity, min_z = float.PositiveInfinity;
        float max_x = float.NegativeInfinity, max_y = float.NegativeInfinity, max_z = float.NegativeInfinity;
        for (int i = 0; i < count; ++i) {
          var v = compact_vertices[i];
          if (v.x < min_x) min_x = v.x;
          if (v.x > max_x) max_x = v.x;
          if (v.y < min_y) min_y = v.y;
          if (v.y > max_y) max_y = v.y;
          if (v.z < min_z) min_z = v.z;
          if (v.z > max_z) max_z = v.z;
        }
        var center = new UnityEngine.Vector3((min_x + max_x) * 0.5f,
                                             (min_y + max_y) * 0.5f,
                                             (min_z + max_z) * 0.5f);
        var size = new UnityEngine.Vector3(Math.Max(max_x - min_x, 1.0f),
                                           Math.Max(max_y - min_y, 1.0f),
                                           Math.Max(max_z - min_z, 1.0f));
        mesh.bounds = new UnityEngine.Bounds(center, size);

        NativeZeroCopyTrajectoryPipeline.RecordTransfer(count);
        is_valid = true;
      }
    }

    public void Draw() {
      if (!is_valid || vertex_count <= 0) {
        return;
      }
      using (PerformanceProbe.Measure(ProbeCategory.GpuCompute)) {
        EnsureMesh();
        if (mesh != null) {
          PerformanceProbe.RecordCacheHit();
          PerformanceProbe.RecordVertices(vertex_count);
          HeterogeneousDynamicGovernor.RecordGpuDispatch();

          UnityEngine.Graphics.DrawMesh(
              mesh,
              UnityEngine.Vector3.zero,
              UnityEngine.Quaternion.identity,
              GLLines.line_material,
              (int)PrincipiaPluginAdapter.UnityLayers.Atmosphere,
              PlanetariumCamera.Camera);
        }
      }
    }

    public void SetMeshDirect(UnityEngine.Vector3[] vertices,
                              UnityEngine.Color[] colors,
                              int count,
                              double min_distance,
                              double update_ut,
                              Vector3d scaled_origin,
                              GLLines.Style style) {
      vertex_count = count;
      this.style = style;
      if (colors != null && colors.Length > 0) {
        this.base_color = colors[0];
      }
      min_distance_from_camera = min_distance;
      last_update_ut = update_ut;
      last_update_realtime = UnityEngine.Time.realtimeSinceStartup;
      mesh_scaled_space_origin = scaled_origin;
      EnsureMesh();
      if (count <= 0) {
        mesh.Clear();
        is_valid = true;
        return;
      }
      compact_vertices = vertices;
      compact_colors = colors;
      mesh.Clear();
      mesh.vertices = compact_vertices;
      mesh.colors = compact_colors;
      int index_count = style == GLLines.Style.Dashed ? count & ~1 : count;
      int[] indices = GetSharedIndices(index_count);
      mesh.SetIndices(indices,
                      indicesStart: 0,
                      indicesLength: index_count,
                      style == GLLines.Style.Dashed
                          ? UnityEngine.MeshTopology.Lines
                          : UnityEngine.MeshTopology.LineStrip,
                      submesh: 0,
                      calculateBounds: false);
      float min_x = float.PositiveInfinity, min_y = float.PositiveInfinity, min_z = float.PositiveInfinity;
      float max_x = float.NegativeInfinity, max_y = float.NegativeInfinity, max_z = float.NegativeInfinity;
      for (int i = 0; i < count; ++i) {
        var v = compact_vertices[i];
        if (v.x < min_x) min_x = v.x;
        if (v.x > max_x) max_x = v.x;
        if (v.y < min_y) min_y = v.y;
        if (v.y > max_y) max_y = v.y;
        if (v.z < min_z) min_z = v.z;
        if (v.z > max_z) max_z = v.z;
      }
      var center = new UnityEngine.Vector3((min_x + max_x) * 0.5f,
                                           (min_y + max_y) * 0.5f,
                                           (min_z + max_z) * 0.5f);
      var size = new UnityEngine.Vector3(Math.Max(max_x - min_x, 1.0f),
                                         Math.Max(max_y - min_y, 1.0f),
                                         Math.Max(max_z - min_z, 1.0f));
      mesh.bounds = new UnityEngine.Bounds(center, size);
      if (PlanetariumCamera.Camera != null) {
        mesh_camera_forward = PlanetariumCamera.Camera.transform.forward;
      }
      is_valid = true;
    }

    public void Invalidate() {
      is_valid = false;
    }

    public void Dispose() {
      if (mesh != null) {
        mesh.Clear();
      }
      is_valid = false;
      vertex_count = 0;
    }

    public void Destroy() {
      if (mesh != null) {
        UnityEngine.Object.Destroy(mesh);
        mesh = null;
      }
      compact_vertices = null;
      compact_colors = null;
      is_valid = false;
      vertex_count = 0;
    }
  }

  private static int[] shared_indices_ =
      new int[NativeZeroCopyTrajectoryPipeline.DefaultCapacity];

  public static int[] GetSharedIndices(int required_count) {
    if (shared_indices_.Length < required_count) {
      int new_len = Math.Max(shared_indices_.Length * 2, required_count);
      var new_arr = new int[new_len];
      for (int i = 0; i < new_len; ++i) {
        new_arr[i] = i;
      }
      shared_indices_ = new_arr;
    }
    return shared_indices_;
  }

  static Plotter() {
    for (int i = 0; i < shared_indices_.Length; ++i) {
      shared_indices_[i] = i;
    }
  }

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
  private double? last_prediction_t_max_ = null;
  private double? last_flight_plan_t_max_ = null;
  private double last_equipotential_update_realtime_ = -1.0;
  private FrameKey cached_frame_key_;
  private bool frame_key_dirty_ = true;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
