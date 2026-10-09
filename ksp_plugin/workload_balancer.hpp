#pragma once

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <mutex>

#include "ksp_plugin/gpu_interop_types.hpp"
#include "ksp_plugin/hardware_profiler.hpp"

namespace principia {
namespace ksp_plugin {
namespace _workload_balancer {
namespace internal {

using _gpu_interop_types::KspSceneType;
using _gpu_interop_types::WorkloadBalanceState;
using _hardware_profiler::HardwareProfiler;

class WorkloadBalancer final {
 public:
  static WorkloadBalancer& Instance() {
    static WorkloadBalancer instance;
    return instance;
  }

  // Updates the scene context and frame timing telemetry from managed adapter (called at ~10-60 Hz).
  void UpdateSceneFeedback(
      int const scene_id,
      double const render_time_ms,
      double const physics_time_ms) {
    current_scene_.store(scene_id, std::memory_order_relaxed);

    // EWMA smoothing for render and physics frame times (alpha = 0.1)
    double const cur_render = smoothed_render_ms_.load(std::memory_order_relaxed);
    double const next_render = (cur_render * 0.9) + (std::max(0.001, render_time_ms) * 0.1);
    smoothed_render_ms_.store(next_render, std::memory_order_relaxed);

    double const cur_phys = smoothed_physics_ms_.load(std::memory_order_relaxed);
    double const next_phys = (cur_phys * 0.9) + (std::max(0.001, physics_time_ms) * 0.1);
    smoothed_physics_ms_.store(next_phys, std::memory_order_relaxed);

    if (is_master_bypassed_.load(std::memory_order_relaxed)) {
      dynamic_gpu_ratio_.store(0.0, std::memory_order_relaxed);
      is_render_pressure_active_.store(false, std::memory_order_relaxed);
      return;
    }

    if (is_manual_override_.load(std::memory_order_relaxed)) {
      dynamic_gpu_ratio_.store(manual_gpu_ratio_.load(std::memory_order_relaxed), std::memory_order_relaxed);
      is_render_pressure_active_.store(false, std::memory_order_relaxed);
      return;
    }

    // Scene-Aware Dynamic Allocation Matrix
    double target_ratio = 0.50; // Default balanced
    bool pressure_active = false;

    auto const scene = static_cast<KspSceneType>(scene_id);
    switch (scene) {
      case KspSceneType::FlightAtmospheric: {
        // High rendering load & complex aerodynamic drag/contact physics.
        // If render time exceeds 13.0 ms (~70 FPS redline), actively throttle GPU compute to 0%
        // to protect dGPU framerate and eliminate any rendering stutter!
        if (next_render > 13.0) {
          pressure_active = true;
          target_ratio = 0.0; // 100% CPU offload
        } else {
          pressure_active = false;
          target_ratio = 0.15; // Conservative background batching
        }
        break;
      }
      case KspSceneType::TrackingStation: {
        // Tracking Station has minimal 3D rasterization load (render < 4ms).
        // Maximize GPU compute to rapidly integrate hundreds of inactive vessels & ephemeris!
        target_ratio = 0.85;
        pressure_active = false;
        break;
      }
      case KspSceneType::SpaceCenter: {
        // Space Center KSC view: light rendering, allocate high budget for lookahead ephemeris.
        target_ratio = 0.70;
        pressure_active = false;
        break;
      }
      case KspSceneType::MapView: {
        // Map View: zero-copy trajectory render pipeline takes precedence.
        target_ratio = 0.60;
        pressure_active = false;
        break;
      }
      case KspSceneType::FlightOrbital:
      default: {
        // Normal orbital flight: smooth adaptive workload.
        if (next_render > 15.0) {
          pressure_active = true;
          target_ratio = 0.20;
        } else {
          pressure_active = false;
          target_ratio = 0.50;
        }
        break;
      }
    }

    is_render_pressure_active_.store(pressure_active, std::memory_order_relaxed);

    // Smoothly converge dynamic ratio towards target ratio (rate = 0.2)
    double const cur_ratio = dynamic_gpu_ratio_.load(std::memory_order_relaxed);
    double const next_ratio = (cur_ratio * 0.8) + (target_ratio * 0.2);
    dynamic_gpu_ratio_.store(std::clamp(next_ratio, 0.0, 1.0), std::memory_order_relaxed);
  }

  // Gets the current workload balance telemetry snapshot.
  WorkloadBalanceState GetState() const {
    WorkloadBalanceState state{};
    state.dynamic_gpu_ratio = dynamic_gpu_ratio_.load(std::memory_order_relaxed);
    state.smoothed_render_ms = smoothed_render_ms_.load(std::memory_order_relaxed);
    state.smoothed_physics_ms = smoothed_physics_ms_.load(std::memory_order_relaxed);
    state.current_scene = current_scene_.load(std::memory_order_relaxed);
    state.is_master_bypassed = is_master_bypassed_.load(std::memory_order_relaxed);
    state.is_render_pressure_active = is_render_pressure_active_.load(std::memory_order_relaxed);
    state.is_manual_override = is_manual_override_.load(std::memory_order_relaxed);
    return state;
  }

  // Master bypass toggle: when true, 100% bypasses heterogeneous GPU dispatches.
  void SetMasterBypass(bool const bypassed, int const module_mask) {
    is_master_bypassed_.store(bypassed, std::memory_order_relaxed);
    module_mask_.store(module_mask, std::memory_order_relaxed);
    if (bypassed) {
      dynamic_gpu_ratio_.store(0.0, std::memory_order_relaxed);
    }
  }

  // Manual override toggle allowing the user to lock a specific GPU workload ratio.
  void SetManualOverride(bool const enable_manual, double const manual_ratio) {
    is_manual_override_.store(enable_manual, std::memory_order_relaxed);
    manual_gpu_ratio_.store(std::clamp(manual_ratio, 0.0, 1.0), std::memory_order_relaxed);
    if (enable_manual) {
      dynamic_gpu_ratio_.store(manual_gpu_ratio_.load(std::memory_order_relaxed), std::memory_order_relaxed);
    }
  }

  // Partitions a batch of inactive vessels into CPU vs GPU subsets based on current balance ratio.
  void PartitionVesselBatch(
      int const total_vessels,
      int* const out_cpu_count,
      int* const out_gpu_count) {
    if (out_cpu_count == nullptr || out_gpu_count == nullptr) {
      return;
    }
    if (total_vessels <= 4 || is_master_bypassed_.load(std::memory_order_relaxed)) {
      *out_cpu_count = total_vessels;
      *out_gpu_count = 0;
      return;
    }

    double const ratio = dynamic_gpu_ratio_.load(std::memory_order_relaxed);
    int const gpu_count = std::clamp(
        static_cast<int>(std::round(total_vessels * ratio)),
        0,
        total_vessels);
    *out_gpu_count = gpu_count;
    *out_cpu_count = total_vessels - gpu_count;
  }

  // Returns true if a task should be dispatched to GPU given current dynamic balance.
  bool ShouldDispatchGpu(int const task_size) {
    if (is_master_bypassed_.load(std::memory_order_relaxed) || task_size < 16) {
      return false;
    }
    if (is_render_pressure_active_.load(std::memory_order_relaxed)) {
      return false;
    }
    return dynamic_gpu_ratio_.load(std::memory_order_relaxed) >= 0.25;
  }

 private:
  WorkloadBalancer() = default;

  std::atomic<bool> is_master_bypassed_{false};
  std::atomic<bool> is_manual_override_{false};
  std::atomic<double> manual_gpu_ratio_{0.50};
  std::atomic<double> dynamic_gpu_ratio_{0.50};
  std::atomic<double> smoothed_render_ms_{8.0};
  std::atomic<double> smoothed_physics_ms_{2.0};
  std::atomic<int32_t> current_scene_{static_cast<int32_t>(KspSceneType::FlightOrbital)};
  std::atomic<bool> is_render_pressure_active_{false};
  std::atomic<int32_t> module_mask_{~0};
};

}  // namespace internal

using internal::WorkloadBalancer;

}  // namespace _workload_balancer
}  // namespace ksp_plugin
}  // namespace principia
