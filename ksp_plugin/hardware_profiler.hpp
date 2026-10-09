#pragma once

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <mutex>
#include <vector>

#include "ksp_plugin/dxgi_adapter_factory.hpp"
#include "ksp_plugin/gpu_interop_types.hpp"
#include "ksp_plugin/chebyshev_segment_export.hpp"

namespace principia {
namespace ksp_plugin {
namespace _hardware_profiler {
namespace internal {

using _dxgi_adapter_factory::DxgiAdapterFactory;
using _gpu_interop_types::ChebyshevSegmentData;
using _gpu_interop_types::DmaTransferMode;
using _gpu_interop_types::GpuAdapterInfo;
using _gpu_interop_types::HardwareProfile;

class HardwareProfiler final {
 public:
  static HardwareProfiler& Instance() {
    static HardwareProfiler instance;
    return instance;
  }

  // Executes rapid multi-device benchmark and builds the hardware calibration profile.
  // Benchmarks CPU FP64 vector arithmetic and PCIe/UMA DMA throughput (~1ms duration).
  HardwareProfile RunCalibration() {
    std::lock_guard<std::mutex> lock(mutex_);

    HardwareProfile profile{};

    // 1. CPU FP64 Symplectic Numerical Advance Micro-Benchmark
    // Executes 64,000 double precision symplectic gravitation steps.
    {
      double x = 1.0, y = 0.0, vx = 0.0, vy = 1.0;
      double const dt = 0.001;
      auto const start_cpu = std::chrono::high_resolution_clock::now();

      for (int i = 0; i < 64000; ++i) {
        double const r2 = x * x + y * y + 1e-6;
        double const r = std::sqrt(r2);
        double const r3 = r2 * r;
        double const ax = -x / r3;
        double const ay = -y / r3;
        vx += ax * dt;
        vy += ay * dt;
        x += vx * dt;
        y += vy * dt;
      }

      auto const end_cpu = std::chrono::high_resolution_clock::now();
      std::chrono::duration<double> const elapsed_cpu = end_cpu - start_cpu;
      double const cpu_duration_s = elapsed_cpu.count();
      // CPU score in thousand steps per second (k steps/s)
      profile.cpu_fp64_gflops_score = cpu_duration_s > 0.0
          ? (64.0 / cpu_duration_s)
          : 500.0;
    }

    // 2. DXGI Adapter Topology Discovery & Multi-GPU Routing
    int const adapter_count = DxgiAdapterFactory::Instance().GetAdapterCount();
    profile.adapter_count = adapter_count;
    profile.has_integrated_gpu = false;
    profile.has_discrete_gpu = false;

    int igpu_idx = -1;
    int dgpu_idx = -1;

    for (int i = 0; i < adapter_count; ++i) {
      GpuAdapterInfo info{};
      if (DxgiAdapterFactory::Instance().GetAdapterInfo(i, &info)) {
        if (info.is_integrated) {
          profile.has_integrated_gpu = true;
          if (igpu_idx < 0) igpu_idx = i;
        } else {
          profile.has_discrete_gpu = true;
          if (dgpu_idx < 0) dgpu_idx = i;
        }
      }
    }

    // Intelligent Asymmetric Multi-GPU Strategy:
    // If system possesses both dedicated GPU (dGPU) and integrated GPU (iGPU):
    // Bind compute workloads to iGPU so dGPU is 100% dedicated to 3D rendering.
    if (profile.has_integrated_gpu && profile.has_discrete_gpu) {
      profile.recommended_compute_adapter = igpu_idx;
    } else if (dgpu_idx >= 0) {
      profile.recommended_compute_adapter = dgpu_idx;
    } else {
      profile.recommended_compute_adapter = -1; // Auto / System Default
    }

    // 3. PCIe / UMA DMA Bandwidth & Latency Benchmark
    {
      int const segment_count = 2048; // 512 KB payload
      std::size_t const total_bytes = static_cast<std::size_t>(segment_count) * sizeof(ChebyshevSegmentData);

#if OS_WIN
      void* src_raw = _aligned_malloc(total_bytes, 64);
      void* dst_raw = _aligned_malloc(total_bytes, 64);
#else
      void* src_raw = std::aligned_alloc(64, total_bytes);
      void* dst_raw = std::aligned_alloc(64, total_bytes);
#endif

      if (src_raw != nullptr && dst_raw != nullptr) {
        auto* src = static_cast<ChebyshevSegmentData*>(src_raw);
        auto* dst = static_cast<ChebyshevSegmentData*>(dst_raw);

        for (int i = 0; i < segment_count; ++i) {
          src[i].t_min = 1.0f * i;
          src[i].t_max = 1.0f * (i + 1);
          src[i].coeffs[0][0] = static_cast<float>(i);
        }

        auto const mode = DxgiAdapterFactory::Instance().GetDmaMode();
        auto const start_dma = std::chrono::high_resolution_clock::now();

        for (int i = 0; i < segment_count; ++i) {
          _chebyshev_segment_export::WriteSegmentData(src[i], &dst[i], mode);
        }

        auto const end_dma = std::chrono::high_resolution_clock::now();
        std::chrono::duration<double> const elapsed_dma = end_dma - start_dma;
        double const dma_dur_s = elapsed_dma.count();

        profile.dma_bandwidth_gbps = dma_dur_s > 0.0
            ? ((static_cast<double>(total_bytes) / 1e9) / dma_dur_s)
            : 8.0;
      } else {
        profile.dma_bandwidth_gbps = 8.0; // Standard fallback estimate
      }

#if OS_WIN
      if (src_raw != nullptr) _aligned_free(src_raw);
      if (dst_raw != nullptr) _aligned_free(dst_raw);
#else
      if (src_raw != nullptr) std::free(src_raw);
      if (dst_raw != nullptr) std::free(dst_raw);
#endif
    }

    // 4. Primary GPU Compute Score Estimation
    if (profile.has_discrete_gpu) {
      profile.primary_gpu_compute_score = profile.cpu_fp64_gflops_score * 4.8;
    } else if (profile.has_integrated_gpu) {
      profile.primary_gpu_compute_score = profile.cpu_fp64_gflops_score * 2.2;
    } else {
      profile.primary_gpu_compute_score = profile.cpu_fp64_gflops_score * 1.5;
    }

    profile.is_calibrated = true;
    cached_profile_ = profile;

    return profile;
  }

  HardwareProfile GetProfile() {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!cached_profile_.is_calibrated) {
      mutex_.unlock();
      HardwareProfile res = RunCalibration();
      mutex_.lock();
      return res;
    }
    return cached_profile_;
  }

 private:
  HardwareProfiler() = default;

  std::mutex mutex_;
  HardwareProfile cached_profile_{};
};

}  // namespace internal

using internal::HardwareProfiler;

}  // namespace _hardware_profiler
}  // namespace ksp_plugin
}  // namespace principia
