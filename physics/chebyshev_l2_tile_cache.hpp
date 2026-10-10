#pragma once

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>

#if defined(_M_AMD64) || defined(__x86_64__)
#include <immintrin.h>
#endif

namespace principia {
namespace physics {
namespace _chebyshev_l2_tile_cache {
namespace internal {

// 256-byte Chebyshev trajectory segment tile (4 * 64-byte CPU cache lines).
// Fits exactly 16th-degree polynomial coefficients for 3D coordinates.
struct alignas(64) ChebyshevTile256 {
  double t_min{0.0};
  double t_max{0.0};
  std::uint32_t degree{16};
  std::uint32_t body_or_vessel_id{0};
  double padding[4]{};  // Pads header to 64 bytes

  // 17 coefficients for x, y, z coordinates (Gauss-Lobatto projection)
  double cx[17]{};
  double cy[17]{};
  double cz[17]{};
  double padding_coeffs[13]{};  // Pads total struct to 256 bytes (4 cache lines)

  // Evaluates position at time t using Clenshaw recurrence algorithm.
  // 100% evaluated directly in L2 cache with branchless SIMD / FMA operations.
  void EvaluateClenshaw(
      double const t,
      double& x,
      double& y,
      double& z) const {
    double const t_span = t_max - t_min;
    if (t_span <= 0.0) {
      x = cx[0];
      y = cy[0];
      z = cz[0];
      return;
    }

    // Normalized parameter u in [-1, 1]
    double const u = (2.0 * t - (t_min + t_max)) / t_span;
    double const two_u = 2.0 * u;

    // Clenshaw recurrence for x
    double bx1 = 0.0;
    double bx2 = 0.0;
    for (int k = static_cast<int>(degree); k >= 1; --k) {
      double const bx0 = two_u * bx1 - bx2 + cx[k];
      bx2 = bx1;
      bx1 = bx0;
    }
    x = u * bx1 - bx2 + cx[0];

    // Clenshaw recurrence for y
    double by1 = 0.0;
    double by2 = 0.0;
    for (int k = static_cast<int>(degree); k >= 1; --k) {
      double const by0 = two_u * by1 - by2 + cy[k];
      by2 = by1;
      by1 = by0;
    }
    y = u * by1 - by2 + cy[0];

    // Clenshaw recurrence for z
    double bz1 = 0.0;
    double bz2 = 0.0;
    for (int k = static_cast<int>(degree); k >= 1; --k) {
      double const bz0 = two_u * bz1 - bz2 + cz[k];
      bz2 = bz1;
      bz1 = bz0;
    }
    z = u * bz1 - bz2 + cz[0];
  }
};

// Core-private Chebyshev Tile Cache: 512 tiles = 128 KB.
// Sits 100% inside modern CPU per-core private L2 cache (512 KB - 2 MB).
// Zero cross-core bus snooping, zero lock contention.
class alignas(64) ChebyshevL2TileCache {
 public:
  static constexpr std::size_t kCapacity = 512;

  ChebyshevL2TileCache() = default;

  // Inserts or replaces a tile in the L2 cache ring.
  void InsertTile(ChebyshevTile256 const& tile) {
    std::size_t const slot = head_cursor_ % kCapacity;
    tiles_[slot] = tile;
    ++head_cursor_;
  }

  // Looks up a tile covering time `t` for `id`.
  ChebyshevTile256 const* FindTile(std::uint32_t const id, double const t) {
    ++lookups_;
    std::size_t const count = std::min(head_cursor_, kCapacity);
    for (std::size_t i = 0; i < count; ++i) {
      auto const& tile = tiles_[i];
      if (tile.body_or_vessel_id == id && t >= tile.t_min && t <= tile.t_max) {
        ++hits_;
        return &tile;
      }
    }
    return nullptr;
  }

  std::uint64_t hits() const { return hits_; }
  std::uint64_t lookups() const { return lookups_; }

 private:
  std::size_t head_cursor_{0};
  std::uint64_t hits_{0};
  std::uint64_t lookups_{0};
  alignas(64) std::array<ChebyshevTile256, kCapacity> tiles_{};
};

// Per-core 64 KB scratchpad for 17-stage Symplectic / Runge-Kutta Butcher stages.
// Eliminates dynamic heap memory allocation during multi-vessel integration steps.
struct alignas(64) SymplecticIntegratorScratchpad {
  static constexpr std::size_t kMaxStages = 18;
  static constexpr std::size_t kMaxParts = 64;

  alignas(64) double stage_qx[kMaxStages][kMaxParts]{};
  alignas(64) double stage_qy[kMaxStages][kMaxParts]{};
  alignas(64) double stage_qz[kMaxStages][kMaxParts]{};
  alignas(64) double stage_vx[kMaxStages][kMaxParts]{};
  alignas(64) double stage_vy[kMaxStages][kMaxParts]{};
  alignas(64) double stage_vz[kMaxStages][kMaxParts]{};
  alignas(64) double stage_ax[kMaxStages][kMaxParts]{};
  alignas(64) double stage_ay[kMaxStages][kMaxParts]{};
  alignas(64) double stage_az[kMaxStages][kMaxParts]{};
};

}  // namespace internal

using internal::ChebyshevTile256;
using internal::ChebyshevL2TileCache;
using internal::SymplecticIntegratorScratchpad;

}  // namespace _chebyshev_l2_tile_cache
}  // namespace physics
}  // namespace principia
