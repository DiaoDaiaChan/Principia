#pragma once

#include <vector>
#include <string>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstring>

#include "base/macros.hpp"
#include "ksp_plugin/gpu_interop_types.hpp"

#if OS_WIN
#include <windows.h>
#include <dxgi.h>
#include <d3d11.h>
#undef interface
#endif

namespace principia {
namespace ksp_plugin {
namespace _dxgi_adapter_factory {
namespace internal {

using _gpu_interop_types::GpuAdapterInfo;
using _gpu_interop_types::DmaTransferMode;

class DxgiAdapterFactory final {
 public:
  static DxgiAdapterFactory& Instance() {
    static DxgiAdapterFactory instance;
    return instance;
  }

  // Refreshes the list of available GPU adapters.
  void RefreshAdapters() {
    std::lock_guard<std::mutex> lock(mutex_);
    RefreshAdaptersLocked();
  }

  int GetAdapterCount() {
    std::lock_guard<std::mutex> lock(mutex_);
    if (adapters_.empty()) {
      RefreshAdaptersLocked();
    }
    return static_cast<int>(adapters_.size());
  }

  bool GetAdapterInfo(int const index, GpuAdapterInfo* const out_info) {
    if (out_info == nullptr) return false;
    std::lock_guard<std::mutex> lock(mutex_);
    if (adapters_.empty()) {
      RefreshAdaptersLocked();
    }
    if (index >= 0 && index < static_cast<int>(adapters_.size())) {
      *out_info = adapters_[index];
      return true;
    }
    return false;
  }

  void SetSelectedAdapterIndex(int const index) {
    selected_adapter_index_.store(index, std::memory_order_relaxed);
  }

  int GetSelectedAdapterIndex() const {
    return selected_adapter_index_.load(std::memory_order_relaxed);
  }

  void SetDmaMode(DmaTransferMode const mode) {
    dma_mode_.store(mode, std::memory_order_relaxed);
  }

  DmaTransferMode GetDmaMode() const {
    return dma_mode_.load(std::memory_order_relaxed);
  }

  // Telemetry counters for DMA performance probe.
  void RecordDmaWrite(std::size_t const bytes, double const duration_seconds) {
    total_dma_bytes_.fetch_add(bytes, std::memory_order_relaxed);
    last_dma_write_seconds_.store(duration_seconds, std::memory_order_relaxed);
  }

  double GetLastDmaWriteSeconds() const {
    return last_dma_write_seconds_.load(std::memory_order_relaxed);
  }

  std::uint64_t GetTotalDmaBytes() const {
    return total_dma_bytes_.load(std::memory_order_relaxed);
  }

#if OS_WIN
  bool GetSelectedDxgiAdapter(IDXGIAdapter1** const out_adapter) {
    if (out_adapter == nullptr) return false;
    *out_adapter = nullptr;

    HMODULE dxgi_module = LoadLibraryW(L"dxgi.dll");
    if (dxgi_module == nullptr) return false;

    using PFN_CreateDXGIFactory1 = HRESULT(WINAPI*)(REFIID, void**);
    auto pfn_create_factory = reinterpret_cast<PFN_CreateDXGIFactory1>(
        GetProcAddress(dxgi_module, "CreateDXGIFactory1"));
    if (pfn_create_factory == nullptr) {
      FreeLibrary(dxgi_module);
      return false;
    }

    IDXGIFactory1* factory = nullptr;
    HRESULT hr = pfn_create_factory(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory));
    if (FAILED(hr) || factory == nullptr) {
      FreeLibrary(dxgi_module);
      return false;
    }

    int selected_idx = selected_adapter_index_.load(std::memory_order_relaxed);
    if (selected_idx < 0) selected_idx = 0;

    IDXGIAdapter1* adapter = nullptr;
    for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
      if (static_cast<int>(i) == selected_idx) {
        *out_adapter = adapter;
        factory->Release();
        FreeLibrary(dxgi_module);
        return true;
      }
      adapter->Release();
      adapter = nullptr;
    }

    factory->Release();
    FreeLibrary(dxgi_module);
    return false;
  }
#endif

 private:
  DxgiAdapterFactory() {
    RefreshAdapters();
  }

  void RefreshAdaptersLocked() {
    adapters_.clear();

#if OS_WIN
    HMODULE dxgi_module = LoadLibraryW(L"dxgi.dll");
    if (dxgi_module == nullptr) {
      return;
    }

    using PFN_CreateDXGIFactory1 = HRESULT(WINAPI*)(REFIID, void**);
    auto pfn_create_factory = reinterpret_cast<PFN_CreateDXGIFactory1>(
        GetProcAddress(dxgi_module, "CreateDXGIFactory1"));
    if (pfn_create_factory == nullptr) {
      FreeLibrary(dxgi_module);
      return;
    }

    IDXGIFactory1* factory = nullptr;
    HRESULT hr = pfn_create_factory(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory));
    if (FAILED(hr) || factory == nullptr) {
      FreeLibrary(dxgi_module);
      return;
    }

    IDXGIAdapter1* adapter = nullptr;
    for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
      DXGI_ADAPTER_DESC1 desc{};
      if (SUCCEEDED(adapter->GetDesc1(&desc))) {
        // Skip software / Microsoft Basic Render Driver adapters.
        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0) {
          GpuAdapterInfo info{};
          info.adapter_index = static_cast<int>(i);

          // Convert UTF-16 Description to UTF-8
          WideCharToMultiByte(
              CP_UTF8, 0, desc.Description, -1,
              info.description, sizeof(info.description), nullptr, nullptr);

          info.dedicated_video_memory = desc.DedicatedVideoMemory;
          info.shared_system_memory = desc.SharedSystemMemory;
          info.vendor_id = desc.VendorId;
          info.device_id = desc.DeviceId;

          // Intel = 0x8086. Heuristic:
          // Intel Arc discrete cards have >= 4GB dedicated VRAM.
          // Intel UHD / Iris Xe iGPUs report 0 or <= 1024 MB dedicated VRAM.
          // AMD APUs also report <= 512 MB dedicated memory.
          info.is_integrated = (desc.DedicatedVideoMemory <= 512 * 1024 * 1024ULL) ||
                               (desc.VendorId == 0x8086 && desc.DedicatedVideoMemory <= 1024 * 1024 * 1024ULL);
          info.supports_compute = true;

          adapters_.push_back(info);
        }
      }
      adapter->Release();
      adapter = nullptr;
    }

    factory->Release();
    FreeLibrary(dxgi_module);
#endif
  }

  std::mutex mutex_;
  std::vector<GpuAdapterInfo> adapters_;
  std::atomic<int> selected_adapter_index_{-1}; // -1 = Auto
  std::atomic<DmaTransferMode> dma_mode_{DmaTransferMode::DirectDma};
  std::atomic<double> last_dma_write_seconds_{0.0};
  std::atomic<std::uint64_t> total_dma_bytes_{0};
};

}  // namespace internal

using internal::DxgiAdapterFactory;

}  // namespace _dxgi_adapter_factory
}  // namespace ksp_plugin
}  // namespace principia
