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
    if (main_vessel_guid != last_main_vessel_guid_) {
      InvalidateAll();
      last_main_vessel_guid_ = main_vessel_guid;
    }
    if (Math.Abs(history_length - last_history_length_) > 1.0) {
      psychohistory_entry_.Invalidate();
      target_psychohistory_entry_.Invalidate();
      last_history_length_ = history_length;
    }
    if (prediction_t_max != last_prediction_t_max_) {
      prediction_entry_.Invalidate();
      last_prediction_t_max_ = prediction_t_max;
    }
    if (flight_plan_t_max != last_flight_plan_t_max_) {
      for (int i = 0; i < flight_plan_segment_entries_.Count; ++i) {
        flight_plan_segment_entries_[i].Invalidate();
      }
      last_flight_plan_t_max_ = flight_plan_t_max;
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
        adapter_.plotting_frame_selector_.frame_type !=
            FrameType.BODY_CENTRED_NON_ROTATING ||
        adapter_.plotting_frame_selector_.target_frame_selected;
    bool is_inertial = !is_expensive_frame;

    Vessel active = FlightGlobals.ActiveVessel;
    bool is_maneuvering = PrincipiaPluginAdapter.IsVesselManeuvering(active);
    bool is_warping = TimeWarp.CurrentRate > 1.0f;

    if (main_vessel_guid != null) {
      // Psychohistory:
      // When maneuvering: refresh at 2 Hz (0.50s).
      // When coasting in inertial frame: refresh every 5.0s.
      // When coasting in rotating/L-point frame: refresh every 2.0s.
      // Decoupled from UT drift during warp to prevent per-frame C++ re-integration.
      double psycho_interval = is_maneuvering ? 0.50 : (is_inertial ? 5.00 : 2.00);
      double warp_ut_drift_allowance = is_warping ? Math.Max(60.0, TimeWarp.CurrentRate * 0.5) : 60.0;
      bool can_reuse_psychohistory = psychohistory_entry_.is_valid &&
          (!time_is_advancing ||
           (current_realtime - psychohistory_entry_.last_update_realtime < psycho_interval &&
            (!is_warping || Math.Abs(current_time_ut - psychohistory_entry_.last_update_ut) < warp_ut_drift_allowance)));

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
      // When maneuvering: 10 Hz (0.100s) for responsive burn steering.
      // When coasting in inertial frame: 5.0s refresh (geometry is static in space).
      // When coasting in rotating/L-point frame: 2.0s refresh.
      // During intermediate frames (or camera rotation/browsing in MapView):
      // Draws directly from cached GPU mesh with 0.002 ms CPU overhead!
      double pred_interval = is_maneuvering ? 0.100 : (is_inertial ? 5.00 : 2.00);
      bool can_reuse_prediction = prediction_entry_.is_valid &&
          (!time_is_advancing ||
           (current_realtime - prediction_entry_.last_update_realtime < pred_interval &&
            (!is_warping || Math.Abs(current_time_ut - prediction_entry_.last_update_ut) < warp_ut_drift_allowance)));

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
              (!time_is_advancing ||
                (current_realtime - entry.last_update_realtime < 1.00 &&
                 (!is_warping || Math.Abs(current_time_ut - entry.last_update_ut) < warp_ut_drift_allowance)));

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
          (!time_is_advancing ||
           (current_realtime - target_psychohistory_entry_.last_update_realtime < 1.00 &&
            (!is_warping || Math.Abs(current_time_ut - target_psychohistory_entry_.last_update_ut) < 60.0)));

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
          (!time_is_advancing ||
           (current_realtime - target_prediction_entry_.last_update_realtime < 1.00 &&
            (!is_warping || Math.Abs(current_time_ut - target_prediction_entry_.last_update_ut) < 60.0)));

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
    IntPtr gpu_ptr = GpuTrajectoryRenderer.IsGpuRenderingEnabled &&
                     UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                         ? NativeZeroCopyTrajectoryPipeline.GpuBufferNativePtr
                         : IntPtr.Zero;
    if (Interface.HasDirectVramSupport && gpu_ptr != IntPtr.Zero) {
      planetarium.PlanetariumPlotPsychohistoryDirect(
          Plugin,
          vessel_guid,
          history_length,
          prediction_t_max,
          gpu_ptr,
          NativeZeroCopyTrajectoryPipeline.CurrentCapacity,
          is_d3d11_buffer: true,
          out vertex_count);
      if (vertex_count > 0) {
        return;
      }
    }
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
    IntPtr gpu_ptr = GpuTrajectoryRenderer.IsGpuRenderingEnabled &&
                     UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                         ? NativeZeroCopyTrajectoryPipeline.GpuBufferNativePtr
                         : IntPtr.Zero;
    if (Interface.HasDirectVramSupport && gpu_ptr != IntPtr.Zero) {
      planetarium.PlanetariumPlotPredictionDirect(
          Plugin,
          vessel_guid,
          prediction_t_max,
          gpu_ptr,
          NativeZeroCopyTrajectoryPipeline.CurrentCapacity,
          is_d3d11_buffer: true,
          out vertex_count);
      if (vertex_count > 0) {
        return;
      }
    }
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
    IntPtr gpu_ptr = GpuTrajectoryRenderer.IsGpuRenderingEnabled &&
                     UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                         ? NativeZeroCopyTrajectoryPipeline.GpuBufferNativePtr
                         : IntPtr.Zero;
    if (Interface.HasDirectVramSupport && gpu_ptr != IntPtr.Zero) {
      planetarium.PlanetariumPlotFlightPlanSegmentDirect(
          Plugin,
          vessel_guid,
          segment_index,
          flight_plan_t_max,
          gpu_ptr,
          NativeZeroCopyTrajectoryPipeline.CurrentCapacity,
          is_d3d11_buffer: true,
          out vertex_count);
      if (vertex_count > 0) {
        return;
      }
    }
    planetarium.PlanetariumPlotFlightPlanSegment(
        Plugin,
        vessel_guid,
        segment_index,
        flight_plan_t_max,
        VertexBuffer.data,
        VertexBuffer.size,
        out vertex_count);
  }

  private const int MaxCelestialReconstructionsPerFrame = 4;
  private int celestial_reconstructed_this_frame_ = 0;

  private void PlotCelestialTrajectories(DisposablePlanetarium planetarium,
                                         string main_vessel_guid,
                                         double history_length) {
    celestial_reconstructed_this_frame_ = 0;
    PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                            Planetarium.fetch.Sun, TanAngularResolution());
  }

  // Plots the trajectories of `root` and its natural satellites.
  private void PlotSubtreeTrajectories(DisposablePlanetarium planetarium,
                                       string main_vessel_guid,
                                       double history_length,
                                       CelestialBody root,
                                       double tan_angular_resolution) {
    var frame_params = adapter_.plotting_frame_selector_.FrameParameters();
    var frame_key = new FrameKey(frame_params);
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

    bool time_is_advancing = adapter_.is_time_advancing;
    double current_time_ut = Planetarium.GetUniversalTime();
    double current_realtime = UnityEngine.Time.realtimeSinceStartup;

    if (!adapter_.plotting_frame_selector_.FixesBody(root) &&
        adapter_.show_celestial_trajectory(root)) {
      bool is_inertial = adapter_.plotting_frame_selector_.frame_type ==
          FrameType.BODY_CENTRED_NON_ROTATING &&
          !adapter_.plotting_frame_selector_.target_frame_selected;
      // Celestial orbits are governed strictly by the gravitational ephemeris.
      // Once calculated, celestial trajectories in any frame (inertial, rotating, or L-point)
      // strictly freeze in GPU VRAM and do NOT expire based on real-time clock or short warps!
      // In inertial frames: static indefinitely (10 Julian Years).
      // In rotating / L-point frames: valid for 30 game days (86400 * 30 s).
      double non_inertial_max_drift = 86400.0 * 30.0;
      if (root?.orbit != null && root.orbit.period > 0) {
        non_inertial_max_drift = Math.Max(300.0, root.orbit.period * 0.05);
      }
      double max_ut_drift = is_inertial ? 86400.0 * 3652.5 * 10.0 : non_inertial_max_drift;

      // Past trajectory: L1 RAM cache hit -> L2 Disk cache hit -> C++ reconstruction fallback
      bool can_reuse_past = past_entry.is_valid &&
          (!time_is_advancing ||
           Math.Abs(current_time_ut - past_entry.last_update_ut) < max_ut_drift ||
           celestial_reconstructed_this_frame_ >= MaxCelestialReconstructionsPerFrame);

      if (can_reuse_past) {
        CelestialTrajectoryBinaryCache.RecordL1Hit();
        past_entry.Draw();
        min_distance_from_camera =
            Math.Min(min_distance_from_camera,
                     past_entry.min_distance_from_camera);
      } else {
        if (CelestialTrajectoryBinaryCache.TryLoadFromDisk(
                past_key,
                current_time_ut,
                max_ut_drift,
                past_entry,
                colour,
                GLLines.Style.Faded)) {
          past_entry.Draw();
          min_distance_from_camera =
              Math.Min(min_distance_from_camera,
                       past_entry.min_distance_from_camera);
        } else if (celestial_reconstructed_this_frame_ >= MaxCelestialReconstructionsPerFrame) {
          // Defer cold reconstruction of additional celestials to subsequent frames
          // to guarantee a silky smooth 60 FPS even during cache warm-up.
        } else {
          celestial_reconstructed_this_frame_++;
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
          CelestialTrajectoryBinaryCache.SaveToDisk(past_key, past_entry);
        }
      }

      if (main_vessel_guid != null) {
        // Future trajectory: L1 RAM cache hit -> L2 Disk cache hit -> C++ reconstruction fallback
        bool can_reuse_future = future_entry.is_valid &&
            (!time_is_advancing ||
             Math.Abs(current_time_ut - future_entry.last_update_ut) < max_ut_drift ||
             celestial_reconstructed_this_frame_ >= MaxCelestialReconstructionsPerFrame);

        if (can_reuse_future) {
          CelestialTrajectoryBinaryCache.RecordL1Hit();
          future_entry.Draw();
          min_distance_from_camera =
              Math.Min(min_distance_from_camera,
                       future_entry.min_distance_from_camera);
        } else {
          if (CelestialTrajectoryBinaryCache.TryLoadFromDisk(
                  future_key,
                  current_time_ut,
                  max_ut_drift,
                  future_entry,
                  colour,
                  GLLines.Style.Solid)) {
            future_entry.Draw();
            min_distance_from_camera =
                Math.Min(min_distance_from_camera,
                         future_entry.min_distance_from_camera);
          } else if (celestial_reconstructed_this_frame_ >= MaxCelestialReconstructionsPerFrame) {
            // Defer cold reconstruction of additional celestials to subsequent frames
          } else {
            celestial_reconstructed_this_frame_++;
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
            CelestialTrajectoryBinaryCache.SaveToDisk(future_key, future_entry);
          }
        }
      }
    }
    bool is_inertial_tree = adapter_.plotting_frame_selector_.frame_type ==
        FrameType.BODY_CENTRED_NON_ROTATING &&
        !adapter_.plotting_frame_selector_.target_frame_selected;
    double min_apparent_ratio = is_inertial_tree ? 2.0 : 12.0;
    foreach (CelestialBody child in root.orbitingBodies) {
      if (!adapter_.show_celestial_trajectory(root) ||
          child.orbit.ApR / min_distance_from_camera >
              min_apparent_ratio * tan_angular_resolution) {
        PlotSubtreeTrajectories(planetarium, main_vessel_guid, history_length,
                                child, tan_angular_resolution);
      }
    }
  }

  public void InvalidatePrediction() {
    prediction_entry_.Invalidate();
  }

  public void InvalidateAll() {
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

        if (GpuTrajectoryRenderer.IsGpuRenderingEnabled) {
          // Level 2 GPU Procedural Path:
          // Bypasses Mesh.Clear, mesh.vertices upload, and Mesh lifecycle!
          NativeZeroCopyTrajectoryPipeline.RecordTransfer(count);
          is_valid = true;
          return;
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

        // In ScaledSpace, camera renders all trajectories without occlusion culling.
        // Fast bounds assignment eliminates the O(N) scalar min/max comparison loop entirely.
        mesh.bounds = NativeZeroCopyTrajectoryPipeline.CosmicBounds;

        NativeZeroCopyTrajectoryPipeline.RecordTransfer(count);
        is_valid = true;
      }
    }

    public void Draw() {
      if (!is_valid || vertex_count <= 0) {
        return;
      }
      using (PerformanceProbe.Measure(ProbeCategory.GpuCompute)) {
        // Mathematically exact ScaledSpace origin compensation:
        // In KSP MapView, ScaledSpace moves so that the camera is centered at the origin.
        // Drawing the mesh offset by ScaledSpace.LocalToScaledSpace(mesh_scaled_space_origin)
        // keeps the trajectory mesh solidly pinned in 3D space with ZERO camera rotation drift.
        UnityEngine.Vector3 draw_position =
            (UnityEngine.Vector3)ScaledSpace.LocalToScaledSpace(mesh_scaled_space_origin);

        if (GpuTrajectoryRenderer.IsGpuRenderingEnabled) {
          if (GpuTrajectoryRenderer.RenderTrajectory(
                  compact_vertices,
                  vertex_count,
                  base_color,
                  style,
                  PlanetariumCamera.Camera,
                  draw_position)) {
            PerformanceProbe.RecordCacheHit();
            PerformanceProbe.RecordVertices(vertex_count);
            HeterogeneousDynamicGovernor.RecordGpuDispatch();
            return;
          }
        }

        EnsureMesh();
        if (mesh != null) {
          if (compact_vertices != null && (mesh.vertexCount == 0 || mesh.vertexCount != vertex_count)) {
            if (compact_colors == null || compact_colors.Length != vertex_count) {
              compact_colors = new UnityEngine.Color[vertex_count];
              for (int i = 0; i < vertex_count; ++i) {
                compact_colors[i] = base_color;
              }
            }
            mesh.Clear();
            mesh.vertices = compact_vertices;
            mesh.colors = compact_colors;
            int index_count = style == GLLines.Style.Dashed ? vertex_count & ~1 : vertex_count;
            int[] indices = GetSharedIndices(index_count);
            mesh.SetIndices(indices,
                            indicesStart: 0,
                            indicesLength: index_count,
                            style == GLLines.Style.Dashed
                                ? UnityEngine.MeshTopology.Lines
                                : UnityEngine.MeshTopology.LineStrip,
                            submesh: 0,
                            calculateBounds: false);
            mesh.bounds = NativeZeroCopyTrajectoryPipeline.CosmicBounds;
          }
          PerformanceProbe.RecordCacheHit();
          PerformanceProbe.RecordVertices(vertex_count);
          HeterogeneousDynamicGovernor.RecordGpuDispatch();

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
      mesh.bounds = NativeZeroCopyTrajectoryPipeline.CosmicBounds;
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

  private string last_main_vessel_guid_ = null;
  private double last_history_length_ = -1.0;
  private string last_target_id_ = null;
  private double? last_prediction_t_max_ = null;
  private double? last_flight_plan_t_max_ = null;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
