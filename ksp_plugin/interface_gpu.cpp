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
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#undef interface
#endif

#include "absl/log/check.h"
#include "absl/log/log.h"
#include "geometry/instant.hpp"
#include "geometry/space.hpp"
#include "ksp_plugin/chebyshev_segment_export.hpp"
#include "ksp_plugin/compute_pipeline.hpp"
#include "ksp_plugin/dxgi_adapter_factory.hpp"
#include "ksp_plugin/flight_plan.hpp"
#include "ksp_plugin/frames.hpp"
#include "ksp_plugin/gpu_interop_types.hpp"
#include "ksp_plugin/hardware_profiler.hpp"
#include "ksp_plugin/time_sliced_scheduler.hpp"
#include "ksp_plugin/interface.hpp"
#include "ksp_plugin/planetarium.hpp"
#include "ksp_plugin/plugin.hpp"
#include "ksp_plugin/vessel.hpp"
#include "ksp_plugin/workload_balancer.hpp"
#include "numerics/elementary_functions.hpp"
#include "physics/degrees_of_freedom.hpp"
#include "physics/discrete_trajectory.hpp"
#include "physics/discrete_trajectory_view.hpp"
#include "physics/ephemeris.hpp"
#include "physics/ephemeris_l3_window_ring.hpp"
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
using ksp_plugin::_chebyshev_segment_export::WriteSegmentDataBatch;
using ksp_plugin::_dxgi_adapter_factory::DxgiAdapterFactory;
using ksp_plugin::_flight_plan::FlightPlan;
using ksp_plugin::_frames::Barycentric;
using ksp_plugin::_frames::NavigationManœuvre;
using ksp_plugin::_gpu_interop_types::ChebyshevSegmentData;
using ksp_plugin::_gpu_interop_types::DmaTransferMode;
using ksp_plugin::_gpu_interop_types::GpuAdapterInfo;
using ksp_plugin::_gpu_interop_types::GpuDeviceRole;
using ksp_plugin::_gpu_interop_types::GpuDeviceInfo;
using ksp_plugin::_gpu_interop_types::HardwareProfile;
using ksp_plugin::_gpu_interop_types::KspSceneType;
using ksp_plugin::_gpu_interop_types::OptimalCandidateBurn;
using ksp_plugin::_gpu_interop_types::OrbitalState64;
using ksp_plugin::_gpu_interop_types::ShootingContextGpu;
using ksp_plugin::_gpu_interop_types::WorkloadBalanceState;
using ksp_plugin::_gpu_interop_types::kMaxCelestialsForGpu;
using ksp_plugin::_hardware_profiler::HardwareProfiler;
using ksp_plugin::_workload_balancer::WorkloadBalancer;
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
#else
  void* src_raw = std::aligned_alloc(64, total_bytes);
#endif
  if (src_raw == nullptr) {
    return false;
  }

  auto* src = static_cast<ChebyshevSegmentData*>(src_raw);
  for (int i = 0; i < count; ++i) {
    src[i].t_min = 100.0f * i;
    src[i].t_max = 100.0f * (i + 1);
    src[i].degree = 4;
    src[i].coeffs[0][0] = static_cast<float>(i);
    src[i].coeffs[0][1] = static_cast<float>(i * 2);
    src[i].coeffs[0][2] = static_cast<float>(i * 3);
  }

  auto const mode = DxgiAdapterFactory::Instance().GetDmaMode();
  double duration_s = 0.0;
  bool hardware_vram_tested = false;

#if OS_WIN
  // Target true GPU Direct-Access / ReBAR PCIe BAR mapped VRAM with USWC (Write-Combining) memory attributes!
  HMODULE d3d11_mod = LoadLibraryW(L"d3d11.dll");
  if (d3d11_mod != nullptr) {
    using PFN_D3D11_CREATE_DEVICE = HRESULT(WINAPI*)(
        IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
        const D3D_FEATURE_LEVEL*, UINT, UINT,
        ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
    auto pfn_create_device = reinterpret_cast<PFN_D3D11_CREATE_DEVICE>(
        GetProcAddress(d3d11_mod, "D3D11CreateDevice"));

    if (pfn_create_device != nullptr) {
      IDXGIAdapter1* dxgi_adapter = nullptr;
      DxgiAdapterFactory::Instance().GetSelectedDxgiAdapter(&dxgi_adapter);

      ID3D11Device* device = nullptr;
      ID3D11DeviceContext* context = nullptr;
      D3D_FEATURE_LEVEL feature_level{};
      HRESULT hr = pfn_create_device(
          dxgi_adapter,
          dxgi_adapter != nullptr ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
          nullptr,
          0,
          nullptr,
          0,
          D3D11_SDK_VERSION,
          &device,
          &feature_level,
          &context);

      if (SUCCEEDED(hr) && device != nullptr && context != nullptr) {
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = static_cast<UINT>(total_bytes);
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
        desc.StructureByteStride = sizeof(ChebyshevSegmentData);

        ID3D11Buffer* d3d_buffer = nullptr;
        hr = device->CreateBuffer(&desc, nullptr, &d3d_buffer);
        if (SUCCEEDED(hr) && d3d_buffer != nullptr) {
          D3D11_MAPPED_SUBRESOURCE mapped{};
          hr = context->Map(d3d_buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
          if (SUCCEEDED(hr) && mapped.pData != nullptr) {
            auto* dst = static_cast<ChebyshevSegmentData*>(mapped.pData);

            // Precision GPU Direct DMA / ReBAR PCIe Write-Combining Bandwidth Benchmark:
            auto const start = std::chrono::high_resolution_clock::now();
            ksp_plugin::_chebyshev_segment_export::WriteSegmentDataBatch(
                src, dst, count, mode);
            context->Unmap(d3d_buffer, 0);
            auto const end = std::chrono::high_resolution_clock::now();

            std::chrono::duration<double> const elapsed = end - start;
            duration_s = elapsed.count();
            hardware_vram_tested = true;
          }
          d3d_buffer->Release();
        }
        context->Release();
        device->Release();
      }
      if (dxgi_adapter != nullptr) {
        dxgi_adapter->Release();
      }
    }
    FreeLibrary(d3d11_mod);
  }
#endif

  if (!hardware_vram_tested) {
#if OS_WIN
    void* dst_raw = _aligned_malloc(total_bytes, 64);
#else
    void* dst_raw = std::aligned_alloc(64, total_bytes);
#endif
    if (dst_raw == nullptr) {
#if OS_WIN
      _aligned_free(src_raw);
#else
      std::free(src_raw);
#endif
      return false;
    }
    auto* dst = static_cast<ChebyshevSegmentData*>(dst_raw);
    auto const start = std::chrono::high_resolution_clock::now();
    ksp_plugin::_chebyshev_segment_export::WriteSegmentDataBatch(
        src, dst, count, mode);
    auto const end = std::chrono::high_resolution_clock::now();
    std::chrono::duration<double> const elapsed = end - start;
    duration_s = elapsed.count();
#if OS_WIN
    _aligned_free(dst_raw);
#else
    std::free(dst_raw);
#endif
  }

#if OS_WIN
  _aligned_free(src_raw);
#else
  std::free(src_raw);
#endif

  *out_duration_seconds = duration_s;
  *out_bandwidth_gigabytes_per_second =
      duration_s > 0.0 ? ((static_cast<double>(total_bytes) / 1e9) / duration_s) : 0.0;

  DxgiAdapterFactory::Instance().RecordDmaWrite(total_bytes, duration_s);
  return true;
}

//=============================================================================
// Hardware Profiling, Calibration & Dynamic Workload Balancer
//=============================================================================

PRINCIPIA_EXPORT bool __cdecl principia__RunHardwareCalibration(
    HardwareProfile* const out_profile) {
  if (out_profile == nullptr) {
    return false;
  }
  *out_profile = HardwareProfiler::Instance().RunCalibration();
  return true;
}

PRINCIPIA_EXPORT bool __cdecl principia__GetHardwareProfile(
    HardwareProfile* const out_profile) {
  if (out_profile == nullptr) {
    return false;
  }
  *out_profile = HardwareProfiler::Instance().GetProfile();
  return true;
}

PRINCIPIA_EXPORT void __cdecl principia__UpdateSceneFeedback(
    int const scene_type,
    double const render_time_ms,
    double const physics_time_ms) {
  WorkloadBalancer::Instance().UpdateSceneFeedback(
      scene_type, render_time_ms, physics_time_ms);
}

PRINCIPIA_EXPORT void __cdecl principia__GetWorkloadBalanceState(
    WorkloadBalanceState* const out_state) {
  if (out_state == nullptr) {
    return;
  }
  *out_state = WorkloadBalancer::Instance().GetState();
}

PRINCIPIA_EXPORT void __cdecl principia__SetWorkloadBypass(
    bool const master_bypass,
    int const module_mask) {
  WorkloadBalancer::Instance().SetMasterBypass(master_bypass, module_mask);
}

PRINCIPIA_EXPORT void __cdecl principia__SetManualWorkloadOverride(
    bool const enable_manual,
    double const manual_gpu_ratio) {
  WorkloadBalancer::Instance().SetManualOverride(enable_manual, manual_gpu_ratio);
}

PRINCIPIA_EXPORT void __cdecl principia__PartitionVesselBatch(
    int const total_vessels,
    int* const out_cpu_count,
    int* const out_gpu_count) {
  WorkloadBalancer::Instance().PartitionVesselBatch(
      total_vessels, out_cpu_count, out_gpu_count);
}

PRINCIPIA_EXPORT bool __cdecl principia__GpuGetDeviceInfo(
    int const index,
    GpuDeviceInfo* const out_info) {
  return HardwareProfiler::Instance().GetDeviceInfo(index, out_info);
}

PRINCIPIA_EXPORT void __cdecl principia__GpuSetDeviceRole(
    int const adapter_index,
    int const role) {
  HardwareProfiler::Instance().SetDeviceRole(
      adapter_index, static_cast<GpuDeviceRole>(role));
}

PRINCIPIA_EXPORT void __cdecl principia__GpuGetMultiDeviceSummary(
    int* const out_device_count,
    int* const out_active_count,
    double* const out_total_compute_score) {
  if (out_device_count != nullptr) {
    *out_device_count = HardwareProfiler::Instance().GetDeviceCount();
  }
  auto const profile = HardwareProfiler::Instance().GetProfile();
  if (out_active_count != nullptr) {
    *out_active_count = profile.active_gpu_count;
  }
  if (out_total_compute_score != nullptr) {
    *out_total_compute_score = profile.total_gpu_compute_score;
  }
}

PRINCIPIA_EXPORT void __cdecl principia__PartitionMultiDeviceBatch(
    int const total_work,
    int* const out_device_counts,
    int const max_devices) {
  WorkloadBalancer::Instance().PartitionMultiDeviceBatch(
      total_work, out_device_counts, max_devices);
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

PRINCIPIA_EXPORT bool __cdecl principia__AdvanceInactiveVesselsBatchGpu(
    Plugin* const plugin,
    double const time_step_seconds,
    int const sub_steps) {
  if (plugin == nullptr || time_step_seconds <= 0.0 || sub_steps <= 0) {
    return false;
  }
  constexpr int kMaxBatch = 4096;
  std::vector<OrbitalState64> states(kMaxBatch);
  int const count = plugin->PackInactiveVesselsForGpu(states.data(), kMaxBatch);
  if (count <= 0) {
    return false;
  }
  states.resize(count);
  ksp_plugin::_compute_pipeline::ComputePipeline::Instance().AdvanceBatch(
      states.data(), count, time_step_seconds, sub_steps);
  plugin->UnpackInactiveVesselsFromGpu(states.data(), count);
  return true;
}

PRINCIPIA_EXPORT bool __cdecl principia__OptimizeFlightPlanShooting(
    ShootingContextGpu const* const context,
    OptimalCandidateBurn* const out_optimal) {
  if (context == nullptr || out_optimal == nullptr) {
    return false;
  }
  return ksp_plugin::_compute_pipeline::ComputePipeline::Instance()
      .OptimizeFlightPlanShooting(*context, *out_optimal);
}

//=============================================================================
// CPU Time-Sliced Scheduling & Multi-Thread Module Telemetry
//=============================================================================

PRINCIPIA_EXPORT void __cdecl principia__GetCpuModuleThreadingStatus(
    Plugin const* const plugin,
    ksp_plugin::_time_sliced_scheduler::CpuModuleThreadingStatus* const status) {
  if (status == nullptr) {
    return;
  }
  if (plugin == nullptr) {
    *status = ksp_plugin::_time_sliced_scheduler::TimeSlicedScheduler::Instance()
        .GetStatusSnapshot(
            static_cast<int>(std::thread::hardware_concurrency()),
            0, 0, 0, 0, 0, 0, 0);
    return;
  }
  *status = plugin->GetCpuModuleThreadingStatus();
}

PRINCIPIA_EXPORT void __cdecl principia__SetFrameBudgetMilliseconds(
    double const ms) {
  ksp_plugin::_time_sliced_scheduler::TimeSlicedScheduler::Instance()
      .SetFrameBudgetMilliseconds(ms);
}

PRINCIPIA_EXPORT bool __cdecl principia__SaveMasterEphemerisCache(
    char const* const file_path) {
  if (file_path == nullptr) {
    return false;
  }
  return physics::_ephemeris_l3_window_ring::EphemerisL3WindowRing::Instance()
      .SaveMasterEphemeris(file_path);
}

PRINCIPIA_EXPORT bool __cdecl principia__LoadMasterEphemerisCache(
    char const* const file_path) {
  if (file_path == nullptr) {
    return false;
  }
  return physics::_ephemeris_l3_window_ring::EphemerisL3WindowRing::Instance()
      .LoadMasterEphemeris(file_path);
}

}  // namespace interface
}  // namespace principia
