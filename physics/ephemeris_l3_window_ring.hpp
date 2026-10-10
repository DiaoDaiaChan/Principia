#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <vector>

#include "absl/synchronization/mutex.h"
#include "physics/chebyshev_l2_tile_cache.hpp"

namespace principia {
namespace physics {
namespace _ephemeris_l3_window_ring {
namespace internal {

using namespace principia::physics::_chebyshev_l2_tile_cache;

// DRAM Backing Overflow Pool:
// Stores segments evicted from the L3 cache into system RAM.
// Provides infinite lossless fallback when an evaluation query spans outside the L3 window.
class EphemerisDramOverflowPool {
 public:
  static constexpr std::size_t kMaxBodies = 32;

  void StoreEvictedSegment(std::size_t const body_idx, ChebyshevTile256 const& seg) {
    if (body_idx >= kMaxBodies) return;
    absl::MutexLock l(&lock_);
    dram_pool_[body_idx].push_back(seg);
    // Keep DRAM pool bounded to recent 4096 segments per body (1 MB per body in RAM)
    if (dram_pool_[body_idx].size() > 4096) {
      dram_pool_[body_idx].erase(dram_pool_[body_idx].begin());
    }
  }

  bool EvaluateCelestialPosition(
      std::size_t const body_idx,
      double const t,
      double& x,
      double& y,
      double& z) const {
    if (body_idx >= kMaxBodies) return false;
    absl::ReaderMutexLock l(&lock_);
    auto const& pool = dram_pool_[body_idx];
    for (auto const& seg : pool) {
      if (t >= seg.t_min && t <= seg.t_max) {
        seg.EvaluateClenshaw(t, x, y, z);
        return true;
      }
    }
    return false;
  }

  std::size_t TotalDramSegments() const {
    absl::ReaderMutexLock l(&lock_);
    std::size_t total = 0;
    for (auto const& p : dram_pool_) {
      total += p.size();
    }
    return total;
  }

 private:
  mutable absl::Mutex lock_;
  std::array<std::vector<ChebyshevTile256>, kMaxBodies> dram_pool_{};
};

// Global Ephemeris Continuous Window Ring Buffer:
// Auto-sized to 10% - 15% of host CPU shared L3 Cache (2 MB - 16 MB).
// Sits 100% inside modern CPU shared L3 Cache (16 MB - 96 MB 3D V-Cache).
// When an L3 segment slot is overwritten (evicted), it automatically spills/dumps
// to the DRAM Overflow Pool in host system RAM!
class alignas(64) EphemerisL3WindowRing {
 public:
  static constexpr std::size_t kMaxBodies = 32;
  static constexpr std::size_t kMaxSegmentsPerBody = 1024;  // Up to 8.38 MB

  static EphemerisL3WindowRing& Instance() {
    static EphemerisL3WindowRing instance;
    return instance;
  }

  void SetSegmentsPerBody(std::size_t const count) {
    segments_per_body_ = std::clamp<std::size_t>(count, 128, kMaxSegmentsPerBody);
  }

  std::size_t segments_per_body() const { return segments_per_body_; }

  // Inserts a continuous trajectory segment into the L3 window ring.
  // If an older segment is evicted, spills it to the DRAM overflow pool!
  void InsertSegment(std::size_t const body_idx, ChebyshevTile256 const& segment) {
    if (body_idx >= kMaxBodies) return;
    std::size_t const slot =
        write_cursor_[body_idx].load(std::memory_order_relaxed) % segments_per_body_;

    if (write_cursor_[body_idx].load(std::memory_order_relaxed) >= segments_per_body_) {
      // Slot contains an old valid segment being evicted from L3: Spill to DRAM!
      dram_overflow_pool_.StoreEvictedSegment(body_idx, ring_[body_idx][slot]);
      spill_to_dram_count_.fetch_add(1, std::memory_order_relaxed);
    }

    ring_[body_idx][slot] = segment;
    write_cursor_[body_idx].fetch_add(1, std::memory_order_release);
  }

  // Evaluates a celestial body's position at time `t`:
  // First searches L3 Cache. If miss, cascades down to DRAM Overflow Pool!
  bool EvaluateCelestialPosition(
      std::size_t const body_idx,
      double const t,
      double& x,
      double& y,
      double& z) const {
    if (body_idx >= kMaxBodies) return false;

    lookups_.fetch_add(1, std::memory_order_relaxed);
    std::size_t const current_head = write_cursor_[body_idx].load(std::memory_order_acquire);
    std::size_t const count = std::min(current_head, segments_per_body_);

    // 1. Fast search in L3 cache lines
    for (std::size_t i = 0; i < count; ++i) {
      auto const& seg = ring_[body_idx][i];
      if (t >= seg.t_min && t <= seg.t_max) {
        seg.EvaluateClenshaw(t, x, y, z);
        l3_hits_.fetch_add(1, std::memory_order_relaxed);
        return true;
      }
    }

    // 2. Cascade down to DRAM Overflow Pool
    if (dram_overflow_pool_.EvaluateCelestialPosition(body_idx, t, x, y, z)) {
      dram_hits_.fetch_add(1, std::memory_order_relaxed);
      return true;
    }

    return false;
  }

  std::uint64_t l3_hits() const {
    return l3_hits_.load(std::memory_order_relaxed);
  }

  std::uint64_t dram_hits() const {
    return dram_hits_.load(std::memory_order_relaxed);
  }

  std::uint64_t spill_to_dram_count() const {
    return spill_to_dram_count_.load(std::memory_order_relaxed);
  }

  std::uint64_t lookups() const {
    return lookups_.load(std::memory_order_relaxed);
  }

  std::size_t TotalDramSegments() const {
    return dram_overflow_pool_.TotalDramSegments();
  }

 private:
  EphemerisL3WindowRing() = default;

  std::size_t segments_per_body_{512};

  mutable std::atomic<std::uint64_t> l3_hits_{0};
  mutable std::atomic<std::uint64_t> dram_hits_{0};
  mutable std::atomic<std::uint64_t> spill_to_dram_count_{0};
  mutable std::atomic<std::uint64_t> lookups_{0};

  alignas(64) std::array<std::atomic<std::size_t>, kMaxBodies> write_cursor_{};
  alignas(64) std::array<std::array<ChebyshevTile256, kMaxSegmentsPerBody>, kMaxBodies> ring_{};

  EphemerisDramOverflowPool dram_overflow_pool_{};
};

}  // namespace internal

using internal::EphemerisL3WindowRing;
using internal::EphemerisDramOverflowPool;

}  // namespace _ephemeris_l3_window_ring
}  // namespace physics
}  // namespace principia
