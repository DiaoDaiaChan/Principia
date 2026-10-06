#pragma once

#include <cstdint>
#include <algorithm>

namespace principia {
namespace ksp_plugin {
namespace _dense_polynomial_slice {

// 5th-order Hermite polynomial trajectory & attitude slice.
// Represents continuous kinematic state over [t_start, t_end].
struct alignas(8) DensePolynomialSlice {
  double t_start;
  double t_end;

  // 5th-order polynomial coefficients for position (q(t) = sum_{k=0}^5 c_k * tau^k, tau in [0, 1]).
  double c0[3];
  double c1[3];
  double c2[3];
  double c3[3];
  double c4[3];
  double c5[3];

  // Quaternion components at t_start and t_end (x, y, z, w).
  double q_start[4];
  double q_end[4];

  // Evaluates position at time t using Horner's scheme (5 FMAs per dimension).
  inline void EvaluatePosition(double const t, double position[3]) const {
    double const duration = t_end - t_start;
    double const tau = (duration <= 0.0) ? 0.0 : (t - t_start) / duration;
    for (int i = 0; i < 3; ++i) {
      position[i] = c0[i] + tau * (c1[i] + tau * (c2[i] + tau * (c3[i] + tau * (c4[i] + tau * c5[i]))));
    }
  }

  // Evaluates velocity at time t by taking analytical derivative:
  // p(t) = d/dt q(t) = (1 / duration) * sum_{k=1}^5 k * c_k * tau^{k-1}
  inline void EvaluateVelocity(double const t, double velocity[3]) const {
    double const duration = t_end - t_start;
    if (duration <= 0.0) {
      velocity[0] = 0.0;
      velocity[1] = 0.0;
      velocity[2] = 0.0;
      return;
    }
    double const tau = (t - t_start) / duration;
    double const inv_duration = 1.0 / duration;
    for (int i = 0; i < 3; ++i) {
      velocity[i] = inv_duration * (c1[i] + tau * (2.0 * c2[i] + tau * (3.0 * c3[i] + tau * (4.0 * c4[i] + tau * (5.0 * c5[i])))));
    }
  }
};

}  // namespace _dense_polynomial_slice

using _dense_polynomial_slice::DensePolynomialSlice;

}  // namespace ksp_plugin
}  // namespace principia
