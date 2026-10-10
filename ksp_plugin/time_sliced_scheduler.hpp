#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <vector>

#include "absl/base/thread_annotations.h"
#include "absl/synchronization/mutex.h"
#include "base/macros.hpp"
#include "geometry/instant.hpp"
#include "physics/celestial_l1_cache.hpp"

namespace principia {
namespace ksp_plugin {
namespace _time_sliced_scheduler {
namespace internal {

using namespace principia::geometry::_instant;
using namespace principia::physics::_celestial_l1_cache;

enum class VesselPriorityTier : std::int32_t {
  Critical = 0,    // 50Hz Real-time (Active vessel, thrusting, or <= 2.5 km)
  Interactive = 1, // 25Hz Interleaved (Nearby 2.5 km - 50 km)
  Deferred = 2     // 2Hz - 5Hz Sliced (Distant > 50 km, debris, idle probes)
};

// 64-byte cache-line aligned telemetry and control structure matching C# P/Invoke.
struct alignas(64) CpuModuleThreadingStatus {
  std::int32_t hardware_concurrency{0};
  std::int32_t vessel_pool_total_threads{0};
  std::int32_t vessel_pool_active_threads{0};
  std::int32_t vessel_pool_pending_tasks{0};

  std::int32_t ephemeris_pacer_active{0};
  std::int32_t prognosticator_active_count{0};
  std::int32_t orbit_analyser_active{0};
  std::int32_t flight_plan_optimizer_active{0};

  double current_frame_physics_time_ms{0.0};
  double frame_budget_limit_ms{3.0};

  std::int32_t tier1_vessel_count{0};
  std::int32_t tier2_vessel_count{0};
  std::int32_t tier3_vessel_count{0};
  std::int32_t deferred_slices_yielded{0};

  std::int32_t l1_celestial_cache_hit_count{0};
  std::int32_t l2_chebyshev_tile_hit_count{0};
};

// High-precision stopwatch guarding the physics frame budget.
class alignas(64) FrameBudgetCircuitBreaker {
 public:
  explicit FrameBudgetCircuitBreaker(std::chrono::microseconds const budget)
      : budget_(budget), start_time_(std::chrono::high_resolution_clock::now()) {}

  bool HasExceededBudget() const {
    auto const now = std::chrono::high_resolution_clock::now();
    return (now - start_time_) >= budget_;
  }

  double ElapsedMilliseconds() const {
    auto const now = std::chrono::high_resolution_clock::now();
    return std::chrono::duration<double, std::milli>(now - start_time_).count();
  }

  std::chrono::microseconds ElapsedMicroseconds() const {
    auto const now = std::chrono::high_resolution_clock::now();
    return std::chrono::duration_cast<std::chrono::microseconds>(now - start_time_);
  }

  std::chrono::microseconds budget() const {
    return budget_;
  }

 private:
  std::chrono::microseconds const budget_;
  std::chrono::high_resolution_clock::time_point const start_time_;
};

// Time-sliced scheduler coordinating priority tiers, frame budget, and cache hits.
class TimeSlicedScheduler {
 public:
  static TimeSlicedScheduler& Instance() {
    static TimeSlicedScheduler instance;
    return instance;
  }

  void SetFrameBudgetMilliseconds(double const ms) {
    budget_us_.store(static_cast<std::int64_t>(ms * 1000.0), std::memory_order_relaxed);
  }

  double GetFrameBudgetMilliseconds() const {
    return budget_us_.load(std::memory_order_relaxed) / 1000.0;
  }

  std::unique_ptr<FrameBudgetCircuitBreaker> CreateTickCircuitBreaker() const {
    return std::make_unique<FrameBudgetCircuitBreaker>(
        std::chrono::microseconds(budget_us_.load(std::memory_order_relaxed)));
  }

  bool ShouldStepVesselThisTick(
      VesselPriorityTier const tier,
      std::uint64_t const vessel_uid,
      std::uint64_t const frame_index,
      FrameBudgetCircuitBreaker const& breaker) {
    if (tier == VesselPriorityTier::Critical) {
      return true;  // Critical vessels step 100% of ticks unconditionally
    }

    if (tier == VesselPriorityTier::Interactive) {
      // 25Hz: Step on even frames (or odd frames based on UID to interleave)
      if ((frame_index + vessel_uid) % 2 != 0) {
        return false;
      }
      if (breaker.HasExceededBudget()) {
        yielded_slices_.fetch_add(1, std::memory_order_relaxed);
        return false;
      }
      return true;
    }

    // Deferred tier (2Hz ~ 5Hz): Step once every 10 frames, interleaved by UID
    if ((frame_index + vessel_uid) % 10 != 0) {
      return false;
    }
    if (breaker.HasExceededBudget()) {
      yielded_slices_.fetch_add(1, std::memory_order_relaxed);
      return false;
    }
    return true;
  }

  void RecordFrameStats(
      double const elapsed_ms,
      int const tier1_count,
      int const tier2_count,
      int const tier3_count) {
    last_frame_elapsed_ms_.store(elapsed_ms, std::memory_order_relaxed);
    tier1_vessels_.store(tier1_count, std::memory_order_relaxed);
    tier2_vessels_.store(tier2_count, std::memory_order_relaxed);
    tier3_vessels_.store(tier3_count, std::memory_order_relaxed);
  }

  void RecordL1Hit() {
    l1_hits_.fetch_add(1, std::memory_order_relaxed);
  }

  void RecordL2TileHit() {
    l2_tile_hits_.fetch_add(1, std::memory_order_relaxed);
  }

  CpuModuleThreadingStatus GetStatusSnapshot(
      int const hardware_concurrency,
      int const pool_total_threads,
      int const pool_active_threads,
      int const pool_pending_tasks,
      int const ephemeris_pacer_active,
      int const prognosticator_count,
      int const orbit_analyser_active,
      int const optimizer_active) const {
    CpuModuleThreadingStatus status{};
    status.hardware_concurrency = hardware_concurrency;
    status.vessel_pool_total_threads = pool_total_threads;
    status.vessel_pool_active_threads = pool_active_threads;
    status.vessel_pool_pending_tasks = pool_pending_tasks;

    status.ephemeris_pacer_active = ephemeris_pacer_active;
    status.prognosticator_active_count = prognosticator_count;
    status.orbit_analyser_active = orbit_analyser_active;
    status.flight_plan_optimizer_active = optimizer_active;

    status.current_frame_physics_time_ms = last_frame_elapsed_ms_.load(std::memory_order_relaxed);
    status.frame_budget_limit_ms = budget_us_.load(std::memory_order_relaxed) / 1000.0;

    status.tier1_vessel_count = tier1_vessels_.load(std::memory_order_relaxed);
    status.tier2_vessel_count = tier2_vessels_.load(std::memory_order_relaxed);
    status.tier3_vessel_count = tier3_vessels_.load(std::memory_order_relaxed);
    status.deferred_slices_yielded = yielded_slices_.load(std::memory_order_relaxed);

    status.l1_celestial_cache_hit_count = l1_hits_.load(std::memory_order_relaxed);
    status.l2_chebyshev_tile_hit_count = l2_tile_hits_.load(std::memory_order_relaxed);
    return status;
  }

  CelestialL1CacheBlock& L1Cache() {
    return l1_cache_;
  }

 private:
  TimeSlicedScheduler() = default;

  std::atomic<std::int64_t> budget_us_{3000};  // Default 3.0 ms (3000 us)
  std::atomic<double> last_frame_elapsed_ms_{0.0};
  std::atomic<std::int32_t> tier1_vessels_{0};
  std::atomic<std::int32_t> tier2_vessels_{0};
  std::atomic<std::int32_t> tier3_vessels_{0};
  std::atomic<std::int32_t> yielded_slices_{0};
  std::atomic<std::int32_t> l1_hits_{0};
  std::atomic<std::int32_t> l2_tile_hits_{0};

  CelestialL1CacheBlock l1_cache_{};
};

}  // namespace internal

using internal::VesselPriorityTier;
using internal::CpuModuleThreadingStatus;
using internal::FrameBudgetCircuitBreaker;
using internal::TimeSlicedScheduler;

}  // namespace _time_sliced_scheduler
}  // namespace ksp_plugin
}  // namespace principia
