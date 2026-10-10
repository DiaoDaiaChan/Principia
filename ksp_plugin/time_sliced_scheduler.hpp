#pragma once

#include <algorithm>
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
#include "physics/chebyshev_l2_tile_cache.hpp"
#include "physics/cpu_cache_topology.hpp"
#include "physics/ephemeris_l3_window_ring.hpp"

namespace principia {
namespace ksp_plugin {
namespace _time_sliced_scheduler {
namespace internal {

using namespace principia::geometry::_instant;
using namespace principia::physics::_celestial_l1_cache;
using namespace principia::physics::_chebyshev_l2_tile_cache;
using namespace principia::physics::_cpu_cache_topology;
using namespace principia::physics::_ephemeris_l3_window_ring;

enum class VesselPriorityTier : std::int32_t {
  Critical = 0,    // 50Hz Real-time (Active vessel, thrusting, or <= 2.5 km)
  Interactive = 1, // Auto-Adaptive 16.7Hz ~ 50Hz (Nearby 2.5 km - 50 km, flight plan)
  Deferred = 2     // Auto-Adaptive 2Hz ~ 25Hz (Distant > 50 km, debris, idle probes)
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
  double frame_budget_limit_ms{8.0};

  std::int32_t tier1_vessel_count{0};
  std::int32_t tier2_vessel_count{0};
  std::int32_t tier3_vessel_count{0};
  std::int32_t deferred_slices_yielded{0};

  std::int32_t l1_celestial_cache_hit_count{0};
  std::int32_t l2_chebyshev_tile_hit_count{0};

  // Auto-Adaptive Dynamic Cadence & L3 Cache Telemetry:
  double effective_tier2_hz{50.0};
  double effective_tier3_hz{10.0};
  std::int32_t tier2_stride{1};
  std::int32_t tier3_stride{5};
  double budget_headroom_ratio{0.5};
  std::int32_t l3_ephemeris_ring_hit_count{0};
  std::int32_t padding{0};  // Keeps 8-byte alignment clean

  // Hardware Topology & Safe Quota Telemetry:
  std::int32_t detected_l1d_kb{48};
  std::int32_t detected_l2_kb{2048};
  std::int32_t detected_l3_mb{36};
  std::int32_t allocated_l2_kb{512};
  std::int32_t allocated_l3_mb{5};

  // Multi-Level Eviction Spill & DRAM Cascade Telemetry:
  std::int32_t l2_to_l3_spill_count{0};
  std::int32_t l3_to_dram_spill_count{0};
  std::int32_t dram_hit_count{0};

  // Pillar 2: Work-Stealing & Core Affinity Telemetry:
  std::int32_t p_core_worker_count{0};
  std::int32_t e_core_worker_count{0};
  std::int64_t work_stolen_count{0};
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

// Dynamic Cadence Governor automatically adapting vessel stepping strides
// based on real-time EWMA physics budget headroom.
class alignas(64) DynamicCadenceGovernor {
 public:
  void UpdateFrame(double const frame_elapsed_ms, double const budget_limit_ms) {
    if (budget_limit_ms <= 0.0) return;

    // EWMA smoothing with alpha = 0.15 to avoid oscillating on single spike frames
    if (smoothed_physics_ms_ <= 0.001) {
      smoothed_physics_ms_ = frame_elapsed_ms;
    } else {
      smoothed_physics_ms_ = 0.85 * smoothed_physics_ms_ + 0.15 * frame_elapsed_ms;
    }

    double const headroom = (budget_limit_ms - smoothed_physics_ms_) / budget_limit_ms;
    headroom_ratio_ = headroom;

    // Tier 2: Interactive
    // When headroom is plentiful (>= 40%), step at full 50Hz (stride 1).
    // When moderate, step at 25Hz (stride 2).
    // When tight (< 10%), step at 16.7Hz (stride 3).
    if (headroom >= 0.40) {
      tier2_stride_ = 1;
    } else if (headroom >= 0.10) {
      tier2_stride_ = 2;
    } else {
      tier2_stride_ = 3;
    }

    // Tier 3: Deferred
    // Dynamically scales from 25Hz down to 2Hz based on available headroom:
    if (headroom >= 0.70) {
      tier3_stride_ = 2;  // 25.0 Hz: Plentiful headroom, no need to defer!
    } else if (headroom >= 0.45) {
      tier3_stride_ = 3;  // 16.7 Hz
    } else if (headroom >= 0.25) {
      tier3_stride_ = 5;  // 10.0 Hz
    } else if (headroom >= 0.10) {
      tier3_stride_ = 8;  // 6.25 Hz
    } else if (headroom >= 0.0) {
      tier3_stride_ = 12; // 4.17 Hz
    } else {
      tier3_stride_ = 25; // 2.00 Hz emergency throttle under high pressure
    }

    effective_tier2_hz_ = 50.0 / static_cast<double>(tier2_stride_);
    effective_tier3_hz_ = 50.0 / static_cast<double>(tier3_stride_);
  }

  std::int32_t tier2_stride() const { return tier2_stride_; }
  std::int32_t tier3_stride() const { return tier3_stride_; }
  double effective_tier2_hz() const { return effective_tier2_hz_; }
  double effective_tier3_hz() const { return effective_tier3_hz_; }
  double headroom_ratio() const { return headroom_ratio_; }
  double smoothed_physics_ms() const { return smoothed_physics_ms_; }

 private:
  double smoothed_physics_ms_{0.0};
  double headroom_ratio_{0.5};
  std::int32_t tier2_stride_{1};
  std::int32_t tier3_stride_{5};
  double effective_tier2_hz_{50.0};
  double effective_tier3_hz_{10.0};
};

// Time-sliced scheduler coordinating auto-adaptive cadence, frame budget, and cache hierarchies.
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

  // Determines whether a vessel should step in the current physics tick.
  // Uses Fibonacci golden ratio hash to ensure 100% uniform dispersal across frames.
  bool ShouldStepVesselThisTick(
      VesselPriorityTier const tier,
      std::uint64_t const vessel_uid,
      std::uint64_t const frame_index,
      FrameBudgetCircuitBreaker const& breaker) {
    if (tier == VesselPriorityTier::Critical) {
      return true;  // Critical vessels step 100% of ticks unconditionally
    }

    // Knuth's 64-bit Fibonacci golden ratio multiplicative hash (2^64 / phi)
    // Evenly spreads vessels across frames without harmonic clustering spikes.
    auto const hash = static_cast<std::uint64_t>(
        (vessel_uid * 11400714819323198485ULL) >> 32);

    if (tier == VesselPriorityTier::Interactive) {
      std::uint64_t const stride = static_cast<std::uint64_t>(
          std::max(1, governor_.tier2_stride()));
      if (stride > 1 && ((frame_index + hash) % stride != 0)) {
        return false;
      }
      if (breaker.HasExceededBudget()) {
        yielded_slices_.fetch_add(1, std::memory_order_relaxed);
        return false;
      }
      return true;
    }

    // Deferred tier
    std::uint64_t const stride = static_cast<std::uint64_t>(
        std::max(1, governor_.tier3_stride()));
    if (stride > 1 && ((frame_index + hash) % stride != 0)) {
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

    double const budget_ms = budget_us_.load(std::memory_order_relaxed) / 1000.0;
    governor_.UpdateFrame(elapsed_ms, budget_ms);
  }

  void RecordL1Hit() {
    l1_hits_.fetch_add(1, std::memory_order_relaxed);
  }

  void RecordL2TileHit() {
    l2_tile_hits_.fetch_add(1, std::memory_order_relaxed);
  }

  void RecordL3Hit() {
    l3_ring_hits_.fetch_add(1, std::memory_order_relaxed);
  }

  // Inserts a tile into the thread-local L2 cache.
  // If an older tile is evicted, automatically spills/dumps it into the shared L3 ring!
  void InsertL2Tile(ChebyshevTile256 const& tile) {
    ThreadLocalL2Cache().InsertTile(tile, [](ChebyshevTile256 const& evicted) {
      EphemerisL3WindowRing::Instance().InsertSegment(evicted.body_or_vessel_id, evicted);
    });
  }

  CpuModuleThreadingStatus GetStatusSnapshot(
      int const hardware_concurrency,
      int const pool_total_threads,
      int const pool_active_threads,
      int const pool_pending_tasks,
      int const ephemeris_pacer_active,
      int const prognosticator_count,
      int const orbit_analyser_active,
      int const optimizer_active,
      int const p_core_workers = 0,
      int const e_core_workers = 0,
      std::int64_t const work_stolen = 0) const {
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

    status.l1_celestial_cache_hit_count = static_cast<std::int32_t>(CelestialL1CacheBlock::Instance().hits) +
                                          l1_hits_.load(std::memory_order_relaxed);
    status.l2_chebyshev_tile_hit_count = l2_tile_hits_.load(std::memory_order_relaxed);

    // Auto-Adaptive Cadence Telemetry
    status.effective_tier2_hz = governor_.effective_tier2_hz();
    status.effective_tier3_hz = governor_.effective_tier3_hz();
    status.tier2_stride = governor_.tier2_stride();
    status.tier3_stride = governor_.tier3_stride();
    status.budget_headroom_ratio = governor_.headroom_ratio();
    status.l3_ephemeris_ring_hit_count = l3_ring_hits_.load(std::memory_order_relaxed);

    // Hardware Topology & Safe Quota Telemetry
    status.detected_l1d_kb = static_cast<std::int32_t>(topology_.l1d_size_bytes / 1024);
    status.detected_l2_kb = static_cast<std::int32_t>(topology_.l2_per_core_bytes / 1024);
    status.detected_l3_mb = static_cast<std::int32_t>(topology_.l3_shared_bytes / (1024 * 1024));
    status.allocated_l2_kb = static_cast<std::int32_t>(topology_.SafeL2Bytes() / 1024);
    status.allocated_l3_mb = static_cast<std::int32_t>(topology_.SafeL3Bytes() / (1024 * 1024));

    // Multi-Level Eviction Spill & DRAM Cascade Telemetry
    status.l2_to_l3_spill_count = static_cast<std::int32_t>(ThreadLocalL2Cache().spill_to_l3_count());
    status.l3_to_dram_spill_count = static_cast<std::int32_t>(EphemerisL3WindowRing::Instance().spill_to_dram_count());
    status.dram_hit_count = static_cast<std::int32_t>(EphemerisL3WindowRing::Instance().dram_hits());

    // Pillar 2: Work-Stealing & Core Affinity Telemetry
    status.p_core_worker_count = p_core_workers;
    status.e_core_worker_count = e_core_workers;
    status.work_stolen_count = work_stolen;

    return status;
  }

  CelestialL1CacheBlock& L1Cache() {
    return l1_cache_;
  }

  ChebyshevL2TileCache& ThreadLocalL2Cache() const {
    thread_local ChebyshevL2TileCache l2_cache(topology_.L2TileCapacity());
    return l2_cache;
  }

  EphemerisL3WindowRing& L3Ring() {
    return EphemerisL3WindowRing::Instance();
  }

  CpuCacheTopology const& topology() const {
    return topology_;
  }

 private:
  TimeSlicedScheduler() {
    topology_ = DetectCpuCacheTopology();
    // Auto-size L3 window ring safely according to host CPU L3 capacity:
    EphemerisL3WindowRing::Instance().SetSegmentsPerBody(topology_.L3SegmentsPerBody());
  }

  CpuCacheTopology topology_{};

  std::atomic<std::int64_t> budget_us_{8000};  // Default 8.0 ms (8000 us)
  std::atomic<double> last_frame_elapsed_ms_{0.0};
  std::atomic<std::int32_t> tier1_vessels_{0};
  std::atomic<std::int32_t> tier2_vessels_{0};
  std::atomic<std::int32_t> tier3_vessels_{0};
  std::atomic<std::int32_t> yielded_slices_{0};
  std::atomic<std::int32_t> l1_hits_{0};
  std::atomic<std::int32_t> l2_tile_hits_{0};
  std::atomic<std::int32_t> l3_ring_hits_{0};

  DynamicCadenceGovernor governor_{};
  CelestialL1CacheBlock l1_cache_{};
};

}  // namespace internal

using internal::VesselPriorityTier;
using internal::CpuModuleThreadingStatus;
using internal::FrameBudgetCircuitBreaker;
using internal::DynamicCadenceGovernor;
using internal::TimeSlicedScheduler;

}  // namespace _time_sliced_scheduler
}  // namespace ksp_plugin
}  // namespace principia
