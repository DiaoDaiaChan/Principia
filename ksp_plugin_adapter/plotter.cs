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

    // Equipotentials are static in the rotating frame and move with the central body.
    // When paused: 100% reuse (0 ms CPU).
    // During 1x physics: refresh at 2 Hz (0.50s interval). Between intervals, 100% GPU reuse with anchor offset (0 ms CPU).
    // When time warping: refresh at 4 Hz (0.25s interval).  The interval must
    // stay above the frame period or every entry goes stale every frame and
    // the cache degenerates to a full re-plot per frame (audit 02, R1).
    double eq_interval = is_warping ? 0.25 : 0.50;
    bool eq_interval_elapsed =
        current_realtime - last_equipotential_update_realtime_ >= eq_interval;

    for (int i = 0; i < number_of_equipotentials; ++i) {
      var entry = equipotential_entries_[i];
      // Reuse whenever there is drawable content and either the scene is
      // static (paused) or the refresh interval has not elapsed.  An
      // invalidated entry keeps drawing its stale mesh until the interval
      // elapses, so repeated InvalidateAll() calls cannot force a rebuild
      // every frame.
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
    // Meshes live in ScaledSpace and are completely invariant to
    // Krakensbane/FloatingOrigin shifts (those only affect LocalSpace).
    // The C++ Planetarium is rebuilt every frame with the fresh origin, so
    // cached meshes stay valid across origin shifts.  Never invalidate here:
    // this previously called InvalidateAll() whenever the origin moved by
    // more than 1 m, which wiped every cache every few seconds in orbit and
    // caused cyclic 40-90 ms frame spikes.

    if (main_vessel_guid == null) {
      // Transient loss of the predicted vessel (e.g. the plugin momentarily
      // not knowing the vessel): keep the caches warm and skip the vessel
      // paths below.  Previously a null here flipped
      // last_main_vessel_guid_, so a flapping null wiped every cache on the
      // next frame.
    } else if (main_vessel_guid != last_main_vessel_guid_) {
      InvalidateAll("vessel-change");
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
      // Per-frame refresh budget for celestial trajectories.  Without it,
      // every overdue entry (potentially 2 × ~17 bodies) is re-plotted in a
      // single frame through the C++ planetarium, which is what produced the
      // 60-90 ms Plotting spikes: see audit report
      // 02_celestial_trajectory_cache_invalidation_root_cause.md (R1/R2).
      celestial_refresh_budget_ = MaxCelestialRefreshesPerFrame;
      PlotCelestialTrajectories(planetarium, main_vessel_guid, history_length);
    }
    using (PerformanceProbe.Measure(ProbeCategory.VesselPlot)) {
      vessel_refresh_budget_ = MaxVesselRefreshesPerFrame;
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
    // Psychohistory refresh throttle.  The interval must stay comfortably
    // above the expected frame period: with a 0.05 s warp interval, any frame
    // slower than 50 ms made every entry overdue every frame, so the cache
    // never hit and the refresh cost itself kept the frame rate low (the
    // positive feedback loop documented as R1 in audit report 02).  Warp now
    // uses 0.25 s; endpoint lag at warp is accepted (the anchor offset in
    // Draw() keeps the trail glued to the reference body).
    //
    // The interval is additionally self-tuning: the rebuild cost grows with
    // history_length and vertex count (a 7-day psychohistory costs ~5-8 ms
    // to re-plot), so stretch the interval with the measured rebuild cost to
    // keep the amortized rebuild overhead below ~8% of the frame time,
    // bounded to [0.20 s, 1.00 s].
    double psychohistory_interval = Math.Max(
        is_warping ? 0.50 : 0.40,
        Math.Min(1.0, psychohistory_rebuild_ms_ema_ * 0.08));

    if (main_vessel_guid != null) {
      // Psychohistory (绿色历史轨迹):
      // Rebuild at most once per interval, budgeted to avoid multi-rebuild frame spikes.
      bool psychohistory_needs_rebuild = NeedsRebuild(psychohistory_entry_,
                                                      psychohistory_interval,
                                                      time_is_advancing,
                                                      current_realtime);
      if (psychohistory_needs_rebuild &&
          (!psychohistory_entry_.has_content || vessel_refresh_budget_ > 0)) {
        if (psychohistory_entry_.has_content) {
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

      // Prediction (粉红色实际预测轨迹):
      // Once calculated by C++ numerical integration, the prediction trajectory curve in 3D space
      // is completely static! It is REUSED from GPU VRAM across all frames.
      const double prediction_rebuild_floor = 0.20;
      bool prediction_floor_elapsed =
          current_realtime - prediction_entry_.last_update_realtime >=
          prediction_rebuild_floor;
      bool prediction_needs_rebuild = !prediction_entry_.has_content ||
          (!prediction_entry_.is_valid && prediction_floor_elapsed);
      if (prediction_needs_rebuild &&
          (!prediction_entry_.has_content || vessel_refresh_budget_ > 0)) {
        if (prediction_entry_.has_content) {
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
          // A user edit (fp_dirty) rebuilds immediately for responsiveness;
          // background invalidation is smoothly budgeted across successive frames.
          bool fp_floor_elapsed =
              current_realtime - entry.last_update_realtime >=
              fp_rebuild_floor;
          bool segment_needs_rebuild = !entry.has_content ||
              fp_dirty ||
              (!entry.is_valid && fp_floor_elapsed);

          if (segment_needs_rebuild &&
              (fp_dirty || !entry.has_content || vessel_refresh_budget_ > 0)) {
            if (entry.has_content && !fp_dirty) {
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
        target_psychohistory_entry_.Invalidate();
        target_prediction_entry_.Invalidate();
        last_target_id_ = target_id;
      }

      bool target_psychohistory_needs_rebuild =
          NeedsRebuild(target_psychohistory_entry_,
                       psychohistory_interval,
                       time_is_advancing,
                       current_realtime);
      if (target_psychohistory_needs_rebuild &&
          (!target_psychohistory_entry_.has_content || vessel_refresh_budget_ > 0)) {
        if (target_psychohistory_entry_.has_content) {
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
          (!target_prediction_entry_.is_valid && target_prediction_floor_elapsed);
      if (target_prediction_needs_rebuild &&
          (!target_prediction_entry_.has_content || vessel_refresh_budget_ > 0)) {
        if (target_prediction_entry_.has_content) {
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

  // Decides whether a vessel-side entry needs a rebuild this frame.  The
  // rebuild is gated on the refresh interval having elapsed since the last
  // rebuild, so a caller that spam-invalidates entries (e.g. repeated
  // InvalidateAll) cannot force a rebuild every frame: the entry keeps
  // drawing its last mesh (has_content) and rebuilds at most once per
  // interval.  `interval` therefore acts both as the periodic refresh rate
  // (while time advances) and as the invalidation-rebuild floor.
  private static bool NeedsRebuild(CachedMeshEntry entry,
                                   double interval,
                                   bool time_is_advancing,
                                   double current_realtime) {
    if (!entry.has_content) {
      return true;  // Nothing to draw yet: build immediately.
    }
    if (entry.is_valid && !time_is_advancing) {
      return false;  // Paused with fresh content: reuse forever.
    }
    bool interval_elapsed =
        current_realtime - entry.last_update_realtime >= interval;
    return interval_elapsed && (!entry.is_valid || time_is_advancing);
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
    CelestialBody anchor = CurrentReferenceAnchor;
    // Whether the current frame hides unpinned celestial trajectories.
    // Queried once per frame: show_celestial_trajectory() used to call
    // FrameParameters() per body per frame, allocating a fresh
    // PlottingFrameParameters and two int[] arrays for every celestial
    // (audit report 02, R5).
    bool frame_hides_unpinned = adapter_.FrameHidesUnpinnedCelestials();
    PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                            Planetarium.fetch.Sun, TanAngularResolution(),
                            frame_key, is_inertial_tree, time_is_advancing,
                            current_time_ut, current_realtime, anchor,
                            frame_hides_unpinned);
  }

  // Decides whether a celestial cache entry may be re-plotted this frame.
  // Guarantees:
  //   * at most MaxCelestialRefreshesPerFrame re-plots per frame, so the cost
  //     of refreshing is amortized instead of bursting every entry in a
  //     single frame (audit report 02, R2);
  //   * entries older than CriticalStalenessMultiplier × interval always
  //     refresh, so a body can never starve when the budget is saturated by
  //     entries that come earlier in tree order;
  //   * not-yet-built entries count against the budget too, so the initial
  //     map-view fill is spread over several frames rather than one 80+ ms
  //     hitch;
  //   * an invalidated entry keeps drawing its stale mesh until the refresh
  //     interval elapses, so repeated InvalidateAll() calls cannot force a
  //     rebuild every frame (the anchor offset in Draw() keeps the stale
  //     mesh glued to the reference body, so it is far better than a frame
  //     spike).
  private bool TryConsumeCelestialRefresh(CachedMeshEntry entry,
                                          double interval,
                                          bool time_is_advancing,
                                          double current_realtime) {
    double age = current_realtime - entry.last_update_realtime;
    if (entry.has_content) {
      if (!time_is_advancing) {
        return false;  // Paused: draw whatever we have.
      }
      if (age < interval) {
        return false;  // Fresh, or stale-but-recently-rebuilt (e.g. just
                       // invalidated by a spam caller): keep drawing.
      }
    }
    // The entry needs a rebuild: it has no drawable content yet, or its
    // content is older than the refresh interval.
    bool critically_stale =
        entry.has_content && age >= CriticalStalenessMultiplier * interval;
    if (celestial_refresh_budget_ > 0) {
      --celestial_refresh_budget_;
      return true;
    }
    return critically_stale;
  }

  // Plots the trajectories of `root` and its natural satellites.
  // The per-frame invariants (`frame_key`, `is_inertial_tree`,
  // `time_is_advancing`, `current_time_ut`, `current_realtime`, `anchor`,
  // `frame_hides_unpinned`) are hoisted to the caller and threaded through
  // the recursion, since recomputing them per body allocated objects on
  // every visit and queried the universal time once per celestial.
  private void PlotSubtreeTrajectories(DisposablePlanetarium planetarium,
                                       string main_vessel_guid,
                                       double history_length,
                                       CelestialBody root,
                                       double tan_angular_resolution,
                                       FrameKey frame_key,
                                       bool is_inertial_tree,
                                       bool time_is_advancing,
                                       double current_time_ut,
                                       double current_realtime,
                                       CelestialBody anchor,
                                       bool frame_hides_unpinned) {
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

    bool show_this_body =
        adapter_.show_celestial_trajectory(root, frame_hides_unpinned);
    bool is_warping = TimeWarp.CurrentRate > 1.0f;
    // The warp interval must stay well above the expected frame period.  The
    // previous 0.05 s value was shorter than the frame time under load, so
    // every entry went overdue on every frame and the cache degenerated into
    // a full re-plot per frame (61 ms Celestial): audit report 02, R1.
    double celestial_interval = is_warping ? 0.50 : 2.0;

    if (!adapter_.plotting_frame_selector_.FixesBody(root) &&
        show_this_body) {
      // Past trajectory:
      // When paused: 100% reuse (0 ms CPU).
      // During 1x physics: celestial orbit is fixed relative to parent body, reuse with anchor offset (0 ms CPU).
      // Refreshed at 0.5 Hz (2.0s interval) or 2 Hz during warp, at most
      // MaxCelestialRefreshesPerFrame entries per frame; over-budget entries
      // keep drawing their stale mesh.
      if (!TryConsumeCelestialRefresh(past_entry,
                                      celestial_interval,
                                      time_is_advancing,
                                      current_realtime)) {
        if (past_entry.has_content) {
          CelestialTrajectoryBinaryCache.RecordL1Hit();
          past_entry.Draw();
          min_distance_from_camera =
              Math.Min(min_distance_from_camera,
                       past_entry.min_distance_from_camera);
        }
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
                              shared_indices_,
                              anchor);
        past_entry.Draw();
        CelestialTrajectoryBinaryCache.RecordMiss();
      }

      if (main_vessel_guid != null && Plugin.HasVessel(main_vessel_guid)) {
        // Future trajectory: same budgeted refresh policy as the past arc.
        if (!TryConsumeCelestialRefresh(future_entry,
                                        celestial_interval,
                                        time_is_advancing,
                                        current_realtime)) {
          if (future_entry.has_content) {
            CelestialTrajectoryBinaryCache.RecordL1Hit();
            future_entry.Draw();
            min_distance_from_camera =
                Math.Min(min_distance_from_camera,
                         future_entry.min_distance_from_camera);
          }
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
                                  shared_indices_,
                                  anchor);
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
                                current_time_ut, current_realtime, anchor,
                                frame_hides_unpinned);
      }
    }
  }

  public void InvalidatePrediction() {
    prediction_entry_.Invalidate();
  }

  public void InvalidateTargetPrediction() {
    target_prediction_entry_.Invalidate();
  }

  public void InvalidateAll(string reason) {
    PerformanceProbe.RecordPlotInvalidation(reason);
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
    // NOTE: last_equipotential_update_realtime_ is intentionally NOT reset
    // here.  Resetting it made the equipotential refresh interval elapse
    // immediately, so every InvalidateAll() forced an equipotential rebuild
    // on the very next frame.  Keeping it lets the interval floor absorb
    // invalidation spam; on a genuine frame change the (brief) stale
    // equipotential draw is corrected at the next interval boundary.
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
    // is_valid: content is fresh (no rebuild needed).  has_content: the mesh
    // holds drawable geometry, possibly stale.  Invalidate() clears only
    // is_valid, so invalidated entries keep rendering their last mesh while
    // awaiting a throttled rebuild; this is what makes repeated
    // InvalidateAll() calls cheap.
    public bool is_valid;
    public bool has_content;
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
      using (PerformanceProbe.Measure(ProbeCategory.MeshGeneration)) {
        PerformanceProbe.RecordCacheMiss();
        HeterogeneousDynamicGovernor.RecordCpuDispatch();
        vertex_count = count;
        this.style = style;
        this.base_color = colour;
        SetReferenceAnchor(anchor);

        if (count <= 0) {
          if (mesh != null) {
            mesh.Clear();
          }
          is_valid = true;
          has_content = false;
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
        has_content = true;
      }
    }

    public void Draw() {
      if (!has_content || vertex_count <= 0) {
        return;
      }
      using (PerformanceProbe.Measure(ProbeCategory.GpuCompute)) {
        EnsureMesh();
        if (mesh != null) {
          PerformanceProbe.RecordCacheHit();
          PerformanceProbe.RecordVertices(vertex_count);
          HeterogeneousDynamicGovernor.RecordGpuDispatch();

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
      }
    }

    public void SetMeshDirect(UnityEngine.Vector3[] vertices,
                              UnityEngine.Color[] colors,
                              int count,
                              double min_distance,
                              double update_ut,
                              Vector3d scaled_origin,
                              GLLines.Style style,
                              CelestialBody anchor = null) {
      vertex_count = count;
      this.style = style;
      if (colors != null && colors.Length > 0) {
        this.base_color = colors[0];
      }
      min_distance_from_camera = min_distance;
      last_update_ut = update_ut;
      last_update_realtime = UnityEngine.Time.realtimeSinceStartup;
      mesh_scaled_space_origin = scaled_origin;
      SetReferenceAnchor(anchor);
      EnsureMesh();
      if (count <= 0) {
        mesh.Clear();
        is_valid = true;
        has_content = false;
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
      has_content = true;
    }

    public void Invalidate() {
      // Only mark the content stale.  The mesh, vertex arrays and reference
      // anchor are intentionally preserved: Draw() gates on has_content, so
      // the entry keeps rendering its last mesh (with the anchor offset)
      // while it awaits a throttled rebuild.  Repeated InvalidateAll()
      // calls therefore cost nothing until the rebuild interval elapses.
      is_valid = false;
    }

    public void Dispose() {
      if (mesh != null) {
        mesh.Clear();
      }
      is_valid = false;
      has_content = false;
      vertex_count = 0;
      reference_body = null;
      reference_scaled_position = UnityEngine.Vector3.zero;
    }

    public void Destroy() {
      if (mesh != null) {
        UnityEngine.Object.Destroy(mesh);
        mesh = null;
      }
      compact_vertices = null;
      compact_colors = null;
      is_valid = false;
      has_content = false;
      vertex_count = 0;
      reference_body = null;
      reference_scaled_position = UnityEngine.Vector3.zero;
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

  private string last_main_vessel_guid_ = null;
  private double last_history_length_ = -1.0;
  private string last_target_id_ = null;
  private double? last_prediction_t_max_ = null;
  private double? last_flight_plan_t_max_ = null;
  private double last_equipotential_update_realtime_ = -1.0;
  private FrameKey cached_frame_key_;
  private bool frame_key_dirty_ = true;

  // Per-frame budget for celestial trajectory re-plots (P/Invoke + mesh
  // rebuild).  Reset at the start of every PlotTrajectories call.  An entry
  // whose staleness exceeds CriticalStalenessMultiplier × interval always
  // refreshes regardless of the budget, preventing starvation.
  private const int MaxCelestialRefreshesPerFrame = 2;
  private const double CriticalStalenessMultiplier = 4.0;
  private int celestial_refresh_budget_ = MaxCelestialRefreshesPerFrame;

  // Per-frame budget for vessel trajectory re-plots (P/Invoke + mesh rebuild).
  // Without it, every invalidated or overdue vessel path (psychohistory,
  // prediction, multi-segment flight plan, target psychohistory/prediction)
  // rebuilds synchronously in a single frame, spiking frame times to 15+ ms.
  // With a budget, at most 1 heavy vessel entry rebuilds per frame while
  // remaining entries smoothly draw their cached meshes.
  private const int MaxVesselRefreshesPerFrame = 1;
  private int vessel_refresh_budget_ = MaxVesselRefreshesPerFrame;

  // EMA of the measured psychohistory rebuild cost (P/Invoke + mesh rebuild),
  // in milliseconds.  Feeds the self-tuning psychohistory refresh interval.
  private double psychohistory_rebuild_ms_ema_ = 0.0;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
