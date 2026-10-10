#pragma once

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
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

    auto const profile = HardwareProfiler::Instance().GetProfile();

    if (is_master_bypassed_.load(std::memory_order_relaxed)) {
      dynamic_cpu_ratio_.store(1.0, std::memory_order_relaxed);
      dynamic_igpu_ratio_.store(0.0, std::memory_order_relaxed);
      dynamic_dgpu_ratio_.store(0.0, std::memory_order_relaxed);
      dynamic_aux_ratio_.store(0.0, std::memory_order_relaxed);
      dynamic_gpu_ratio_.store(0.0, std::memory_order_relaxed);
      is_render_pressure_active_.store(false, std::memory_order_relaxed);
      return;
    }

    if (is_manual_override_.load(std::memory_order_relaxed)) {
      double const manual_r = manual_gpu_ratio_.load(std::memory_order_relaxed);
      double manual_igpu = 0.0;
      double manual_dgpu = 0.0;
      double manual_aux = 0.0;

      if (profile.is_heterogeneous_tri_engine) {
        manual_igpu = manual_r * 0.40;
        manual_dgpu = manual_r * 0.60;
      } else if (profile.has_integrated_gpu) {
        manual_igpu = manual_r;
      } else {
        manual_dgpu = manual_r;
      }

      dynamic_cpu_ratio_.store(1.0 - manual_r, std::memory_order_relaxed);
      dynamic_igpu_ratio_.store(manual_igpu, std::memory_order_relaxed);
      dynamic_dgpu_ratio_.store(manual_dgpu, std::memory_order_relaxed);
      dynamic_aux_ratio_.store(manual_aux, std::memory_order_relaxed);
      dynamic_gpu_ratio_.store(manual_r, std::memory_order_relaxed);
      is_render_pressure_active_.store(false, std::memory_order_relaxed);
      return;
    }

    // Dynamic Multi-Engine Allocation Matrix
    double target_cpu = 0.20;
    double target_igpu = 0.0;
    double target_dgpu = 0.0;
    double target_aux = 0.0;
    bool pressure_active = false;

    auto const scene = static_cast<KspSceneType>(scene_id);

    if (profile.is_heterogeneous_tri_engine) {
      // Tri-Engine Mode: CPU + iGPU (UMA zero-copy) + dGPU (Rendering Master)
      if (scene == KspSceneType::FlightAtmospheric) {
        // High rendering load & complex aerodynamic drag/contact physics.
        // If render time exceeds 13.0 ms (~70 FPS redline), actively throttle dGPU compute to 0%
        // to protect rasterization framerate, while smoothly rerouting physics workloads
        // to iGPU + CPU, preventing frame stutter!
        if (next_render > 13.0) {
          pressure_active = true;
          target_dgpu = 0.0;
          target_igpu = 0.60;
          target_cpu = 0.40;
        } else {
          pressure_active = false;
          target_dgpu = 0.25;
          target_igpu = 0.45;
          target_cpu = 0.30;
        }
      } else if (scene == KspSceneType::TrackingStation) {
        // Tracking Station has minimal 3D rasterization load (render < 4ms).
        // Maximize dGPU and iGPU compute to rapidly integrate hundreds of vessels!
        target_dgpu = 0.55;
        target_igpu = 0.35;
        target_cpu = 0.10;
        pressure_active = false;
      } else if (scene == KspSceneType::SpaceCenter) {
        target_dgpu = 0.45;
        target_igpu = 0.40;
        target_cpu = 0.15;
        pressure_active = false;
      } else if (scene == KspSceneType::MapView) {
        // Map View: zero-copy trajectory render pipeline takes precedence.
        target_dgpu = 0.40;
        target_igpu = 0.40;
        target_cpu = 0.20;
        pressure_active = false;
      } else {
        // FlightOrbital / default
        if (next_render > 14.0) {
          pressure_active = true;
          target_dgpu = 0.10;
          target_igpu = 0.55;
          target_cpu = 0.35;
        } else {
          pressure_active = false;
          target_dgpu = 0.45;
          target_igpu = 0.35;
          target_cpu = 0.20;
        }
      }
    } else if (profile.active_gpu_count >= 2 && profile.has_discrete_gpu && !profile.has_integrated_gpu) {
      // Multi-dGPU: Primary dGPU handles display, Secondary dGPU is DedicatedCompute
      if (next_render > 13.0) {
        pressure_active = true;
        target_dgpu = 0.0; // Primary throttled
        target_aux = 0.70;  // Secondary dedicated card takes compute
        target_cpu = 0.30;
      } else {
        pressure_active = false;
        target_dgpu = 0.40;
        target_aux = 0.45;
        target_cpu = 0.15;
      }
    } else if (profile.has_integrated_gpu && !profile.has_discrete_gpu) {
      // iGPU only
      if (next_render > 16.0) {
        pressure_active = true;
        target_igpu = 0.30;
        target_cpu = 0.70;
      } else {
        pressure_active = false;
        target_igpu = 0.60;
        target_cpu = 0.40;
      }
    } else if (profile.has_discrete_gpu && !profile.has_integrated_gpu) {
      // Single dGPU only
      if (next_render > 13.0) {
        pressure_active = true;
        target_dgpu = 0.0;
        target_cpu = 1.0;
      } else {
        pressure_active = false;
        target_dgpu = (scene == KspSceneType::TrackingStation) ? 0.85 : 0.50;
        target_cpu = 1.0 - target_dgpu;
      }
    } else {
      // Pure CPU fallback
      target_cpu = 1.0;
      pressure_active = false;
    }

    is_render_pressure_active_.store(pressure_active, std::memory_order_relaxed);

    // Smoothly converge multi-channel dynamic ratios towards target ratios (rate = 0.2)
    double const cur_cpu = dynamic_cpu_ratio_.load(std::memory_order_relaxed);
    double const cur_igpu = dynamic_igpu_ratio_.load(std::memory_order_relaxed);
    double const cur_dgpu = dynamic_dgpu_ratio_.load(std::memory_order_relaxed);
    double const cur_aux = dynamic_aux_ratio_.load(std::memory_order_relaxed);

    double next_cpu = (cur_cpu * 0.8) + (target_cpu * 0.2);
    double next_igpu = (cur_igpu * 0.8) + (target_igpu * 0.2);
    double next_dgpu = (cur_dgpu * 0.8) + (target_dgpu * 0.2);
    double next_aux = (cur_aux * 0.8) + (target_aux * 0.2);

    double const sum = next_cpu + next_igpu + next_dgpu + next_aux;
    if (sum > 1e-6) {
      next_cpu /= sum;
      next_igpu /= sum;
      next_dgpu /= sum;
      next_aux /= sum;
    } else {
      next_cpu = 1.0;
      next_igpu = 0.0;
      next_dgpu = 0.0;
      next_aux = 0.0;
    }

    dynamic_cpu_ratio_.store(std::clamp(next_cpu, 0.0, 1.0), std::memory_order_relaxed);
    dynamic_igpu_ratio_.store(std::clamp(next_igpu, 0.0, 1.0), std::memory_order_relaxed);
    dynamic_dgpu_ratio_.store(std::clamp(next_dgpu, 0.0, 1.0), std::memory_order_relaxed);
    dynamic_aux_ratio_.store(std::clamp(next_aux, 0.0, 1.0), std::memory_order_relaxed);
    dynamic_gpu_ratio_.store(std::clamp(next_igpu + next_dgpu + next_aux, 0.0, 1.0), std::memory_order_relaxed);

    // Update individual device ratio telemetry
    for (int i = 0; i < profile.adapter_count; ++i) {
      _gpu_interop_types::GpuDeviceInfo dev{};
      if (HardwareProfiler::Instance().GetDeviceInfo(i, &dev)) {
        if (dev.is_integrated) {
          HardwareProfiler::Instance().SetDeviceDynamicRatio(i, next_igpu);
        } else if (dev.role == _gpu_interop_types::GpuDeviceRole::PrimaryRender) {
          HardwareProfiler::Instance().SetDeviceDynamicRatio(i, next_dgpu);
        } else {
          HardwareProfiler::Instance().SetDeviceDynamicRatio(i, next_aux);
        }
      }
    }
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
    state.dynamic_cpu_ratio = dynamic_cpu_ratio_.load(std::memory_order_relaxed);
    state.dynamic_igpu_ratio = dynamic_igpu_ratio_.load(std::memory_order_relaxed);
    state.dynamic_dgpu_ratio = dynamic_dgpu_ratio_.load(std::memory_order_relaxed);
    state.dynamic_aux_ratio = dynamic_aux_ratio_.load(std::memory_order_relaxed);
    state.active_device_count = HardwareProfiler::Instance().GetProfile().active_gpu_count;
    return state;
  }

  // Master bypass toggle: when true, 100% bypasses heterogeneous GPU dispatches.
  void SetMasterBypass(bool const bypassed, int const module_mask) {
    is_master_bypassed_.store(bypassed, std::memory_order_relaxed);
    module_mask_.store(module_mask, std::memory_order_relaxed);
    if (bypassed) {
      dynamic_cpu_ratio_.store(1.0, std::memory_order_relaxed);
      dynamic_igpu_ratio_.store(0.0, std::memory_order_relaxed);
      dynamic_dgpu_ratio_.store(0.0, std::memory_order_relaxed);
      dynamic_aux_ratio_.store(0.0, std::memory_order_relaxed);
      dynamic_gpu_ratio_.store(0.0, std::memory_order_relaxed);
    }
  }

  // Manual override toggle allowing the user to lock a specific GPU workload ratio.
  void SetManualOverride(bool const enable_manual, double const manual_ratio) {
    is_manual_override_.store(enable_manual, std::memory_order_relaxed);
    manual_gpu_ratio_.store(std::clamp(manual_ratio, 0.0, 1.0), std::memory_order_relaxed);
    if (enable_manual) {
      UpdateSceneFeedback(current_scene_.load(std::memory_order_relaxed),
                          smoothed_render_ms_.load(std::memory_order_relaxed),
                          smoothed_physics_ms_.load(std::memory_order_relaxed));
    }
  }

  // Partitions work across all heterogeneous compute devices: [CPU, iGPU, dGPU, Aux]
  void PartitionMultiDeviceBatch(
      int const total_work,
      int* const out_device_counts,
      int const max_devices) {
    if (out_device_counts == nullptr || max_devices <= 0) {
      return;
    }
    std::memset(out_device_counts, 0, max_devices * sizeof(int));
    if (total_work <= 0) {
      return;
    }

    if (total_work <= 4 || is_master_bypassed_.load(std::memory_order_relaxed)) {
      out_device_counts[0] = total_work; // 100% CPU
      return;
    }

    double const cpu_r = dynamic_cpu_ratio_.load(std::memory_order_relaxed);
    double const igpu_r = dynamic_igpu_ratio_.load(std::memory_order_relaxed);
    double const dgpu_r = dynamic_dgpu_ratio_.load(std::memory_order_relaxed);
    double const aux_r = dynamic_aux_ratio_.load(std::memory_order_relaxed);

    int c_cpu = static_cast<int>(std::round(total_work * cpu_r));
    int c_igpu = static_cast<int>(std::round(total_work * igpu_r));
    int c_dgpu = static_cast<int>(std::round(total_work * dgpu_r));
    int c_aux = static_cast<int>(std::round(total_work * aux_r));

    // Absorb rounding discrepancy into CPU count
    int const allocated = c_cpu + c_igpu + c_dgpu + c_aux;
    int const diff = total_work - allocated;
    c_cpu += diff;
    if (c_cpu < 0) {
      c_igpu += c_cpu;
      c_cpu = 0;
    }

    out_device_counts[0] = c_cpu;
    if (max_devices > 1) out_device_counts[1] = c_igpu;
    if (max_devices > 2) out_device_counts[2] = c_dgpu;
    if (max_devices > 3) out_device_counts[3] = c_aux;
  }

  // Partitions a batch of inactive vessels into CPU vs GPU subsets based on current balance ratio.
  void PartitionVesselBatch(
      int const total_vessels,
      int* const out_cpu_count,
      int* const out_gpu_count) {
    if (out_cpu_count == nullptr || out_gpu_count == nullptr) {
      return;
    }
    int counts[4] = {0, 0, 0, 0};
    PartitionMultiDeviceBatch(total_vessels, counts, 4);
    *out_cpu_count = counts[0];
    *out_gpu_count = counts[1] + counts[2] + counts[3];
  }

  // Returns true if a task should be dispatched to GPU given current dynamic balance.
  bool ShouldDispatchGpu(int const task_size) {
    if (is_master_bypassed_.load(std::memory_order_relaxed) || task_size <= 4) {
      return false;
    }
    if (is_render_pressure_active_.load(std::memory_order_relaxed)) {
      // If render pressure is active, still permit dispatch if iGPU is present and active!
      return dynamic_igpu_ratio_.load(std::memory_order_relaxed) >= 0.25;
    }
    return dynamic_gpu_ratio_.load(std::memory_order_relaxed) >= 0.25;
  }

 private:
  WorkloadBalancer() = default;

  std::atomic<bool> is_master_bypassed_{false};
  std::atomic<bool> is_manual_override_{false};
  std::atomic<double> manual_gpu_ratio_{0.50};
  std::atomic<double> dynamic_cpu_ratio_{0.20};
  std::atomic<double> dynamic_igpu_ratio_{0.35};
  std::atomic<double> dynamic_dgpu_ratio_{0.45};
  std::atomic<double> dynamic_aux_ratio_{0.0};
  std::atomic<double> dynamic_gpu_ratio_{0.80};
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
