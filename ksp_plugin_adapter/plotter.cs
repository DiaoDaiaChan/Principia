using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace principia {
namespace ksp_plugin_adapter {

class Plotter {
  public Plotter(PrincipiaPluginAdapter adapter) {
    adapter_ = adapter;
    shared_indices_ = new int[VertexBuffer.size];
    for (int i = 0; i < shared_indices_.Length; ++i) {
      shared_indices_[i] = i;
    }
  }

  public static double TanAngularResolution() {
    const double degree = Math.PI / 180;
    UnityEngine.Camera camera = PlanetariumCamera.Camera;
    float vertical_fov = camera.fieldOfView;
    float horizontal_fov =
        UnityEngine.Camera.VerticalToHorizontalFieldOfView(
            vertical_fov,
            camera.aspect);
    return Math.Min(
               Math.Tan(vertical_fov * degree / 2) / (camera.pixelHeight / 2),
               Math.Tan(horizontal_fov * degree / 2) /
               (camera.pixelWidth / 2));
  }

  public void PlotEquipotentials(DisposablePlanetarium planetarium) {
    PlotEquipotentialsInternal(planetarium);
  }

  private CelestialBody CurrentReferenceAnchor =>
      adapter_.plotting_frame_selector_.Centre() ??
      FlightGlobals.currentMainBody ??
      Planetarium.fetch?.Sun;

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

    double eq_interval = is_warping ? 0.25 : 0.50;
    bool eq_interval_elapsed =
        current_realtime - last_equipotential_update_realtime_ >= eq_interval;

    for (int i = 0; i < number_of_equipotentials; ++i) {
      var entry = equipotential_entries_[i];
      bool can_reuse_eq = entry.has_content &&
          ((!time_is_advancing && entry.is_valid) || !eq_interval_elapsed);
      if (can_reuse_eq) {
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
      entry.UpdateMesh(vertex_count, colour, GLLines.Style.Solid, shared_indices_, CurrentReferenceAnchor);
      entry.Draw();
    }
    if (eq_interval_elapsed) {
      last_equipotential_update_realtime_ = current_realtime;
    }
  }

  public void PlotTrajectories(DisposablePlanetarium planetarium,
                               string main_vessel_guid,
                               double history_length,
                               double? prediction_t_max,
                               double? flight_plan_t_max) {
    if (main_vessel_guid == null) {
      last_main_vessel_guid_ = null;
    } else if (main_vessel_guid != last_main_vessel_guid_) {
      InvalidateAll("vessel-change");
      last_main_vessel_guid_ = main_vessel_guid;
    }

    if (Math.Abs(history_length - last_history_length_) > 1.0) {
      psychohistory_entry_.Invalidate();
      target_psychohistory_entry_.Invalidate();
      last_history_length_ = history_length;
    }

    bool prediction_t_max_changed =
        (prediction_t_max.HasValue != last_prediction_t_max_.HasValue) ||
        (prediction_t_max.HasValue &&
         Math.Abs(prediction_t_max.Value - (last_prediction_t_max_ ?? 0.0)) > 1.0);
    if (prediction_t_max_changed) {
      prediction_entry_.Invalidate(immediate: true);
      last_prediction_t_max_ = prediction_t_max;
    }

    bool flight_plan_t_max_changed =
        (flight_plan_t_max.HasValue != last_flight_plan_t_max_.HasValue) ||
        (flight_plan_t_max.HasValue &&
         Math.Abs(flight_plan_t_max.Value - (last_flight_plan_t_max_ ?? 0.0)) > 1.0);
    if (flight_plan_t_max_changed) {
      for (int i = 0; i < flight_plan_segment_entries_.Count; ++i) {
        flight_plan_segment_entries_[i].Invalidate(immediate: true);
      }
      last_flight_plan_t_max_ = flight_plan_t_max;
    }

    if (PlanetariumCamera.fetch != null) {
      float current_camera_distance = PlanetariumCamera.fetch.Distance;
      double current_realtime = UnityEngine.Time.realtimeSinceStartup;
      if (last_camera_distance_ > 0f && current_camera_distance > 0f) {
        float distance_ratio = current_camera_distance / last_camera_distance_;
        // 15% distance change triggers prompt adaptive LOD resample without delay
        bool zoom_changed = distance_ratio < 0.85f || distance_ratio > 1.18f;
        if (zoom_changed && current_realtime - last_camera_zoom_realtime_ >= 0.08) {
          last_camera_distance_ = current_camera_distance;
          last_camera_zoom_realtime_ = current_realtime;
          prediction_entry_.Invalidate(immediate: true);
          target_prediction_entry_.Invalidate(immediate: true);
          for (int i = 0; i < flight_plan_segment_entries_.Count; ++i) {
            flight_plan_segment_entries_[i].Invalidate(immediate: true);
          }
          psychohistory_entry_.Invalidate(immediate: true);
          target_psychohistory_entry_.Invalidate(immediate: true);
        }
      } else if (current_camera_distance > 0f) {
        last_camera_distance_ = current_camera_distance;
      }
    }

    celestial_refresh_budget_ = MaxCelestialRefreshesPerFrame;
    vessel_refresh_budget_ = MaxVesselRefreshesPerFrame;

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
    bool is_warping = TimeWarp.CurrentRate > 1.0f;
    CelestialBody anchor = CurrentReferenceAnchor;

    double psychohistory_interval = Math.Max(
        is_warping ? 0.50 : 0.40,
        Math.Min(1.0, psychohistory_rebuild_ms_ema_ * 0.08));

    if (main_vessel_guid != null) {
      bool psychohistory_needs_rebuild = NeedsRebuild(psychohistory_entry_,
                                                      psychohistory_interval,
                                                      time_is_advancing,
                                                      current_realtime);
      if (psychohistory_needs_rebuild &&
          (!psychohistory_entry_.has_content || psychohistory_entry_.immediate_rebuild_requested || vessel_refresh_budget_ > 0)) {
        if (psychohistory_entry_.has_content && !psychohistory_entry_.immediate_rebuild_requested) {
          --vessel_refresh_budget_;
        }
        long rebuild_t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
                                        shared_indices_,
                                        anchor);
        double rebuild_ms =
            (System.Diagnostics.Stopwatch.GetTimestamp() - rebuild_t0) *
            1000.0 / System.Diagnostics.Stopwatch.Frequency;
        psychohistory_rebuild_ms_ema_ =
            psychohistory_rebuild_ms_ema_ * 0.9 + rebuild_ms * 0.1;
      }
      psychohistory_entry_.Draw();

      const double prediction_rebuild_floor = 0.20;
      bool prediction_floor_elapsed =
          current_realtime - prediction_entry_.last_update_realtime >=
          prediction_rebuild_floor;
      bool prediction_needs_rebuild = !prediction_entry_.has_content ||
          (!prediction_entry_.is_valid && (prediction_entry_.immediate_rebuild_requested || prediction_floor_elapsed));
      if (prediction_needs_rebuild &&
          (!prediction_entry_.has_content || prediction_entry_.immediate_rebuild_requested || vessel_refresh_budget_ > 0)) {
        if (prediction_entry_.has_content && !prediction_entry_.immediate_rebuild_requested) {
          --vessel_refresh_budget_;
        }
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
                                     shared_indices_,
                                     anchor);
      }
      prediction_entry_.Draw();

      // Main vessel flight plan.
      if (Plugin.FlightPlanExists(main_vessel_guid)) {
        int number_of_segments =
            Plugin.FlightPlanNumberOfSegments(main_vessel_guid);
        while (flight_plan_segment_entries_.Count < number_of_segments) {
          flight_plan_segment_entries_.Add(new CachedMeshEntry());
        }

        bool fp_dirty = adapter_.flight_plan_dirty;
        const double fp_rebuild_floor = 0.20;
        for (int i = 0; i < number_of_segments; ++i) {
          var entry = flight_plan_segment_entries_[i];
          bool fp_floor_elapsed =
              current_realtime - entry.last_update_realtime >=
              fp_rebuild_floor;
          bool segment_needs_rebuild = !entry.has_content ||
              fp_dirty ||
              (!entry.is_valid && (entry.immediate_rebuild_requested || fp_floor_elapsed));

          if (segment_needs_rebuild &&
              (fp_dirty || entry.immediate_rebuild_requested || !entry.has_content || vessel_refresh_budget_ > 0)) {
            if (entry.has_content && !fp_dirty && !entry.immediate_rebuild_requested) {
              --vessel_refresh_budget_;
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
            entry.UpdateMesh(vertex_count, colour, style, shared_indices_, anchor);
          }
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
        target_psychohistory_entry_.Invalidate(immediate: true);
        target_prediction_entry_.Invalidate(immediate: true);
        last_target_id_ = target_id;
      }

      bool target_psychohistory_needs_rebuild =
          NeedsRebuild(target_psychohistory_entry_,
                       psychohistory_interval,
                       time_is_advancing,
                       current_realtime);
      if (target_psychohistory_needs_rebuild &&
          (!target_psychohistory_entry_.has_content || target_psychohistory_entry_.immediate_rebuild_requested || vessel_refresh_budget_ > 0)) {
        if (target_psychohistory_entry_.has_content && !target_psychohistory_entry_.immediate_rebuild_requested) {
          --vessel_refresh_budget_;
        }
        long rebuild_t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
                                               shared_indices_,
                                               anchor);
        double rebuild_ms =
            (System.Diagnostics.Stopwatch.GetTimestamp() - rebuild_t0) *
            1000.0 / System.Diagnostics.Stopwatch.Frequency;
        psychohistory_rebuild_ms_ema_ =
            psychohistory_rebuild_ms_ema_ * 0.9 + rebuild_ms * 0.1;
      }
      target_psychohistory_entry_.Draw();

      const double target_prediction_rebuild_floor = 0.20;
      bool target_prediction_floor_elapsed =
          current_realtime - target_prediction_entry_.last_update_realtime >=
          target_prediction_rebuild_floor;
      bool target_prediction_needs_rebuild =
          !target_prediction_entry_.has_content ||
          (!target_prediction_entry_.is_valid && (target_prediction_entry_.immediate_rebuild_requested || target_prediction_floor_elapsed));
      if (target_prediction_needs_rebuild &&
          (!target_prediction_entry_.has_content || target_prediction_entry_.immediate_rebuild_requested || vessel_refresh_budget_ > 0)) {
        if (target_prediction_entry_.has_content && !target_prediction_entry_.immediate_rebuild_requested) {
          --vessel_refresh_budget_;
        }
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
                                            shared_indices_,
                                            anchor);
      }
      target_prediction_entry_.Draw();
    }
  }

  private static bool NeedsRebuild(CachedMeshEntry entry,
                                   double interval,
                                   bool time_is_advancing,
                                   double current_realtime) {
    if (!entry.has_content || entry.immediate_rebuild_requested) {
      return true;
    }
    if (!time_is_advancing) {
      return !entry.is_valid;
    }
    return !entry.is_valid &&
           (current_realtime - entry.last_update_realtime >= interval);
  }

  private bool TryConsumeCelestialRefresh(CachedMeshEntry entry,
                                          double interval,
                                          bool time_is_advancing,
                                          double current_realtime) {
    if (!entry.has_content) {
      return true;
    }
    if (!time_is_advancing) {
      return false;
    }
    if (current_realtime - entry.last_update_realtime < interval) {
      return false;
    }
    if (celestial_refresh_budget_ > 0) {
      --celestial_refresh_budget_;
      return true;
    }
    return false;
  }

  private void PlotCelestialTrajectories(DisposablePlanetarium planetarium,
                                         string main_vessel_guid,
                                         double history_length) {
    string frame_key = adapter_.plotting_frame_selector_.FrameParameters().ToString();
    if (frame_key != last_frame_key_ || frame_key_dirty_) {
      last_frame_key_ = frame_key;
      frame_key_dirty_ = false;
      foreach (var pair in celestial_trajectory_entries_) {
        pair.Value.past.Invalidate(immediate: true);
        pair.Value.future.Invalidate(immediate: true);
      }
    }

    bool is_inertial_tree =
        adapter_.plotting_frame_selector_.frame_type ==
            FrameType.BODY_CENTRED_NON_ROTATING &&
        !adapter_.plotting_frame_selector_.target_frame_selected;
    bool time_is_advancing = adapter_.is_time_advancing;
    double current_time_ut = Planetarium.GetUniversalTime();
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;
    CelestialBody anchor = is_inertial_tree ? null : CurrentReferenceAnchor;
    bool frame_hides_unpinned = adapter_.FrameHidesUnpinnedCelestials();

    PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                            Planetarium.fetch.Sun, TanAngularResolution(),
                            frame_key, is_inertial_tree, time_is_advancing,
                            current_time_ut, current_realtime, anchor,
                            frame_hides_unpinned);
  }

  private void PlotSubtreeTrajectories(DisposablePlanetarium planetarium,
                                       string main_vessel_guid,
                                       double history_length,
                                       CelestialBody root,
                                       double tan_angular_resolution,
                                       string frame_key,
                                       bool is_inertial_tree,
                                       bool time_is_advancing,
                                       double current_time_ut,
                                       double current_realtime,
                                       CelestialBody anchor,
                                       bool frame_hides_unpinned) {
    if (!celestial_trajectory_entries_.TryGetValue(root, out var entries)) {
      entries = celestial_trajectory_entries_[root] = new CelestialTrajectories();
    }
    var past_entry = entries.past;
    var future_entry = entries.future;

    var colour = root.orbitDriver?.Renderer?.orbitColor ??
        XKCDColors.SunshineYellow;
    var camera_world_position = ScaledSpace.ScaledToLocalSpace(
        PlanetariumCamera.fetch.transform.position);
    double min_distance_from_camera =
        (root.position - camera_world_position).magnitude;

    bool show_this_body =
        adapter_.show_celestial_trajectory(root, frame_hides_unpinned);
    bool is_warping = TimeWarp.CurrentRate > 1.0f;
    double celestial_interval = is_warping ? 0.50 : 2.0;

    if (!adapter_.plotting_frame_selector_.FixesBody(root) &&
        show_this_body) {
      if (!TryConsumeCelestialRefresh(past_entry,
                                      celestial_interval,
                                      time_is_advancing,
                                      current_realtime)) {
        if (past_entry.has_content) {
          past_entry.Draw();
          if (!double.IsNaN(past_entry.min_distance_from_camera) && past_entry.min_distance_from_camera > 0.0) {
            min_distance_from_camera =
                Math.Min(min_distance_from_camera,
                         past_entry.min_distance_from_camera);
          }
        }
      } else {
        int vertex_count;
        using (PerformanceProbe.Measure(ProbeCategory.CppPlotting)) {
          planetarium.PlanetariumPlotCelestialPastTrajectory(
              Plugin,
              root.flightGlobalsIndex,
              main_vessel_guid,
              history_length,
              VertexBuffer.data,
              VertexBuffer.size,
              out double min_past_distance,
              out vertex_count);
          if (!double.IsNaN(min_past_distance) && min_past_distance > 0.0) {
            min_distance_from_camera =
                Math.Min(min_distance_from_camera, min_past_distance);
            past_entry.min_distance_from_camera = min_past_distance;
          }
        }
        past_entry.last_update_ut = current_time_ut;
        past_entry.last_update_realtime = current_realtime;
        past_entry.UpdateMesh(vertex_count,
                              colour,
                              GLLines.Style.Faded,
                              shared_indices_,
                              anchor);
        past_entry.Draw();
      }

      if (main_vessel_guid != null && Plugin.HasVessel(main_vessel_guid)) {
        if (!TryConsumeCelestialRefresh(future_entry,
                                        celestial_interval,
                                        time_is_advancing,
                                        current_realtime)) {
          if (future_entry.has_content) {
            future_entry.Draw();
            if (!double.IsNaN(future_entry.min_distance_from_camera) && future_entry.min_distance_from_camera > 0.0) {
              min_distance_from_camera =
                  Math.Min(min_distance_from_camera,
                           future_entry.min_distance_from_camera);
            }
          }
        } else {
          int vertex_count;
          using (PerformanceProbe.Measure(ProbeCategory.CppPlotting)) {
            planetarium.PlanetariumPlotCelestialFutureTrajectory(
                Plugin,
                root.flightGlobalsIndex,
                main_vessel_guid,
                VertexBuffer.data,
                VertexBuffer.size,
                out double min_future_distance,
                out vertex_count);
            if (!double.IsNaN(min_future_distance) && min_future_distance > 0.0) {
              min_distance_from_camera =
                  Math.Min(min_distance_from_camera, min_future_distance);
              future_entry.min_distance_from_camera = min_future_distance;
            }
          }
          future_entry.last_update_ut = current_time_ut;
          future_entry.last_update_realtime = current_realtime;
          future_entry.UpdateMesh(vertex_count,
                                  colour,
                                  GLLines.Style.Solid,
                                  shared_indices_,
                                  anchor);
          future_entry.Draw();
        }
      }
    }
    double safe_camera_distance =
        !double.IsNaN(min_distance_from_camera) && min_distance_from_camera > 0.0
            ? min_distance_from_camera
            : Math.Max((root.position - camera_world_position).magnitude, 1.0);
    foreach (CelestialBody child in root.orbitingBodies) {
      if (child.orbit != null &&
          child.orbit.ApR / safe_camera_distance >
              2.0 * tan_angular_resolution) {
        PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                                child, tan_angular_resolution,
                                frame_key, is_inertial_tree, time_is_advancing,
                                current_time_ut, current_realtime, anchor,
                                frame_hides_unpinned);
      }
    }
  }

  public void InvalidatePrediction(bool immediate = false) {
    prediction_entry_.Invalidate(immediate);
  }

  public void InvalidateTargetPrediction(bool immediate = false) {
    target_prediction_entry_.Invalidate(immediate);
  }

  public void InvalidateAll(string reason) {
    frame_key_dirty_ = true;
    last_camera_distance_ = -1f;
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
    foreach (var pair in celestial_trajectory_entries_) {
      pair.Value.past.Invalidate();
      pair.Value.future.Invalidate();
    }
  }

  private void PlotPsychohistoryDirect(
      DisposablePlanetarium planetarium,
      string vessel_guid,
      double history_length,
      double? prediction_t_max,
      out int vertex_count) {
    using (PerformanceProbe.Measure(ProbeCategory.CppPlotting)) {
      planetarium.PlanetariumPlotPsychohistory(
          Plugin,
          vessel_guid,
          history_length,
          prediction_t_max,
          VertexBuffer.data,
          VertexBuffer.size,
          out vertex_count);
    }
  }

  private void PlotPredictionDirect(
      DisposablePlanetarium planetarium,
      string vessel_guid,
      double? prediction_t_max,
      out int vertex_count) {
    using (PerformanceProbe.Measure(ProbeCategory.CppPlotting)) {
      planetarium.PlanetariumPlotPrediction(
          Plugin,
          vessel_guid,
          prediction_t_max,
          VertexBuffer.data,
          VertexBuffer.size,
          out vertex_count);
    }
  }

  private void PlotFlightPlanSegmentDirect(
      DisposablePlanetarium planetarium,
      string vessel_guid,
      int segment_index,
      double? flight_plan_t_max,
      out int vertex_count) {
    using (PerformanceProbe.Measure(ProbeCategory.CppPlotting)) {
      planetarium.PlanetariumPlotFlightPlanSegment(
          Plugin,
          vessel_guid,
          segment_index,
          flight_plan_t_max,
          VertexBuffer.data,
          VertexBuffer.size,
          out vertex_count);
    }
  }

  internal class CachedMeshEntry {
    public UnityEngine.Mesh mesh;
    public UnityEngine.Vector3[] compact_vertices;
    public UnityEngine.Color[] compact_colors;
    public int vertex_count;
    public bool is_valid;
    public bool has_content;
    public bool immediate_rebuild_requested;
    public double last_update_ut;
    public double last_update_realtime;
    public double min_distance_from_camera;

    public CelestialBody reference_body;
    public UnityEngine.Vector3 reference_scaled_position;

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

    public void SetReferenceAnchor(CelestialBody body) {
      reference_body = body;
      if (body != null) {
        reference_scaled_position = ScaledSpace.LocalToScaledSpace(body.position);
      } else {
        reference_scaled_position = UnityEngine.Vector3.zero;
      }
    }

    public void UpdateMesh(int count,
                           UnityEngine.Color colour,
                           GLLines.Style style,
                           int[] shared_indices,
                           CelestialBody anchor = null) {
      immediate_rebuild_requested = false;
      this.style = style;
      this.base_color = colour;
      SetReferenceAnchor(anchor);

      if (count < 2) {
        if (mesh != null) {
          mesh.Clear();
        }
        vertex_count = 0;
        is_valid = true;
        has_content = false;
        return;
      }

      int effective_count = count;
      if (compact_vertices == null || compact_vertices.Length != effective_count) {
        compact_vertices = new UnityEngine.Vector3[effective_count];
      }
      Array.Copy(VertexBuffer.vertices, compact_vertices, effective_count);

      vertex_count = effective_count;

      mesh_scaled_space_origin = ScaledSpace.ScaledToLocalSpace(Vector3d.zero);
      if (PlanetariumCamera.Camera != null) {
        mesh_camera_forward = PlanetariumCamera.Camera.transform.forward;
      }

      EnsureMesh();

      if (compact_colors == null || compact_colors.Length != effective_count) {
        compact_colors = new UnityEngine.Color[effective_count];
      }

      if (style == GLLines.Style.Faded) {
        float inv_count = 1.0f / effective_count;
        for (int i = 0; i < effective_count; ++i) {
          var faded_colour = colour;
          faded_colour.a *= 1.0f - 0.8f * (i * inv_count);
          compact_colors[i] = faded_colour;
        }
      } else {
        for (int i = 0; i < effective_count; ++i) {
          compact_colors[i] = colour;
        }
      }

      mesh.Clear();
      mesh.vertices = compact_vertices;
      mesh.colors = compact_colors;

      int index_count = style == GLLines.Style.Dashed ? effective_count & ~1 : effective_count;
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
      for (int i = 0; i < effective_count; ++i) {
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

      is_valid = true;
      has_content = true;
    }

    public void Draw() {
      if (!has_content || vertex_count < 2) {
        return;
      }

      UnityEngine.Vector3 draw_position = UnityEngine.Vector3.zero;
      if (reference_body != null) {
        UnityEngine.Vector3 current_scaled_pos =
            ScaledSpace.LocalToScaledSpace(reference_body.position);
        draw_position = current_scaled_pos - reference_scaled_position;
      }

      UnityEngine.Graphics.DrawMesh(
          mesh,
          draw_position,
          UnityEngine.Quaternion.identity,
          GLLines.line_material,
          (int)PrincipiaPluginAdapter.UnityLayers.Atmosphere,
          PlanetariumCamera.Camera);
    }

    public void Invalidate(bool immediate = false) {
      is_valid = false;
      if (immediate) {
        immediate_rebuild_requested = true;
      }
    }
  }

  private static UnityEngine.Mesh MakeDynamicMesh() {
    var result = new UnityEngine.Mesh();
    result.MarkDynamic();
    return result;
  }

  private static int[] GetSharedIndices(int count) {
    if (shared_indices_ == null || shared_indices_.Length < count) {
      int new_size = Math.Max(count, shared_indices_ == null ? 65536 : shared_indices_.Length * 2);
      shared_indices_ = new int[new_size];
      for (int i = 0; i < new_size; ++i) {
        shared_indices_[i] = i;
      }
    }
    return shared_indices_;
  }

  private readonly PrincipiaPluginAdapter adapter_;

  private IntPtr Plugin => adapter_.Plugin();

  private static class VertexBuffer {
    public static IntPtr data => handle_.AddrOfPinnedObject();
    public static int size => vertices_.Length;

    public static UnityEngine.Vector3[] vertices => vertices_;

    private static readonly UnityEngine.Vector3[] vertices_ =
        new UnityEngine.Vector3[65536];
    private static GCHandle handle_ =
        GCHandle.Alloc(vertices_, GCHandleType.Pinned);
  }

  private class CelestialTrajectories {
    public CachedMeshEntry past = new CachedMeshEntry();
    public CachedMeshEntry future = new CachedMeshEntry();
  }

  private readonly Dictionary<CelestialBody, CelestialTrajectories>
      celestial_trajectory_entries_ =
      new Dictionary<CelestialBody, CelestialTrajectories>();

  private readonly CachedMeshEntry psychohistory_entry_ = new CachedMeshEntry();
  private readonly CachedMeshEntry prediction_entry_ = new CachedMeshEntry();
  private readonly CachedMeshEntry target_psychohistory_entry_ = new CachedMeshEntry();
  private readonly CachedMeshEntry target_prediction_entry_ = new CachedMeshEntry();

  private readonly List<CachedMeshEntry> flight_plan_segment_entries_ =
      new List<CachedMeshEntry>();
  private readonly List<CachedMeshEntry> equipotential_entries_ =
      new List<CachedMeshEntry>();

  private static int[] shared_indices_ = null;

  private double last_equipotential_update_realtime_ = -1.0;
  private string last_main_vessel_guid_ = null;
  private string last_target_id_ = null;
  private string last_frame_key_ = null;
  private bool frame_key_dirty_ = false;
  private float last_camera_distance_ = -1f;
  private double last_camera_zoom_realtime_ = 0.0;
  private double last_history_length_ = -1.0;
  private double? last_prediction_t_max_ = null;
  private double? last_flight_plan_t_max_ = null;

  private double psychohistory_rebuild_ms_ema_ = 2.0;

  private const int MaxCelestialRefreshesPerFrame = 2;
  private const int MaxVesselRefreshesPerFrame = 2;
  private int celestial_refresh_budget_ = MaxCelestialRefreshesPerFrame;
  private int vessel_refresh_budget_ = MaxVesselRefreshesPerFrame;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
