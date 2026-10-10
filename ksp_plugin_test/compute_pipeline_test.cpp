#include "ksp_plugin/compute_pipeline.hpp"

#include <cmath>
#include <vector>

#include "gmock/gmock.h"
#include "gtest/gtest.h"
#include "ksp_plugin/gpu_interop_types.hpp"
#include "ksp_plugin/trajectory_renderer_interop.hpp"

namespace principia {
namespace ksp_plugin {
namespace _compute_pipeline {

using ::testing::AllOf;
using ::testing::DoubleNear;
using ::testing::Ge;
using ::testing::Le;
using _gpu_interop_types::CelestialBodyGpuState;
using _gpu_interop_types::ChebyshevSegmentData;
using _gpu_interop_types::DoubleSingle;
using _gpu_interop_types::DsVec3;
using _gpu_interop_types::OrbitalState64;
using _gpu_interop_types::ToDoubleSingle;
using _gpu_interop_types::FromDoubleSingle;
using _trajectory_renderer_interop::TrajectoryVertex;

class ComputePipelineTest : public ::testing::Test {
 protected:
  ComputePipelineTest() = default;
};

TEST_F(ComputePipelineTest, AdvanceBatchOrbitalConservation) {
  auto& pipeline = ComputePipeline::Instance();

  // Kerbin/Earth-like central celestial.
  CelestialBodyGpuState celestial{};
  celestial.position[0] = 0.0f;
  celestial.position[1] = 0.0f;
  celestial.position[2] = 0.0f;
  celestial.gravitational_parameter = 3.986004418e14f;  // m^3 / s^2

  pipeline.SetCelestialBodies(&celestial, 1);

  // Circular orbit at r = 7000 km.
  double const r0 = 7000000.0;
  double const v0 = std::sqrt(static_cast<double>(celestial.gravitational_parameter) / r0);

  constexpr int kParticleCount = 8;
  std::vector<OrbitalState64> states(kParticleCount);
  for (int i = 0; i < kParticleCount; ++i) {
    states[i].position.x = ToDoubleSingle(r0);
    states[i].position.y = ToDoubleSingle(0.0);
    states[i].position.z = ToDoubleSingle(0.0);

    states[i].velocity.x = ToDoubleSingle(0.0);
    states[i].velocity.y = ToDoubleSingle(v0);
    states[i].velocity.z = ToDoubleSingle(0.0);
  }

  // Advance for 100 steps of 10.0 seconds.
  pipeline.AdvanceBatch(states.data(), kParticleCount, /*time_step_seconds=*/10.0, /*sub_steps=*/100);

  for (int i = 0; i < kParticleCount; ++i) {
    double const px = FromDoubleSingle(states[i].position.x);
    double const py = FromDoubleSingle(states[i].position.y);
    double const pz = FromDoubleSingle(states[i].position.z);
    double const r_final = std::sqrt(px * px + py * py + pz * pz);

    // Symplectic Verlet should conserve orbit radius within < 1% over 1000s.
    EXPECT_THAT(r_final, DoubleNear(r0, r0 * 0.02));
  }
}

TEST_F(ComputePipelineTest, EvaluateChebyshevBatchCorrectness) {
  auto& pipeline = ComputePipeline::Instance();

  ChebyshevSegmentData seg{};
  seg.t_min = 0.0f;
  seg.t_max = 100.0f;
  seg.degree = 2;

  // P(tau) = c0*T0(tau) + c1*T1(tau) + c2*T2(tau)
  // T0(tau) = 1, T1(tau) = tau, T2(tau) = 2*tau^2 - 1
  seg.coeffs[0][0] = 10.0f; seg.coeffs[0][1] = 20.0f; seg.coeffs[0][2] = 30.0f;
  seg.coeffs[1][0] = 5.0f;  seg.coeffs[1][1] = -5.0f; seg.coeffs[1][2] = 2.0f;
  seg.coeffs[2][0] = 1.0f;  seg.coeffs[2][1] = 2.0f;  seg.coeffs[2][2] = 3.0f;

  std::vector<TrajectoryVertex> out_vertices(64);
  int const count = pipeline.EvaluateChebyshevBatch(&seg, 1, out_vertices.data(), 64);

  EXPECT_GT(count, 0);

  // Check the first point (tau = -1):
  // T0(-1) = 1, T1(-1) = -1, T2(-1) = 1
  // x = 10*1 + 5*(-1) + 1*(1) = 6
  EXPECT_NEAR(out_vertices[0].position[0], 6.0f, 1e-4f);
  // y = 20*1 + (-5)*(-1) + 2*(1) = 27
  EXPECT_NEAR(out_vertices[0].position[1], 27.0f, 1e-4f);
  // z = 30*1 + 2*(-1) + 3*(1) = 31
  EXPECT_NEAR(out_vertices[0].position[2], 31.0f, 1e-4f);

  // Check the last point (tau = 1):
  // T0(1) = 1, T1(1) = 1, T2(1) = 1
  // x = 10 + 5 + 1 = 16
  EXPECT_NEAR(out_vertices[count - 1].position[0], 16.0f, 1e-4f);
  // y = 20 - 5 + 2 = 17
  EXPECT_NEAR(out_vertices[count - 1].position[1], 17.0f, 1e-4f);
  // z = 30 + 2 + 3 = 35
  EXPECT_NEAR(out_vertices[count - 1].position[2], 35.0f, 1e-4f);
}

}  // namespace _compute_pipeline
}  // namespace ksp_plugin
}  // namespace principia
