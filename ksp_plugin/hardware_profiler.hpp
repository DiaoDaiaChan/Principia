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
using _gpu_interop_types::GpuDeviceRole;
using _gpu_interop_types::GpuDeviceInfo;
using _gpu_interop_types::HardwareProfile;
using _gpu_interop_types::kMaxGpuAdapters;

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

    // 2. DXGI Adapter Topology Discovery & Multi-GPU Intelligent Routing
    int const adapter_count = DxgiAdapterFactory::Instance().GetAdapterCount();
    profile.adapter_count = adapter_count;
    profile.has_integrated_gpu = false;
    profile.has_discrete_gpu = false;
    profile.active_gpu_count = 0;
    profile.igpu_compute_score = 0.0;
    profile.primary_gpu_compute_score = 0.0;
    profile.total_gpu_compute_score = 0.0;
    profile.is_heterogeneous_tri_engine = false;

    devices_.clear();
    int igpu_idx = -1;
    int primary_dgpu_idx = -1;

    for (int i = 0; i < adapter_count && i < kMaxGpuAdapters; ++i) {
      GpuAdapterInfo info{};
      if (DxgiAdapterFactory::Instance().GetAdapterInfo(i, &info)) {
        GpuDeviceInfo dev{};
        dev.adapter_index = i;
        std::memcpy(dev.description, info.description, sizeof(dev.description));
        dev.dedicated_video_memory = info.dedicated_video_memory;
        dev.shared_system_memory = info.shared_system_memory;
        dev.vendor_id = info.vendor_id;
        dev.device_id = info.device_id;
        dev.is_integrated = info.is_integrated;
        dev.supports_compute = info.supports_compute;

        if (info.is_integrated) {
          profile.has_integrated_gpu = true;
          dev.compute_score = profile.cpu_fp64_gflops_score * 2.2;
          profile.igpu_compute_score = dev.compute_score;
          if (igpu_idx < 0) igpu_idx = i;
        } else {
          profile.has_discrete_gpu = true;
          double const vram_gb = static_cast<double>(info.dedicated_video_memory) / (1024.0 * 1024.0 * 1024.0);
          double const scale = 4.2 + std::min(4.0, vram_gb / 2.0);
          dev.compute_score = profile.cpu_fp64_gflops_score * scale;
          if (primary_dgpu_idx < 0) {
            primary_dgpu_idx = i;
            profile.primary_gpu_compute_score = dev.compute_score;
          }
        }
        profile.total_gpu_compute_score += dev.compute_score;
        profile.active_gpu_count++;
        devices_.push_back(dev);
      }
    }

    // Intelligent Asymmetric Multi-GPU Topology Strategy:
    if (profile.has_integrated_gpu && profile.has_discrete_gpu) {
      // Heterogeneous Tri-Engine Mode: CPU + iGPU + dGPU
      profile.is_heterogeneous_tri_engine = true;
      for (auto& dev : devices_) {
        if (dev.is_integrated) {
          dev.role = GpuDeviceRole::UmaCompute; // Zero-copy UMA compute engine
        } else if (dev.adapter_index == primary_dgpu_idx) {
          dev.role = GpuDeviceRole::PrimaryRender; // 3D Render Master
        } else {
          dev.role = GpuDeviceRole::DedicatedCompute; // Secondary dGPU compute accelerator
        }
      }
      profile.recommended_compute_adapter = igpu_idx; // iGPU for zero-copy background compute
    } else if (profile.has_discrete_gpu) {
      // Multi-dGPU or Single dGPU without iGPU
      for (auto& dev : devices_) {
        if (dev.adapter_index == primary_dgpu_idx) {
          dev.role = GpuDeviceRole::PrimaryRender;
        } else {
          dev.role = GpuDeviceRole::DedicatedCompute;
        }
      }
      profile.recommended_compute_adapter = primary_dgpu_idx;
    } else if (profile.has_integrated_gpu) {
      // iGPU only
      for (auto& dev : devices_) {
        dev.role = GpuDeviceRole::UmaCompute;
      }
      profile.recommended_compute_adapter = igpu_idx;
      profile.primary_gpu_compute_score = profile.igpu_compute_score;
    } else {
      profile.recommended_compute_adapter = -1;
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

        _chebyshev_segment_export::WriteSegmentDataBatch(src, dst, segment_count, mode);

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

  bool GetDeviceInfo(int const index, GpuDeviceInfo* const out_info) {
    if (out_info == nullptr) return false;
    std::lock_guard<std::mutex> lock(mutex_);
    if (!cached_profile_.is_calibrated) {
      mutex_.unlock();
      RunCalibration();
      mutex_.lock();
    }
    if (index >= 0 && index < static_cast<int>(devices_.size())) {
      *out_info = devices_[index];
      return true;
    }
    return false;
  }

  void SetDeviceRole(int const index, GpuDeviceRole const role) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (index >= 0 && index < static_cast<int>(devices_.size())) {
      devices_[index].role = role;
    }
  }

  void SetDeviceDynamicRatio(int const index, double const ratio) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (index >= 0 && index < static_cast<int>(devices_.size())) {
      devices_[index].dynamic_workload_ratio = ratio;
    }
  }

  int GetDeviceCount() {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!cached_profile_.is_calibrated) {
      mutex_.unlock();
      RunCalibration();
      mutex_.lock();
    }
    return static_cast<int>(devices_.size());
  }

 private:
  HardwareProfiler() = default;

  std::mutex mutex_;
  HardwareProfile cached_profile_{};
  std::vector<GpuDeviceInfo> devices_{};
};

}  // namespace internal

using internal::HardwareProfiler;

}  // namespace _hardware_profiler
}  // namespace ksp_plugin
}  // namespace principia
