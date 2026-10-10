#include "ksp_plugin/compute_pipeline.hpp"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

#include "absl/log/log.h"
#include "ksp_plugin/workload_balancer.hpp"
#include "ksp_plugin/dxgi_adapter_factory.hpp"

#if OS_WIN
#include "ksp_plugin/batch_orbit_integrator_cs.h"
#include "ksp_plugin/shooting_grid_cs.h"
#include "ksp_plugin/shooting_eval_cs.h"
#include "ksp_plugin/shooting_reduce_cs.h"
#include "ksp_plugin/shooting_reduce_final_cs.h"
#include "ksp_plugin/trajectory_tessellator_cs.h"
#endif

namespace principia {
namespace ksp_plugin {
namespace _compute_pipeline {
namespace internal {

using _dxgi_adapter_factory::DxgiAdapterFactory;
using _gpu_interop_types::CelestialBodyGpuState;
using _gpu_interop_types::ChebyshevSegmentData;
using _gpu_interop_types::DoubleSingle;
using _gpu_interop_types::DsVec3;
using _gpu_interop_types::FromDoubleSingle;
using _gpu_interop_types::OptimalCandidateBurn;
using _gpu_interop_types::OrbitalState64;
using _gpu_interop_types::ShootingContextGpu;
using _gpu_interop_types::ToDoubleSingle;
using _gpu_interop_types::kMaxCelestialsForGpu;
using _gpu_interop_types::kMaxChebyshevDegree;
using _workload_balancer::WorkloadBalancer;

namespace {

// Matches CelestialData in BatchOrbitIntegrator.compute (64 bytes)
struct alignas(16) CelestialDataGpu {
  DsVec3 position;
  DoubleSingle gravitational_parameter;
  DsVec3 velocity;
  float radius;
  float padding;
};
static_assert(sizeof(CelestialDataGpu) == 64, "CelestialDataGpu must be 64 bytes");

// Matches BatchIntegratorConfig cbuffer in BatchOrbitIntegrator.compute (16 bytes)
struct alignas(16) BatchIntegratorConfig {
  std::uint32_t vessel_count;
  std::uint32_t celestial_count;
  float time_step_seconds;
  std::uint32_t sub_steps;
};
static_assert(sizeof(BatchIntegratorConfig) == 16, "BatchIntegratorConfig must be 16 bytes");

// Matches OptimizerConfig cbuffer in FlightPlanShootingOptimizer.compute (128 bytes)
struct alignas(16) OptimizerConfigGpu {
  float base_burn_times[4];
  float base_delta_v[4];
  float span_delta_v[4];
  float optimization_weights[4];
  float initial_vessel_pos[4];
  float initial_vessel_vel[4];
  float grid_dimensions[4];
  std::uint32_t celestial_count;
  std::uint32_t step_count;
  std::uint32_t target_celestial_index;
  std::uint32_t reduction_item_count;
};
static_assert(sizeof(OptimizerConfigGpu) == 128, "OptimizerConfigGpu must be 128 bytes");

// Evaluates a 3D Chebyshev polynomial at tau in [-1, 1] using Clenshaw recurrence.
inline void EvaluateChebyshevClenshaw(
    float const coeffs[kMaxChebyshevDegree + 1][3],
    int const degree,
    float const tau,
    float out_pos[3]) {
  float const two_tau = 2.0f * tau;

  for (int dim = 0; dim < 3; ++dim) {
    float b_k_plus_2 = 0.0f;
    float b_k_plus_1 = 0.0f;
    for (int k = degree; k >= 1; --k) {
      float const b_k = two_tau * b_k_plus_1 - b_k_plus_2 + coeffs[k][dim];
      b_k_plus_2 = b_k_plus_1;
      b_k_plus_1 = b_k;
    }
    out_pos[dim] = tau * b_k_plus_1 - b_k_plus_2 + coeffs[0][dim];
  }
}

}  // namespace

ComputePipeline& ComputePipeline::Instance() {
  static ComputePipeline instance;
  return instance;
}

ComputePipeline::ComputePipeline() {
  InitializeShadersIfPossible();
}

ComputePipeline::~ComputePipeline() {
  ReleaseD3D11Resources();
}

void ComputePipeline::ReleaseD3D11Resources() {
#if OS_WIN
  auto safe_release = [](auto*& res) {
    if (res != nullptr) {
      res->Release();
      res = nullptr;
    }
  };

  safe_release(batch_orbit_cb_);
  safe_release(batch_orbit_celestial_srv_);
  safe_release(batch_orbit_celestial_buf_);
  safe_release(batch_orbit_states_uav_);
  safe_release(batch_orbit_states_staging_);
  safe_release(batch_orbit_states_buf_);
  cached_celestial_count_ = 0;
  cached_vessel_count_ = 0;

  safe_release(shooting_cb_);
  safe_release(shooting_celestial_srv_);
  safe_release(shooting_celestial_buf_);
  safe_release(shooting_grid_uav_);
  safe_release(shooting_grid_buf_);
  safe_release(shooting_eval_uav_);
  safe_release(shooting_eval_buf_);
  safe_release(shooting_reduce_uav_);
  safe_release(shooting_reduce_staging_);
  safe_release(shooting_reduce_buf_);

  safe_release(batch_orbit_shader_);
  safe_release(trajectory_tessellator_shader_);
  safe_release(shooting_grid_shader_);
  safe_release(shooting_eval_shader_);
  safe_release(shooting_reduce_shader_);
  safe_release(shooting_reduce_final_shader_);

  safe_release(d3d11_context_);
  safe_release(d3d11_device_);
#endif
}

bool ComputePipeline::EnsureD3D11Initialized() {
#if OS_WIN
  if (d3d11_device_ != nullptr && d3d11_context_ != nullptr) {
    return true;
  }
  HMODULE const d3d11_mod = LoadLibraryW(L"d3d11.dll");
  if (d3d11_mod == nullptr) {
    return false;
  }
  using PFN_D3D11_CREATE_DEVICE = HRESULT(WINAPI*)(
      IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
      const D3D_FEATURE_LEVEL*, UINT, UINT,
      ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
  auto const pfn_create_device = reinterpret_cast<PFN_D3D11_CREATE_DEVICE>(
      GetProcAddress(d3d11_mod, "D3D11CreateDevice"));
  if (pfn_create_device == nullptr) {
    FreeLibrary(d3d11_mod);
    return false;
  }

  IDXGIAdapter1* dxgi_adapter = nullptr;
  DxgiAdapterFactory::Instance().GetSelectedDxgiAdapter(&dxgi_adapter);

  ID3D11Device* device = nullptr;
  ID3D11DeviceContext* context = nullptr;
  D3D_FEATURE_LEVEL feature_level{};
  D3D_FEATURE_LEVEL const feature_levels[] = {
      D3D_FEATURE_LEVEL_11_1,
      D3D_FEATURE_LEVEL_11_0,
  };
  HRESULT const hr = pfn_create_device(
      dxgi_adapter,
      dxgi_adapter != nullptr ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
      nullptr,
      0,
      feature_levels,
      2,
      D3D11_SDK_VERSION,
      &device,
      &feature_level,
      &context);
  if (dxgi_adapter != nullptr) {
    dxgi_adapter->Release();
  }
  FreeLibrary(d3d11_mod);

  if (SUCCEEDED(hr) && device != nullptr && context != nullptr) {
    d3d11_device_ = device;
    d3d11_context_ = context;
    InitializeShadersIfPossible();
    return true;
  }
#endif
  return false;
}

void ComputePipeline::InitializeD3D11(void* d3d11_device, void* d3d11_context) {
#if OS_WIN
  ReleaseD3D11Resources();
  if (d3d11_device != nullptr) {
    d3d11_device_ = static_cast<ID3D11Device*>(d3d11_device);
    d3d11_device_->AddRef();
  }
  if (d3d11_context != nullptr) {
    d3d11_context_ = static_cast<ID3D11DeviceContext*>(d3d11_context);
    d3d11_context_->AddRef();
  }
  InitializeShadersIfPossible();
#endif
}

void ComputePipeline::InitializeShadersIfPossible() {
#if OS_WIN
  if (d3d11_device_ == nullptr) return;

  if (batch_orbit_shader_ == nullptr) {
    d3d11_device_->CreateComputeShader(
        g_batch_orbit_integrator_cs,
        sizeof(g_batch_orbit_integrator_cs),
        nullptr,
        &batch_orbit_shader_);
  }
  if (trajectory_tessellator_shader_ == nullptr) {
    d3d11_device_->CreateComputeShader(
        g_trajectory_tessellator_cs,
        sizeof(g_trajectory_tessellator_cs),
        nullptr,
        &trajectory_tessellator_shader_);
  }
  if (shooting_grid_shader_ == nullptr) {
    d3d11_device_->CreateComputeShader(
        g_shooting_grid_cs,
        sizeof(g_shooting_grid_cs),
        nullptr,
        &shooting_grid_shader_);
  }
  if (shooting_eval_shader_ == nullptr) {
    d3d11_device_->CreateComputeShader(
        g_shooting_eval_cs,
        sizeof(g_shooting_eval_cs),
        nullptr,
        &shooting_eval_shader_);
  }
  if (shooting_reduce_shader_ == nullptr) {
    d3d11_device_->CreateComputeShader(
        g_shooting_reduce_cs,
        sizeof(g_shooting_reduce_cs),
        nullptr,
        &shooting_reduce_shader_);
  }
  if (shooting_reduce_final_shader_ == nullptr) {
    d3d11_device_->CreateComputeShader(
        g_shooting_reduce_final_cs,
        sizeof(g_shooting_reduce_final_cs),
        nullptr,
        &shooting_reduce_final_shader_);
  }
#endif
}

void ComputePipeline::SetCelestialBodies(
    CelestialBodyGpuState const* celestials,
    int celestial_count) {
  if (celestials == nullptr || celestial_count <= 0) {
    cached_celestials_.clear();
    return;
  }
  cached_celestials_.assign(celestials, celestials + celestial_count);
}

void ComputePipeline::AdvanceBatch(
    OrbitalState64* states,
    int const count,
    double const time_step_seconds,
    int const sub_steps) {
  if (states == nullptr || count <= 0) return;

  // Let WorkloadBalancer arbitrate based on batch size and scene pressure.
  bool const use_gpu = WorkloadBalancer::Instance().ShouldDispatchGpu(count);

#if OS_WIN
  if (use_gpu && EnsureD3D11Initialized() && batch_orbit_shader_ != nullptr &&
      !cached_celestials_.empty()) {
    // 1. Prepare Celestial buffer (StructuredBuffer<CelestialData> _CelestialBodies : register(t0))
    if (batch_orbit_celestial_buf_ == nullptr ||
        cached_celestial_count_ != static_cast<int>(cached_celestials_.size())) {
      if (batch_orbit_celestial_srv_ != nullptr) {
        batch_orbit_celestial_srv_->Release();
        batch_orbit_celestial_srv_ = nullptr;
      }
      if (batch_orbit_celestial_buf_ != nullptr) {
        batch_orbit_celestial_buf_->Release();
        batch_orbit_celestial_buf_ = nullptr;
      }

      std::vector<CelestialDataGpu> gpu_celestials(cached_celestials_.size());
      for (std::size_t i = 0; i < cached_celestials_.size(); ++i) {
        gpu_celestials[i].position.x = ToDoubleSingle(cached_celestials_[i].position[0]);
        gpu_celestials[i].position.y = ToDoubleSingle(cached_celestials_[i].position[1]);
        gpu_celestials[i].position.z = ToDoubleSingle(cached_celestials_[i].position[2]);
        gpu_celestials[i].gravitational_parameter =
            ToDoubleSingle(cached_celestials_[i].gravitational_parameter);
        gpu_celestials[i].velocity.x = ToDoubleSingle(cached_celestials_[i].velocity[0]);
        gpu_celestials[i].velocity.y = ToDoubleSingle(cached_celestials_[i].velocity[1]);
        gpu_celestials[i].velocity.z = ToDoubleSingle(cached_celestials_[i].velocity[2]);
        gpu_celestials[i].radius = 0.0f;
        gpu_celestials[i].padding = 0.0f;
      }

      D3D11_BUFFER_DESC desc{};
      desc.ByteWidth = static_cast<UINT>(gpu_celestials.size() * sizeof(CelestialDataGpu));
      desc.Usage = D3D11_USAGE_DEFAULT;
      desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
      desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
      desc.StructureByteStride = sizeof(CelestialDataGpu);

      D3D11_SUBRESOURCE_DATA init_data{};
      init_data.pSysMem = gpu_celestials.data();

      HRESULT const hr = d3d11_device_->CreateBuffer(&desc, &init_data, &batch_orbit_celestial_buf_);
      if (SUCCEEDED(hr) && batch_orbit_celestial_buf_ != nullptr) {
        D3D11_SHADER_RESOURCE_VIEW_DESC srv_desc{};
        srv_desc.Format = DXGI_FORMAT_UNKNOWN;
        srv_desc.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
        srv_desc.Buffer.FirstElement = 0;
        srv_desc.Buffer.NumElements = static_cast<UINT>(gpu_celestials.size());
        d3d11_device_->CreateShaderResourceView(
            batch_orbit_celestial_buf_, &srv_desc, &batch_orbit_celestial_srv_);
        cached_celestial_count_ = static_cast<int>(cached_celestials_.size());
      }
    }

    // 2. Prepare Vessel States buffer (RWStructuredBuffer<OrbitalState64> _VesselStates : register(u0))
    if (batch_orbit_states_buf_ == nullptr || cached_vessel_count_ < count) {
      if (batch_orbit_states_uav_ != nullptr) {
        batch_orbit_states_uav_->Release();
        batch_orbit_states_uav_ = nullptr;
      }
      if (batch_orbit_states_staging_ != nullptr) {
        batch_orbit_states_staging_->Release();
        batch_orbit_states_staging_ = nullptr;
      }
      if (batch_orbit_states_buf_ != nullptr) {
        batch_orbit_states_buf_->Release();
        batch_orbit_states_buf_ = nullptr;
      }

      int const alloc_count = std::max(count, 1024);
      D3D11_BUFFER_DESC buf_desc{};
      buf_desc.ByteWidth = static_cast<UINT>(alloc_count * sizeof(OrbitalState64));
      buf_desc.Usage = D3D11_USAGE_DEFAULT;
      buf_desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
      buf_desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
      buf_desc.StructureByteStride = sizeof(OrbitalState64);

      HRESULT const hr = d3d11_device_->CreateBuffer(&buf_desc, nullptr, &batch_orbit_states_buf_);
      if (SUCCEEDED(hr) && batch_orbit_states_buf_ != nullptr) {
        D3D11_UNORDERED_ACCESS_VIEW_DESC uav_desc{};
        uav_desc.Format = DXGI_FORMAT_UNKNOWN;
        uav_desc.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
        uav_desc.Buffer.FirstElement = 0;
        uav_desc.Buffer.NumElements = static_cast<UINT>(alloc_count);
        d3d11_device_->CreateUnorderedAccessView(
            batch_orbit_states_buf_, &uav_desc, &batch_orbit_states_uav_);

        // Staging buffer for readback
        D3D11_BUFFER_DESC staging_desc = buf_desc;
        staging_desc.Usage = D3D11_USAGE_STAGING;
        staging_desc.BindFlags = 0;
        staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        d3d11_device_->CreateBuffer(&staging_desc, nullptr, &batch_orbit_states_staging_);
        cached_vessel_count_ = alloc_count;
      }
    }

    // 3. Prepare Config ConstantBuffer (BatchIntegratorConfig : register(b0))
    if (batch_orbit_cb_ == nullptr) {
      D3D11_BUFFER_DESC cb_desc{};
      cb_desc.ByteWidth = sizeof(BatchIntegratorConfig);
      cb_desc.Usage = D3D11_USAGE_DEFAULT;
      cb_desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
      d3d11_device_->CreateBuffer(&cb_desc, nullptr, &batch_orbit_cb_);
    }

    if (batch_orbit_celestial_srv_ != nullptr &&
        batch_orbit_states_uav_ != nullptr &&
        batch_orbit_states_staging_ != nullptr &&
        batch_orbit_cb_ != nullptr) {
      BatchIntegratorConfig config{
          static_cast<std::uint32_t>(count),
          static_cast<std::uint32_t>(cached_celestials_.size()),
          static_cast<float>(time_step_seconds),
          static_cast<std::uint32_t>(sub_steps)
      };
      d3d11_context_->UpdateSubresource(batch_orbit_cb_, 0, nullptr, &config, 0, 0);
      d3d11_context_->UpdateSubresource(batch_orbit_states_buf_, 0, nullptr, states, 0, 0);

      // Bind pipeline
      d3d11_context_->CSSetShader(batch_orbit_shader_, nullptr, 0);
      d3d11_context_->CSSetConstantBuffers(0, 1, &batch_orbit_cb_);
      d3d11_context_->CSSetShaderResources(0, 1, &batch_orbit_celestial_srv_);
      d3d11_context_->CSSetUnorderedAccessViews(0, 1, &batch_orbit_states_uav_, nullptr);

      // Dispatch
      UINT const num_groups = (static_cast<UINT>(count) + 127) / 128;
      d3d11_context_->Dispatch(num_groups, 1, 1);

      // Unbind UAV to allow copying
      ID3D11UnorderedAccessView* null_uav = nullptr;
      d3d11_context_->CSSetUnorderedAccessViews(0, 1, &null_uav, nullptr);
      ID3D11ShaderResourceView* null_srv = nullptr;
      d3d11_context_->CSSetShaderResources(0, 1, &null_srv);

      // Readback
      d3d11_context_->CopyResource(batch_orbit_states_staging_, batch_orbit_states_buf_);
      D3D11_MAPPED_SUBRESOURCE mapped{};
      HRESULT const map_hr = d3d11_context_->Map(
          batch_orbit_states_staging_, 0, D3D11_MAP_READ, 0, &mapped);
      if (SUCCEEDED(map_hr) && mapped.pData != nullptr) {
        std::memcpy(states, mapped.pData, count * sizeof(OrbitalState64));
        d3d11_context_->Unmap(batch_orbit_states_staging_, 0);
        return;  // Successfully stepped via GPU DirectCompute!
      }
    }
  }
#endif

  // Robust bit-exact CPU numerical fallback.
  AdvanceBatchCpu(states, count, time_step_seconds, sub_steps);
}

void ComputePipeline::AdvanceBatchCpu(
    OrbitalState64* states,
    int const count,
    double const time_step_seconds,
    int const sub_steps) {
  if (states == nullptr || count <= 0 || cached_celestials_.empty()) return;

  double const half_dt = time_step_seconds * 0.5;

  for (int i = 0; i < count; ++i) {
    double px = FromDoubleSingle(states[i].position.x);
    double py = FromDoubleSingle(states[i].position.y);
    double pz = FromDoubleSingle(states[i].position.z);
    double vx = FromDoubleSingle(states[i].velocity.x);
    double vy = FromDoubleSingle(states[i].velocity.y);
    double vz = FromDoubleSingle(states[i].velocity.z);

    for (int step = 0; step < sub_steps; ++step) {
      // Step 1: Compute gravitational acceleration a(t)
      double ax = 0.0, ay = 0.0, az = 0.0;
      for (auto const& c : cached_celestials_) {
        double const rx = static_cast<double>(c.position[0]) - px;
        double const ry = static_cast<double>(c.position[1]) - py;
        double const rz = static_cast<double>(c.position[2]) - pz;
        double const r2 = rx * rx + ry * ry + rz * rz + 1.0;
        double const r = std::sqrt(r2);
        double const r3 = r2 * r;
        double const factor = static_cast<double>(c.gravitational_parameter) / r3;
        ax += rx * factor;
        ay += ry * factor;
        az += rz * factor;
      }

      // Step 2: Velocity half-step
      vx += ax * half_dt;
      vy += ay * half_dt;
      vz += az * half_dt;

      // Step 3: Position full-step
      px += vx * time_step_seconds;
      py += vy * time_step_seconds;
      pz += vz * time_step_seconds;

      // Step 4: Compute next gravitational acceleration a(t + dt)
      double ax2 = 0.0, ay2 = 0.0, az2 = 0.0;
      for (auto const& c : cached_celestials_) {
        double const rx = static_cast<double>(c.position[0]) - px;
        double const ry = static_cast<double>(c.position[1]) - py;
        double const rz = static_cast<double>(c.position[2]) - pz;
        double const r2 = rx * rx + ry * ry + rz * rz + 1.0;
        double const r = std::sqrt(r2);
        double const r3 = r2 * r;
        double const factor = static_cast<double>(c.gravitational_parameter) / r3;
        ax2 += rx * factor;
        ay2 += ry * factor;
        az2 += rz * factor;
      }

      // Step 5: Velocity second half-step
      vx += ax2 * half_dt;
      vy += ay2 * half_dt;
      vz += az2 * half_dt;
    }

    states[i].position.x = ToDoubleSingle(px);
    states[i].position.y = ToDoubleSingle(py);
    states[i].position.z = ToDoubleSingle(pz);
    states[i].velocity.x = ToDoubleSingle(vx);
    states[i].velocity.y = ToDoubleSingle(vy);
    states[i].velocity.z = ToDoubleSingle(vz);
  }
}

int ComputePipeline::EvaluateChebyshevBatch(
    ChebyshevSegmentData const* segments,
    int const segment_count,
    TrajectoryVertex* out_vertices,
    int const max_vertices) {
  if (segments == nullptr || out_vertices == nullptr || segment_count <= 0 || max_vertices <= 0) {
    return 0;
  }

  // Clenshaw continuous curve evaluation.
  return EvaluateChebyshevBatchCpu(segments, segment_count, out_vertices, max_vertices);
}

int ComputePipeline::EvaluateChebyshevBatchCpu(
    ChebyshevSegmentData const* segments,
    int const segment_count,
    TrajectoryVertex* out_vertices,
    int const max_vertices) {
  if (segments == nullptr || out_vertices == nullptr || segment_count <= 0 || max_vertices <= 0) {
    return 0;
  }

  int written_vertices = 0;
  constexpr int samples_per_segment = 16;

  for (int s = 0; s < segment_count; ++s) {
    ChebyshevSegmentData const& seg = segments[s];
    int const deg = std::clamp(static_cast<int>(seg.degree), 0, kMaxChebyshevDegree);
    float const t_span = seg.t_max - seg.t_min;
    if (t_span <= 0.0f) continue;

    for (int step = 0; step < samples_per_segment; ++step) {
      if (written_vertices >= max_vertices) {
        return written_vertices;
      }

      float const u = static_cast<float>(step) / (samples_per_segment - 1);
      float const tau = 2.0f * u - 1.0f;  // Map to [-1, 1]
      double const t = seg.t_min + static_cast<double>(u) * t_span;

      TrajectoryVertex& vert = out_vertices[written_vertices++];
      EvaluateChebyshevClenshaw(seg.coeffs, deg, tau, vert.position);
      vert.velocity[0] = 0.0f;
      vert.velocity[1] = 0.0f;
      vert.velocity[2] = 0.0f;
      vert.colour[0] = 0.2f;
      vert.colour[1] = 0.8f;
      vert.colour[2] = 1.0f;
      vert.colour[3] = 1.0f;
      vert.time_seconds = t;
    }
  }

  return written_vertices;
}

bool ComputePipeline::OptimizeFlightPlanShooting(
    ShootingContextGpu const& context,
    OptimalCandidateBurn& out_optimal) {
  bool const use_gpu = WorkloadBalancer::Instance().ShouldDispatchGpu(16384);
  if (use_gpu) {
    if (OptimizeFlightPlanShootingGpu(context, out_optimal)) {
      return true;
    }
  }
  return OptimizeFlightPlanShootingCpu(context, out_optimal);
}

bool ComputePipeline::OptimizeFlightPlanShootingGpu(
    ShootingContextGpu const& context,
    OptimalCandidateBurn& out_optimal) {
#if OS_WIN
  if (!EnsureD3D11Initialized()) {
    return false;
  }
  if (shooting_grid_shader_ == nullptr || shooting_eval_shader_ == nullptr ||
      shooting_reduce_shader_ == nullptr || shooting_reduce_final_shader_ == nullptr) {
    return false;
  }

  // 1. Optimizer Config Constant Buffer (128 bytes)
  if (shooting_cb_ == nullptr) {
    D3D11_BUFFER_DESC cb_desc{};
    cb_desc.ByteWidth = sizeof(OptimizerConfigGpu);
    cb_desc.Usage = D3D11_USAGE_DEFAULT;
    cb_desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    d3d11_device_->CreateBuffer(&cb_desc, nullptr, &shooting_cb_);
  }

  // 2. Celestials Buffer (SRV t0)
  std::uint32_t const celestial_count = std::max(
      1u, std::min(context.celestial_count, static_cast<std::uint32_t>(kMaxCelestialsForGpu)));
  if (shooting_celestial_buf_ == nullptr) {
    D3D11_BUFFER_DESC cel_desc{};
    cel_desc.ByteWidth = static_cast<UINT>(kMaxCelestialsForGpu * sizeof(CelestialBodyGpuState));
    cel_desc.Usage = D3D11_USAGE_DEFAULT;
    cel_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    cel_desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    cel_desc.StructureByteStride = sizeof(CelestialBodyGpuState);
    d3d11_device_->CreateBuffer(&cel_desc, nullptr, &shooting_celestial_buf_);

    D3D11_SHADER_RESOURCE_VIEW_DESC srv_desc{};
    srv_desc.Format = DXGI_FORMAT_UNKNOWN;
    srv_desc.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    srv_desc.Buffer.FirstElement = 0;
    srv_desc.Buffer.NumElements = kMaxCelestialsForGpu;
    d3d11_device_->CreateShaderResourceView(
        shooting_celestial_buf_, &srv_desc, &shooting_celestial_srv_);
  }

  // 3. Candidate Grid Buffer (UAV u0): 16384 * 16 bytes = 256 KB
  constexpr std::uint32_t kTotalCandidates = 16384;
  if (shooting_grid_buf_ == nullptr) {
    D3D11_BUFFER_DESC grid_desc{};
    grid_desc.ByteWidth = kTotalCandidates * 16;  // CandidateBurnParameters
    grid_desc.Usage = D3D11_USAGE_DEFAULT;
    grid_desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    grid_desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    grid_desc.StructureByteStride = 16;
    d3d11_device_->CreateBuffer(&grid_desc, nullptr, &shooting_grid_buf_);

    D3D11_UNORDERED_ACCESS_VIEW_DESC uav_desc{};
    uav_desc.Format = DXGI_FORMAT_UNKNOWN;
    uav_desc.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    uav_desc.Buffer.FirstElement = 0;
    uav_desc.Buffer.NumElements = kTotalCandidates;
    d3d11_device_->CreateUnorderedAccessView(
        shooting_grid_buf_, &uav_desc, &shooting_grid_uav_);
  }

  // 4. Evaluations Buffer (UAV u1): 16384 * 32 bytes = 512 KB
  if (shooting_eval_buf_ == nullptr) {
    D3D11_BUFFER_DESC eval_desc{};
    eval_desc.ByteWidth = kTotalCandidates * sizeof(OptimalCandidateBurn);
    eval_desc.Usage = D3D11_USAGE_DEFAULT;
    eval_desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    eval_desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    eval_desc.StructureByteStride = sizeof(OptimalCandidateBurn);
    d3d11_device_->CreateBuffer(&eval_desc, nullptr, &shooting_eval_buf_);

    D3D11_UNORDERED_ACCESS_VIEW_DESC uav_desc{};
    uav_desc.Format = DXGI_FORMAT_UNKNOWN;
    uav_desc.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    uav_desc.Buffer.FirstElement = 0;
    uav_desc.Buffer.NumElements = kTotalCandidates;
    d3d11_device_->CreateUnorderedAccessView(
        shooting_eval_buf_, &uav_desc, &shooting_eval_uav_);
  }

  // 5. Reduced Results Buffer (UAV u2): 64 * 32 bytes = 2048 bytes
  if (shooting_reduce_buf_ == nullptr) {
    D3D11_BUFFER_DESC red_desc{};
    red_desc.ByteWidth = 64 * sizeof(OptimalCandidateBurn);
    red_desc.Usage = D3D11_USAGE_DEFAULT;
    red_desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    red_desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    red_desc.StructureByteStride = sizeof(OptimalCandidateBurn);
    d3d11_device_->CreateBuffer(&red_desc, nullptr, &shooting_reduce_buf_);

    D3D11_UNORDERED_ACCESS_VIEW_DESC uav_desc{};
    uav_desc.Format = DXGI_FORMAT_UNKNOWN;
    uav_desc.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    uav_desc.Buffer.FirstElement = 0;
    uav_desc.Buffer.NumElements = 64;
    d3d11_device_->CreateUnorderedAccessView(
        shooting_reduce_buf_, &uav_desc, &shooting_reduce_uav_);

    D3D11_BUFFER_DESC staging_desc = red_desc;
    staging_desc.Usage = D3D11_USAGE_STAGING;
    staging_desc.BindFlags = 0;
    staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    d3d11_device_->CreateBuffer(&staging_desc, nullptr, &shooting_reduce_staging_);
  }

  if (shooting_cb_ == nullptr || shooting_celestial_srv_ == nullptr ||
      shooting_grid_uav_ == nullptr || shooting_eval_uav_ == nullptr ||
      shooting_reduce_uav_ == nullptr || shooting_reduce_staging_ == nullptr) {
    return false;
  }

  // Upload data
  OptimizerConfigGpu cfg{};
  cfg.base_burn_times[0] = context.initial_time;
  cfg.base_burn_times[1] = context.span_delta_t > 0.0f ? context.span_delta_t : 60.0f;
  cfg.base_burn_times[2] = 1000.0f;
  cfg.base_burn_times[3] = 10000.0f;

  cfg.base_delta_v[0] = context.base_delta_v[0];
  cfg.base_delta_v[1] = context.base_delta_v[1];
  cfg.base_delta_v[2] = context.base_delta_v[2];
  cfg.base_delta_v[3] = 300.0f;

  cfg.span_delta_v[0] = context.span_delta_v[0] > 0.0f ? context.span_delta_v[0] : 100.0f;
  cfg.span_delta_v[1] = context.span_delta_v[1] > 0.0f ? context.span_delta_v[1] : 100.0f;
  cfg.span_delta_v[2] = context.span_delta_v[2] > 0.0f ? context.span_delta_v[2] : 100.0f;
  cfg.span_delta_v[3] = context.target_periapsis_altitude;

  cfg.optimization_weights[0] = 1.0f;
  cfg.optimization_weights[1] = 0.05f;
  cfg.optimization_weights[2] = 0.01f;
  cfg.optimization_weights[3] = 0.0f;

  cfg.initial_vessel_pos[0] = context.initial_pos[0];
  cfg.initial_vessel_pos[1] = context.initial_pos[1];
  cfg.initial_vessel_pos[2] = context.initial_pos[2];
  cfg.initial_vessel_pos[3] = context.initial_time;

  cfg.initial_vessel_vel[0] = context.initial_vel[0];
  cfg.initial_vessel_vel[1] = context.initial_vel[1];
  cfg.initial_vessel_vel[2] = context.initial_vel[2];
  cfg.initial_vessel_vel[3] = context.step_size_seconds > 0.0f ? context.step_size_seconds : 10.0f;

  cfg.grid_dimensions[0] = 16.0f;
  cfg.grid_dimensions[1] = 16.0f;
  cfg.grid_dimensions[2] = 8.0f;
  cfg.grid_dimensions[3] = 8.0f;

  cfg.celestial_count = celestial_count;
  cfg.step_count = context.step_count > 0 ? context.step_count : 256;
  cfg.target_celestial_index = context.target_celestial_index;
  cfg.reduction_item_count = kTotalCandidates;

  d3d11_context_->UpdateSubresource(shooting_cb_, 0, nullptr, &cfg, 0, 0);
  d3d11_context_->UpdateSubresource(
      shooting_celestial_buf_, 0, nullptr, context.celestials, 0, 0);

  // Kernel 1: GenerateCandidateGrid
  d3d11_context_->CSSetShader(shooting_grid_shader_, nullptr, 0);
  d3d11_context_->CSSetConstantBuffers(0, 1, &shooting_cb_);
  d3d11_context_->CSSetUnorderedAccessViews(0, 1, &shooting_grid_uav_, nullptr);
  d3d11_context_->Dispatch(64, 1, 1);

  ID3D11UnorderedAccessView* null_uavs[3] = {nullptr, nullptr, nullptr};
  d3d11_context_->CSSetUnorderedAccessViews(0, 1, null_uavs, nullptr);

  // Kernel 2: EvaluateTrajectoryBundle
  d3d11_context_->CSSetShader(shooting_eval_shader_, nullptr, 0);
  d3d11_context_->CSSetShaderResources(0, 1, &shooting_celestial_srv_);
  d3d11_context_->CSSetUnorderedAccessViews(0, 1, &shooting_grid_uav_, nullptr);
  d3d11_context_->CSSetUnorderedAccessViews(1, 1, &shooting_eval_uav_, nullptr);
  d3d11_context_->Dispatch(64, 1, 1);

  d3d11_context_->CSSetUnorderedAccessViews(0, 2, null_uavs, nullptr);
  ID3D11ShaderResourceView* null_srv = nullptr;
  d3d11_context_->CSSetShaderResources(0, 1, &null_srv);

  // Kernel 3: ParallelReduceOptimalBurn
  d3d11_context_->CSSetShader(shooting_reduce_shader_, nullptr, 0);
  d3d11_context_->CSSetUnorderedAccessViews(1, 1, &shooting_eval_uav_, nullptr);
  d3d11_context_->CSSetUnorderedAccessViews(2, 1, &shooting_reduce_uav_, nullptr);
  d3d11_context_->Dispatch(64, 1, 1);

  d3d11_context_->CSSetUnorderedAccessViews(1, 2, null_uavs, nullptr);

  // Kernel 4: ParallelReduceOptimalBurnFinal
  d3d11_context_->CSSetShader(shooting_reduce_final_shader_, nullptr, 0);
  d3d11_context_->CSSetUnorderedAccessViews(2, 1, &shooting_reduce_uav_, nullptr);
  d3d11_context_->Dispatch(1, 1, 1);

  d3d11_context_->CSSetUnorderedAccessViews(2, 1, null_uavs, nullptr);

  // Readback
  d3d11_context_->CopyResource(shooting_reduce_staging_, shooting_reduce_buf_);
  D3D11_MAPPED_SUBRESOURCE mapped{};
  HRESULT const map_hr = d3d11_context_->Map(
      shooting_reduce_staging_, 0, D3D11_MAP_READ, 0, &mapped);
  if (SUCCEEDED(map_hr) && mapped.pData != nullptr) {
    auto const* results = static_cast<OptimalCandidateBurn const*>(mapped.pData);
    out_optimal = results[0];
    d3d11_context_->Unmap(shooting_reduce_staging_, 0);
    return out_optimal.cost_metric < 1e30f;
  }
#endif
  return false;
}

bool ComputePipeline::OptimizeFlightPlanShootingCpu(
    ShootingContextGpu const& context,
    OptimalCandidateBurn& out_optimal) {
  if (context.celestial_count == 0) return false;

  constexpr std::uint32_t dim_t = 8;
  constexpr std::uint32_t dim_vt = 8;
  constexpr std::uint32_t dim_vn = 4;
  constexpr std::uint32_t dim_vb = 4;

  float best_cost = 1e38f;
  OptimalCandidateBurn best_burn{};

  float const span_t = context.span_delta_t > 0.0f ? context.span_delta_t : 60.0f;
  float const span_vt = context.span_delta_v[0] > 0.0f ? context.span_delta_v[0] : 100.0f;
  float const span_vn = context.span_delta_v[1] > 0.0f ? context.span_delta_v[1] : 100.0f;
  float const span_vb = context.span_delta_v[2] > 0.0f ? context.span_delta_v[2] : 100.0f;

  float const base_vt = context.base_delta_v[0];
  float const base_vn = context.base_delta_v[1];
  float const base_vb = context.base_delta_v[2];

  float const dt = context.step_size_seconds > 0.0f ? context.step_size_seconds : 10.0f;
  std::uint32_t const step_count = context.step_count > 0 ? context.step_count : 128;
  std::uint32_t const target_idx = context.target_celestial_index;

  for (std::uint32_t it = 0; it < dim_t; ++it) {
    float const norm_t = dim_t > 1 ? (static_cast<float>(it) / (dim_t - 1)) * 2.0f - 1.0f : 0.0f;
    float const delta_t = norm_t * span_t;

    for (std::uint32_t ivt = 0; ivt < dim_vt; ++ivt) {
      float const norm_vt = dim_vt > 1 ? (static_cast<float>(ivt) / (dim_vt - 1)) * 2.0f - 1.0f : 0.0f;
      float const dv_t = base_vt + norm_vt * span_vt;

      for (std::uint32_t ivn = 0; ivn < dim_vn; ++ivn) {
        float const norm_vn = dim_vn > 1 ? (static_cast<float>(ivn) / (dim_vn - 1)) * 2.0f - 1.0f : 0.0f;
        float const dv_n = base_vn + norm_vn * span_vn;

        for (std::uint32_t ivb = 0; ivb < dim_vb; ++ivb) {
          float const norm_vb = dim_vb > 1 ? (static_cast<float>(ivb) / (dim_vb - 1)) * 2.0f - 1.0f : 0.0f;
          float const dv_b = base_vb + norm_vb * span_vb;

          // Propagate forward
          float pos[3] = {context.initial_pos[0], context.initial_pos[1], context.initial_pos[2]};
          float vel[3] = {context.initial_vel[0] + dv_t, context.initial_vel[1] + dv_n, context.initial_vel[2] + dv_b};

          float min_dist_sqr = 1e38f;
          float speed_at_min = 0.0f;

          for (std::uint32_t s = 0; s < step_count; ++s) {
            float const current_t = s * dt;
            float ax = 0.0f, ay = 0.0f, az = 0.0f;
            for (std::uint32_t b = 0; b < context.celestial_count; ++b) {
              auto const& body = context.celestials[b];
              float const bx = body.position[0] + body.velocity[0] * current_t;
              float const by = body.position[1] + body.velocity[1] * current_t;
              float const bz = body.position[2] + body.velocity[2] * current_t;

              float const rx = bx - pos[0];
              float const ry = by - pos[1];
              float const rz = bz - pos[2];
              float const r2 = rx * rx + ry * ry + rz * rz;
              float const r = std::sqrt(r2);
              if (r > 1.0f) {
                float const factor = body.gravitational_parameter / (r2 * r);
                ax += rx * factor;
                ay += ry * factor;
                az += rz * factor;
              }

              if (b == target_idx && r2 < min_dist_sqr) {
                min_dist_sqr = r2;
                float const rel_vx = vel[0] - body.velocity[0];
                float const rel_vy = vel[1] - body.velocity[1];
                float const rel_vz = vel[2] - body.velocity[2];
                speed_at_min = std::sqrt(rel_vx * rel_vx + rel_vy * rel_vy + rel_vz * rel_vz);
              }
            }
            vel[0] += ax * dt;
            vel[1] += ay * dt;
            vel[2] += az * dt;
            pos[0] += vel[0] * dt;
            pos[1] += vel[1] * dt;
            pos[2] += vel[2] * dt;
          }

          float const min_dist = std::sqrt(min_dist_sqr);
          float const dv_mag = std::sqrt(dv_t * dv_t + dv_n * dv_n + dv_b * dv_b);
          float const cost = std::abs(min_dist - context.target_periapsis_altitude) +
                             0.05f * speed_at_min + 0.01f * dv_mag;

          if (cost < best_cost) {
            best_cost = cost;
            best_burn.candidate_index = it * 64 + ivt * 16 + ivn * 4 + ivb;
            best_burn.periapsis_distance = min_dist;
            best_burn.target_relative_speed = speed_at_min;
            best_burn.cost_metric = cost;
            best_burn.delta_t = delta_t;
            best_burn.delta_v_tangent = dv_t;
            best_burn.delta_v_normal = dv_n;
            best_burn.delta_v_binormal = dv_b;
          }
        }
      }
    }
  }

  if (best_cost < 1e30f) {
    out_optimal = best_burn;
    return true;
  }
  return false;
}

}  // namespace internal
}  // namespace _compute_pipeline
}  // namespace ksp_plugin
}  // namespace principia
