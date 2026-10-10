#pragma once

#include <cstdint>
#include <memory>
#include <vector>

#include "base/macros.hpp"
#include "ksp_plugin/gpu_interop_types.hpp"
#include "ksp_plugin/trajectory_renderer_interop.hpp"

#if OS_WIN
#include <windows.h>
#include <d3d11.h>
#undef interface
#endif

namespace principia {
namespace ksp_plugin {
namespace _compute_pipeline {
namespace internal {

using _gpu_interop_types::CelestialBodyGpuState;
using _gpu_interop_types::ChebyshevSegmentData;
using _gpu_interop_types::OrbitalState64;
using _trajectory_renderer_interop::TrajectoryVertex;

class ComputePipeline final {
 public:
  static ComputePipeline& Instance();

  // Initialize with host Direct3D 11 device and context (if available).
  void InitializeD3D11(void* d3d11_device, void* d3d11_context);

  // Updates celestial bodies snapshot for the gravitational field.
  void SetCelestialBodies(
      CelestialBodyGpuState const* celestials,
      int celestial_count);

  // Universal Primitive A: High-throughput symplectic numerical advance.
  // Advances an arbitrary batch of test particles (flight plan candidate burns,
  // inactive vessels, coasting predictions) by `sub_steps` of `time_step_seconds`.
  // Automatically dispatched between GPU and CPU via WorkloadBalancer.
  void AdvanceBatch(
      OrbitalState64* states,
      int count,
      double time_step_seconds,
      int sub_steps);

  // Universal Primitive B: High-throughput Chebyshev continuous curve evaluation.
  // Expands Chebyshev polynomial segments into continuous trajectory vertices
  // directly in memory or GPU vertex buffers.
  int EvaluateChebyshevBatch(
      ChebyshevSegmentData const* segments,
      int segment_count,
      TrajectoryVertex* out_vertices,
      int max_vertices);

  // Fallback implementations accessible for benchmark verification.
  void AdvanceBatchCpu(
      OrbitalState64* states,
      int count,
      double time_step_seconds,
      int sub_steps);

  int EvaluateChebyshevBatchCpu(
      ChebyshevSegmentData const* segments,
      int segment_count,
      TrajectoryVertex* out_vertices,
      int max_vertices);

 private:
  ComputePipeline();
  ~ComputePipeline();

  void InitializeShadersIfPossible();

  std::vector<CelestialBodyGpuState> cached_celestials_;

#if OS_WIN
  ID3D11Device* d3d11_device_ = nullptr;
  ID3D11DeviceContext* d3d11_context_ = nullptr;
  ID3D11ComputeShader* batch_orbit_shader_ = nullptr;
  ID3D11ComputeShader* trajectory_tessellator_shader_ = nullptr;
#endif
};

}  // namespace internal

using internal::ComputePipeline;

}  // namespace _compute_pipeline
}  // namespace ksp_plugin
}  // namespace principia
