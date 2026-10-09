#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <vector>
#if OS_WIN
#include <malloc.h>
#endif

#include "absl/log/check.h"
#include "absl/log/log.h"
#include "geometry/instant.hpp"
#include "geometry/space.hpp"
#include "ksp_plugin/chebyshev_segment_export.hpp"
#include "ksp_plugin/dxgi_adapter_factory.hpp"
#include "ksp_plugin/flight_plan.hpp"
#include "ksp_plugin/frames.hpp"
#include "ksp_plugin/gpu_interop_types.hpp"
#include "ksp_plugin/interface.hpp"
#include "ksp_plugin/planetarium.hpp"
#include "ksp_plugin/plugin.hpp"
#include "ksp_plugin/vessel.hpp"
#include "numerics/elementary_functions.hpp"
#include "physics/degrees_of_freedom.hpp"
#include "physics/discrete_trajectory.hpp"
#include "physics/discrete_trajectory_view.hpp"
#include "physics/ephemeris.hpp"
#include "quantities/quantities.hpp"
#include "quantities/si.hpp"

#if defined(_MSC_VER)
#define PRINCIPIA_EXPORT extern "C" __declspec(dllexport)
#else
#define PRINCIPIA_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace principia {
namespace interface {

using geometry::_instant::InfinitePast;
using geometry::_instant::Instant;
using ksp_plugin::_chebyshev_segment_export::ExportTrajectoryViewToChebyshevSegments;
using ksp_plugin::_dxgi_adapter_factory::DxgiAdapterFactory;
using ksp_plugin::_flight_plan::FlightPlan;
using ksp_plugin::_frames::Barycentric;
using ksp_plugin::_frames::NavigationManœuvre;
using ksp_plugin::_gpu_interop_types::ChebyshevSegmentData;
using ksp_plugin::_gpu_interop_types::DmaTransferMode;
using ksp_plugin::_gpu_interop_types::GpuAdapterInfo;
using ksp_plugin::_gpu_interop_types::OptimalCandidateBurn;
using ksp_plugin::_gpu_interop_types::OrbitalState64;
using ksp_plugin::_gpu_interop_types::ShootingContextGpu;
using ksp_plugin::_gpu_interop_types::kMaxCelestialsForGpu;
using ksp_plugin::_planetarium::Planetarium;
using ksp_plugin::_plugin::Plugin;
using ksp_plugin::_vessel::Vessel;
using numerics::_elementary_functions::Pow;
using physics::_degrees_of_freedom::DegreesOfFreedom;
using physics::_discrete_trajectory::DiscreteTrajectory;
using physics::_discrete_trajectory_view::DiscreteTrajectoryView;
using quantities::_si::Metre;
using quantities::_si::Second;

//=============================================================================
// Hardware DMA, DXGI Multi-GPU Selection & Runtime Modes (Phase 4)
//=============================================================================

PRINCIPIA_EXPORT int __cdecl principia__GpuGetAdapterCount() {
  return DxgiAdapterFactory::Instance().GetAdapterCount();
}

PRINCIPIA_EXPORT bool __cdecl principia__GpuGetAdapterInfo(
    int const index,
    GpuAdapterInfo* const out_info) {
  return DxgiAdapterFactory::Instance().GetAdapterInfo(index, out_info);
}

PRINCIPIA_EXPORT void __cdecl principia__GpuSelectComputeAdapter(
    int const adapter_index) {
  DxgiAdapterFactory::Instance().SetSelectedAdapterIndex(adapter_index);
}

PRINCIPIA_EXPORT int __cdecl principia__GpuGetSelectedAdapter() {
  return DxgiAdapterFactory::Instance().GetSelectedAdapterIndex();
}

PRINCIPIA_EXPORT void __cdecl principia__GpuSetDmaMode(int const mode) {
  DxgiAdapterFactory::Instance().SetDmaMode(static_cast<DmaTransferMode>(mode));
}

PRINCIPIA_EXPORT int __cdecl principia__GpuGetDmaMode() {
  return static_cast<int>(DxgiAdapterFactory::Instance().GetDmaMode());
}

PRINCIPIA_EXPORT double __cdecl principia__GpuGetLastDmaWriteSeconds() {
  return DxgiAdapterFactory::Instance().GetLastDmaWriteSeconds();
}

PRINCIPIA_EXPORT std::uint64_t __cdecl principia__GpuGetTotalDmaBytes() {
  return DxgiAdapterFactory::Instance().GetTotalDmaBytes();
}

PRINCIPIA_EXPORT bool __cdecl principia__GpuBenchmarkDmaTransfer(
    int const segment_count,
    double* const out_duration_seconds,
    double* const out_bandwidth_gigabytes_per_second) {
  if (segment_count <= 0 || out_duration_seconds == nullptr ||
      out_bandwidth_gigabytes_per_second == nullptr) {
    return false;
  }

  int const count = std::min(std::max(segment_count, 16), 65536);
  std::size_t const total_bytes = static_cast<std::size_t>(count) * sizeof(ChebyshevSegmentData);

#if OS_WIN
  void* src_raw = _aligned_malloc(total_bytes, 64);
  void* dst_raw = _aligned_malloc(total_bytes, 64);
#else
  void* src_raw = std::aligned_alloc(64, total_bytes);
  void* dst_raw = std::aligned_alloc(64, total_bytes);
#endif
  if (src_raw == nullptr || dst_raw == nullptr) {
#if OS_WIN
    if (src_raw != nullptr) _aligned_free(src_raw);
    if (dst_raw != nullptr) _aligned_free(dst_raw);
#else
    if (src_raw != nullptr) std::free(src_raw);
    if (dst_raw != nullptr) std::free(dst_raw);
#endif
    return false;
  }

  auto* src = static_cast<ChebyshevSegmentData*>(src_raw);
  auto* dst = static_cast<ChebyshevSegmentData*>(dst_raw);

  for (int i = 0; i < count; ++i) {
    src[i].t_min = 100.0f * i;
    src[i].t_max = 100.0f * (i + 1);
    src[i].degree = 4;
    src[i].coeffs[0][0] = static_cast<float>(i);
    src[i].coeffs[0][1] = static_cast<float>(i * 2);
    src[i].coeffs[0][2] = static_cast<float>(i * 3);
  }
  std::memset(dst, 0, total_bytes);

  auto const mode = DxgiAdapterFactory::Instance().GetDmaMode();

  auto const start = std::chrono::high_resolution_clock::now();

  for (int i = 0; i < count; ++i) {
    ksp_plugin::_chebyshev_segment_export::WriteSegmentData(src[i], &dst[i], mode);
  }

  auto const end = std::chrono::high_resolution_clock::now();
  std::chrono::duration<double> const elapsed = end - start;
  double const duration_s = elapsed.count();

  bool verified = true;
  for (int i = 0; i < count; ++i) {
    if (dst[i].t_min != src[i].t_min ||
        dst[i].t_max != src[i].t_max ||
        dst[i].coeffs[0][0] != src[i].coeffs[0][0]) {
      verified = false;
      break;
    }
  }

#if OS_WIN
  _aligned_free(src_raw);
  _aligned_free(dst_raw);
#else
  std::free(src_raw);
  std::free(dst_raw);
#endif

  if (!verified) {
    return false;
  }

  *out_duration_seconds = duration_s;
  *out_bandwidth_gigabytes_per_second =
      duration_s > 0.0 ? ((static_cast<double>(total_bytes) / 1e9) / duration_s) : 0.0;

  DxgiAdapterFactory::Instance().RecordDmaWrite(total_bytes, duration_s);

  return true;
}

//=============================================================================
// Trajectory Chebyshev Polynomial DMA / Staging Exporter (Phase 1)
//=============================================================================

PRINCIPIA_EXPORT int __cdecl principia__ExportChebyshevSegments(
    Planetarium const* const planetarium,
    Plugin const* const plugin,
    char const* const vessel_guid,
    int const segment_index,
    double const* const t_max,
    ChebyshevSegmentData* const buffer,
    int const max_segments,
    bool const is_d3d11_buffer) {
  if (planetarium == nullptr || plugin == nullptr ||
      vessel_guid == nullptr || buffer == nullptr || max_segments <= 0) {
    return 0;
  }

  Vessel const* const vessel = plugin->GetVessel(vessel_guid);
  if (vessel == nullptr || !vessel->has_flight_plan()) {
    return 0;
  }

  auto const plotting_frame = plugin->renderer().GetPlottingFrame();
  auto const payload = vessel->GetPayload(plotting_frame);
  FlightPlan const& flight_plan = vessel->flight_plan();

  if (segment_index < 0 || segment_index >= flight_plan.number_of_segments()) {
    return 0;
  }

  auto const segment = flight_plan.GetSegment(segment_index);
  if (segment->empty() || segment->front().time >= plotting_frame->t_min()) {
    DiscreteTrajectoryView segment_view(&flight_plan.GetAllSegments(), segment);
    segment_view.Restrict(payload.plottable_time_interval);
    if (t_max != nullptr) {
      segment_view.Restrict(InfinitePast, FromGameTime(*plugin, *t_max));
    }
    if (segment_view.empty() || segment_view.t_max() <= segment_view.t_min()) {
      return 0;
    }
    return planetarium->ExportChebyshevSegments(
        segment_view, buffer, max_segments, is_d3d11_buffer);
  }
  return 0;
}

PRINCIPIA_EXPORT int __cdecl principia__ExportPredictionChebyshevSegments(
    Planetarium const* const planetarium,
    Plugin const* const plugin,
    char const* const vessel_guid,
    double const* const t_max,
    ChebyshevSegmentData* const buffer,
    int const max_segments,
    bool const is_d3d11_buffer) {
  if (planetarium == nullptr || plugin == nullptr ||
      vessel_guid == nullptr || buffer == nullptr || max_segments <= 0) {
    return 0;
  }

  Vessel const* const vessel = plugin->GetVessel(vessel_guid);
  if (vessel == nullptr) {
    return 0;
  }

  auto const plotting_frame = plugin->renderer().GetPlottingFrame();
  auto const payload = vessel->GetPayload(plotting_frame);
  auto const& prediction = vessel->prediction();
  if (prediction->empty()) {
    return 0;
  }

  DiscreteTrajectoryView prediction_view(&vessel->trajectory(), prediction);
  prediction_view.Restrict(payload.plottable_time_interval);
  if (t_max != nullptr) {
    prediction_view.Restrict(InfinitePast, FromGameTime(*plugin, *t_max));
  }
  if (prediction_view.empty() || prediction_view.t_max() <= prediction_view.t_min()) {
    return 0;
  }

  return planetarium->ExportChebyshevSegments(
      prediction_view, buffer, max_segments, is_d3d11_buffer);
}

//=============================================================================
// Flight Plan Shooting Optimizer Context & Co-Processing (Phase 2)
//=============================================================================

PRINCIPIA_EXPORT bool __cdecl principia__FlightPlanExportShootingContext(
    Plugin const* const plugin,
    char const* const vessel_guid,
    int const burn_index,
    ShootingContextGpu* const context) {
  if (plugin == nullptr || vessel_guid == nullptr || context == nullptr) {
    return false;
  }

  Vessel const* const vessel = plugin->GetVessel(vessel_guid);
  if (vessel == nullptr || !vessel->has_flight_plan()) {
    return false;
  }

  FlightPlan const& flight_plan = vessel->flight_plan();
  if (burn_index < 0 || burn_index >= flight_plan.number_of_manœuvres()) {
    return false;
  }

  NavigationManœuvre const& manœuvre = flight_plan.GetManœuvre(burn_index);
  Instant const initial_time = manœuvre.initial_time();

  if (vessel->psychohistory()->empty()) {
    return false;
  }

  DegreesOfFreedom<Barycentric> const initial_dof =
      vessel->psychohistory()->back().degrees_of_freedom;

  std::memset(context, 0, sizeof(ShootingContextGpu));
  context->initial_time = static_cast<float>((initial_time - Instant{}) / Second);

  auto const pos = (initial_dof.position() - Barycentric::origin).coordinates();
  context->initial_pos[0] = static_cast<float>(pos.x / Metre);
  context->initial_pos[1] = static_cast<float>(pos.y / Metre);
  context->initial_pos[2] = static_cast<float>(pos.z / Metre);

  auto const vel = initial_dof.velocity().coordinates();
  context->initial_vel[0] = static_cast<float>(vel.x / (Metre / Second));
  context->initial_vel[1] = static_cast<float>(vel.y / (Metre / Second));
  context->initial_vel[2] = static_cast<float>(vel.z / (Metre / Second));

  context->step_size_seconds = 10.0f;
  context->candidate_count = 16384;
  context->step_count = 256;

  // Export bodies from ephemeris
  auto const& ephemeris = *plugin->ephemeris();
  auto const& bodies = ephemeris.bodies();
  std::uint32_t celestial_idx = 0;
  for (auto const& body : bodies) {
    if (celestial_idx >= kMaxCelestialsForGpu) {
      break;
    }
    DegreesOfFreedom<Barycentric> const body_dof =
        ephemeris.trajectory(body)->EvaluateDegreesOfFreedom(initial_time);
    auto const bpos = (body_dof.position() - Barycentric::origin).coordinates();
    auto const bvel = body_dof.velocity().coordinates();

    context->celestials[celestial_idx].position[0] = static_cast<float>(bpos.x / Metre);
    context->celestials[celestial_idx].position[1] = static_cast<float>(bpos.y / Metre);
    context->celestials[celestial_idx].position[2] = static_cast<float>(bpos.z / Metre);
    context->celestials[celestial_idx].velocity[0] = static_cast<float>(bvel.x / (Metre / Second));
    context->celestials[celestial_idx].velocity[1] = static_cast<float>(bvel.y / (Metre / Second));
    context->celestials[celestial_idx].velocity[2] = static_cast<float>(bvel.z / (Metre / Second));
    context->celestials[celestial_idx].gravitational_parameter =
        static_cast<float>(body->gravitational_parameter() / (Pow<3>(Metre) / Pow<2>(Second)));
    ++celestial_idx;
  }
  context->celestial_count = celestial_idx;
  return true;
}

//=============================================================================
// Inactive Vessels Batch Packing & Unpacking (Phase 3)
//=============================================================================

PRINCIPIA_EXPORT int __cdecl principia__PackInactiveVesselsBatch(
    Plugin const* const plugin,
    OrbitalState64* const out_states,
    int const max_states) {
  if (plugin == nullptr || out_states == nullptr || max_states <= 0) {
    return 0;
  }
  return plugin->PackInactiveVesselsForGpu(out_states, max_states);
}

PRINCIPIA_EXPORT void __cdecl principia__UnpackInactiveVesselsBatch(
    Plugin* const plugin,
    OrbitalState64 const* const in_states,
    int const count) {
  if (plugin == nullptr || in_states == nullptr || count <= 0) {
    return;
  }
  plugin->UnpackInactiveVesselsFromGpu(in_states, count);
}

}  // namespace interface
}  // namespace principia
