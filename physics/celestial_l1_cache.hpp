#pragma once

#include <array>
#include <cstdint>
#include <cmath>

#if defined(_M_AMD64) || defined(__x86_64__)
#include <immintrin.h>
#endif

namespace principia {
namespace physics {
namespace _celestial_l1_cache {
namespace internal {

// Compact Structure-of-Arrays (SoA) layout for up to 32 celestial bodies.
// Total footprint: 7 * 32 * 8 bytes + 64 bytes header = 1856 bytes (< 1.85 KB).
// Fits 100% inside modern CPU L1d cache (32 KB - 48 KB) across all Runge-Kutta substeps!
// Aligned to 64 bytes to eliminate false sharing and allow optimal SIMD broadcast.
struct alignas(64) CelestialL1CacheBlock {
  static constexpr int kMaxBodies = 32;

  static CelestialL1CacheBlock& Instance() {
    static CelestialL1CacheBlock instance;
    return instance;
  }

  int count{0};
  double t_sec{0.0};
  std::uint32_t hits{0};
  std::uint32_t evaluations{0};
  std::uint32_t padding[11]{};  // Pads header to 64 bytes

  alignas(64) double x[kMaxBodies]{};
  alignas(64) double y[kMaxBodies]{};
  alignas(64) double z[kMaxBodies]{};
  alignas(64) double vx[kMaxBodies]{};
  alignas(64) double vy[kMaxBodies]{};
  alignas(64) double vz[kMaxBodies]{};
  alignas(64) double mu[kMaxBodies]{};  // Gravitational parameter GM
  alignas(64) double r_min_sqr[kMaxBodies]{};  // Collision radius squared

  // Computes gravitational acceleration on a point (px, py, pz) from cached bodies.
  // dt = (t - t_sec): linearly extrapolates celestial position using current velocity.
  // start_body: allows skipping oblate bodies (e.g. index 0) which require J2 zonal harmonics.
  // Returns true if collision is detected with any evaluated celestial body.
  bool ComputeGravitationalAcceleration(
      double const px,
      double const py,
      double const pz,
      double const dt,
      double& ax,
      double& ay,
      double& az,
      int const start_body = 0) const {
    double total_ax = 0.0;
    double total_ay = 0.0;
    double total_az = 0.0;
    bool collision = false;

    int const n = count;
    for (int i = start_body; i < n; ++i) {
      double const cx = x[i] + vx[i] * dt;
      double const cy = y[i] + vy[i] * dt;
      double const cz = z[i] + vz[i] * dt;
      double const dx = cx - px;
      double const dy = cy - py;
      double const dz = cz - pz;
      double const r2 = dx * dx + dy * dy + dz * dz;
      if (r_min_sqr[i] > 0.0 && r2 <= r_min_sqr[i]) {
        collision = true;
      }
      if (r2 > 1.0) {  // Avoid division by zero inside center of celestial
        double const inv_r = 1.0 / std::sqrt(r2);
        double const inv_r3 = inv_r * inv_r * inv_r;
        double const g = mu[i] * inv_r3;
        total_ax += g * dx;
        total_ay += g * dy;
        total_az += g * dz;
      }
    }

    ax = total_ax;
    ay = total_ay;
    az = total_az;
    return collision;
  }

  bool ComputeGravitationalAcceleration(
      double const px,
      double const py,
      double const pz,
      double& ax,
      double& ay,
      double& az) const {
    return ComputeGravitationalAcceleration(px, py, pz, 0.0, ax, ay, az, 0);
  }
};

}  // namespace internal

using internal::CelestialL1CacheBlock;

}  // namespace _celestial_l1_cache
}  // namespace physics
}  // namespace principia
