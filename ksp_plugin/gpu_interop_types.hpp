#pragma once

#include <cstdint>
#include <cstddef>

namespace principia {
namespace ksp_plugin {
namespace _gpu_interop_types {
namespace internal {

constexpr int kMaxChebyshevDegree = 16;
constexpr int kMaxCelestialsForGpu = 128;

// Matches ChebyshevSegmentData in TrajectoryTessellator.compute.
// Total size: 16 bytes header + 17 * 12 bytes coeffs + 36 bytes padding = 256 bytes (4 cache lines, 8 AVX2 vectors).
struct alignas(32) ChebyshevSegmentData {
  float t_min;
  float t_max;
  std::uint32_t degree;
  float padding;
  float coeffs[kMaxChebyshevDegree + 1][3];
  float reserved[9];
};
static_assert(sizeof(ChebyshevSegmentData) == 256, "ChebyshevSegmentData must be 256 bytes for 32-byte AVX2 alignment.");


// Hardware adapter telemetry and classification.
struct alignas(8) GpuAdapterInfo {
  int adapter_index;
  char description[128];
  std::uint64_t dedicated_video_memory;
  std::uint64_t shared_system_memory;
  std::uint32_t vendor_id;
  std::uint32_t device_id;
  bool is_integrated;
  bool supports_compute;
};

// DMA runtime mode.
enum class DmaTransferMode : std::int32_t {
  DirectDma = 0,       // ReBAR + AVX2 streaming non-temporal stores
  StagingFallback = 1, // Traditional staging host copy
  PureCpu = 2          // Disabled / CPU native
};

// Celestial body state matching FlightPlanShootingOptimizer.compute.
struct alignas(16) CelestialBodyGpuState {
  float position[3];
  float gravitational_parameter; // GM
  float velocity[3];
  float padding;
};

// Optimization context exported to GPU shooting optimizer.
struct alignas(16) ShootingContextGpu {
  float initial_pos[3];
  float initial_time;
  float initial_vel[3];
  float step_size_seconds;
  float base_delta_v[3];
  float span_delta_t;
  float span_delta_v[3];
  float target_periapsis_altitude;
  std::uint32_t target_celestial_index;
  std::uint32_t celestial_count;
  std::uint32_t step_count;
  std::uint32_t candidate_count;
  CelestialBodyGpuState celestials[kMaxCelestialsForGpu];
};

// Matches CandidateBurnEvaluation in FlightPlanShootingOptimizer.compute.
struct alignas(4) OptimalCandidateBurn {
  std::uint32_t candidate_index;
  float periapsis_distance;
  float target_relative_speed;
  float cost_metric;
  float delta_t;
  float delta_v_tangent;
  float delta_v_normal;
  float delta_v_binormal;
};

// Double-single floating point component (high float, low float) with 53-bit precision.
struct alignas(4) DoubleSingle {
  float hi;
  float lo;
};

// 3D vector in DoubleSingle precision.
struct alignas(4) DsVec3 {
  DoubleSingle x;
  DoubleSingle y;
  DoubleSingle z;
};

// Matches OrbitalState64 in BatchOrbitIntegrator.compute.
struct alignas(4) OrbitalState64 {
  DsVec3 position;
  DsVec3 velocity;
};

inline DoubleSingle ToDoubleSingle(double const v) {
  float const hi = static_cast<float>(v);
  float const lo = static_cast<float>(v - static_cast<double>(hi));
  return DoubleSingle{hi, lo};
}

inline double FromDoubleSingle(DoubleSingle const& ds) {
  return static_cast<double>(ds.hi) + static_cast<double>(ds.lo);
}

// KSP scene types for scene-aware workload adaptation.
enum class KspSceneType : std::int32_t {
  FlightAtmospheric = 0,   // Atmospheric launch/flight (high rendering load & part count)
  FlightOrbital     = 1,   // Orbital vacuum cruise (stable)
  TrackingStation   = 2,   // Tracking station (low rendering, high background physics)
  SpaceCenter       = 3,   // Space center KSC view
  MapView           = 4,   // Map view (trajectory rendering dominant)
  Other             = 5
};

// Full hardware calibration profile.
struct alignas(8) HardwareProfile {
  double cpu_fp64_gflops_score;      // CPU FP64 throughput score (k steps / s)
  double primary_gpu_compute_score;  // Primary GPU compute score
  double dma_bandwidth_gbps;         // PCIe / UMA measured DMA bandwidth (GB/s)
  std::int32_t adapter_count;        // Discovered GPU count
  std::int32_t recommended_compute_adapter; // Recommended adapter index (-1 for auto/default)
  bool has_integrated_gpu;           // System has iGPU (Intel UHD/Iris or AMD APU)
  bool has_discrete_gpu;             // System has dGPU (NVIDIA/AMD)
  bool is_calibrated;                // True if calibrated
  std::uint8_t padding[5];           // Pad to 8-byte alignment (40 bytes total)
};
static_assert(sizeof(HardwareProfile) == 40, "HardwareProfile must be 40 bytes.");

// Real-time workload balance state.
struct alignas(8) WorkloadBalanceState {
  double dynamic_gpu_ratio;          // Dynamically calculated GPU workload ratio [0.0, 1.0]
  double smoothed_render_ms;         // EWMA smoothed render time (ms)
  double smoothed_physics_ms;        // EWMA smoothed physics time (ms)
  std::int32_t current_scene;        // KspSceneType
  bool is_master_bypassed;           // 100% classic CPU native bypass
  bool is_render_pressure_active;    // GPU render frame protection throttled
  bool is_manual_override;           // User manually locked ratio
  std::uint8_t padding[1];           // Pad to 8-byte alignment (32 bytes total)
};
static_assert(sizeof(WorkloadBalanceState) == 32, "WorkloadBalanceState must be 32 bytes.");

}  // namespace internal

using internal::kMaxChebyshevDegree;
using internal::kMaxCelestialsForGpu;
using internal::ChebyshevSegmentData;
using internal::GpuAdapterInfo;
using internal::DmaTransferMode;
using internal::CelestialBodyGpuState;
using internal::ShootingContextGpu;
using internal::OptimalCandidateBurn;
using internal::DoubleSingle;
using internal::DsVec3;
using internal::OrbitalState64;
using internal::ToDoubleSingle;
using internal::FromDoubleSingle;
using internal::KspSceneType;
using internal::HardwareProfile;
using internal::WorkloadBalanceState;

}  // namespace _gpu_interop_types
}  // namespace ksp_plugin
}  // namespace principia
