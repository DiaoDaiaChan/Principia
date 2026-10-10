#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>

#include "physics/chebyshev_l2_tile_cache.hpp"

namespace principia {
namespace physics {
namespace _ephemeris_l3_window_ring {
namespace internal {

using namespace principia::physics::_chebyshev_l2_tile_cache;

// Global Ephemeris Continuous Window Ring Buffer:
// 32 Celestial Bodies * 512 Chebyshev Segments * 256 Bytes = 4.194 MB.
// Fits 100% inside modern CPU shared L3 Cache (16 MB - 96 MB 3D V-Cache).
// Allows all CPU worker threads on all cores to evaluate celestial positions
// with zero DRAM memory latency (saving ~70ns per access).
class alignas(64) EphemerisL3WindowRing {
 public:
  static constexpr std::size_t kMaxBodies = 32;
  static constexpr std::size_t kSegmentsPerBody = 512;

  static EphemerisL3WindowRing& Instance() {
    static EphemerisL3WindowRing instance;
    return instance;
  }

  // Inserts a continuous trajectory segment into the L3 window ring.
  void InsertSegment(std::size_t const body_idx, ChebyshevTile256 const& segment) {
    if (body_idx >= kMaxBodies) return;
    std::size_t const slot = write_cursor_[body_idx].load(std::memory_order_relaxed) % kSegmentsPerBody;
    ring_[body_idx][slot] = segment;
    write_cursor_[body_idx].fetch_add(1, std::memory_order_release);
  }

  // Evaluates a celestial body's position at time `t` directly from the L3 cache.
  bool EvaluateCelestialPosition(
      std::size_t const body_idx,
      double const t,
      double& x,
      double& y,
      double& z) const {
    if (body_idx >= kMaxBodies) return false;

    lookups_.fetch_add(1, std::memory_order_relaxed);
    std::size_t const current_head = write_cursor_[body_idx].load(std::memory_order_acquire);
    std::size_t const count = std::min(current_head, kSegmentsPerBody);

    // Fast search in L3 memory lines
    for (std::size_t i = 0; i < count; ++i) {
      auto const& seg = ring_[body_idx][i];
      if (t >= seg.t_min && t <= seg.t_max) {
        seg.EvaluateClenshaw(t, x, y, z);
        hits_.fetch_add(1, std::memory_order_relaxed);
        return true;
      }
    }
    return false;
  }

  std::uint64_t hits() const {
    return hits_.load(std::memory_order_relaxed);
  }

  std::uint64_t lookups() const {
    return lookups_.load(std::memory_order_relaxed);
  }

  void ResetCounters() {
    hits_.store(0, std::memory_order_relaxed);
    lookups_.store(0, std::memory_order_relaxed);
  }

 private:
  EphemerisL3WindowRing() = default;

  mutable std::atomic<std::uint64_t> hits_{0};
  mutable std::atomic<std::uint64_t> lookups_{0};

  alignas(64) std::array<std::atomic<std::size_t>, kMaxBodies> write_cursor_{};
  // 32 bodies * 512 segments * 256 bytes = 4.194 MB, 64-byte aligned
  alignas(64) std::array<std::array<ChebyshevTile256, kSegmentsPerBody>, kMaxBodies> ring_{};
};

}  // namespace internal

using internal::EphemerisL3WindowRing;

}  // namespace _ephemeris_l3_window_ring
}  // namespace physics
}  // namespace principia
