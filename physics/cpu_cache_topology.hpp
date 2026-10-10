#pragma once

#include <algorithm>
#include <cstdint>
#include <vector>

#if defined(_WIN32)
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace principia {
namespace physics {
namespace _cpu_cache_topology {
namespace internal {

// Hardware cache topology detected from the host operating system.
struct CpuCacheTopology {
  std::size_t l1d_size_bytes{48 * 1024};       // Default 48 KB
  std::size_t l1i_size_bytes{32 * 1024};       // Default 32 KB
  std::size_t l2_per_core_bytes{2048 * 1024};  // Default 2 MB per core
  std::size_t l3_shared_bytes{36 * 1024 * 1024}; // Default 36 MB shared
  std::size_t cache_line_size{64};             // Default 64 bytes
  bool detected{false};

  // Safe Quota Calculation:
  // L1d: <= 5% (to never evict hot registers/stack from L1)
  // L2:  <= 25% of core-private L2 (leaving 75% for thread stack & game code)
  // L3:  <= 15% of shared L3 (leaving 85% for graphics driver, OS, Unity engine)
  std::size_t SafeL1dBytes() const {
    return std::min<std::size_t>(l1d_size_bytes / 20, 4 * 1024);
  }

  std::size_t SafeL2Bytes() const {
    std::size_t const target = l2_per_core_bytes / 4;  // 25%
    return std::clamp<std::size_t>(target, 128 * 1024, 1024 * 1024); // 128 KB - 1024 KB
  }

  std::size_t SafeL3Bytes() const {
    std::size_t const target = (l3_shared_bytes * 15) / 100; // 15%
    return std::clamp<std::size_t>(target, 2 * 1024 * 1024, 16 * 1024 * 1024); // 2 MB - 16 MB
  }

  // Capacity in 256-byte tiles
  std::size_t L2TileCapacity() const {
    return SafeL2Bytes() / 256;
  }

  // Capacity in segments per body (for 32 bodies)
  std::size_t L3SegmentsPerBody() const {
    return SafeL3Bytes() / (32 * 256);
  }
};

inline CpuCacheTopology DetectCpuCacheTopology() {
  CpuCacheTopology topo;
#if defined(_WIN32)
  DWORD length = 0;
  if (!GetLogicalProcessorInformationEx(RelationCache, nullptr, &length) &&
      GetLastError() == ERROR_INSUFFICIENT_BUFFER) {
    std::vector<char> buffer(length);
    if (GetLogicalProcessorInformationEx(
            RelationCache,
            reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data()),
            &length)) {
      char const* ptr = buffer.data();
      char const* end = ptr + length;
      while (ptr < end) {
        auto const* info =
            reinterpret_cast<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX const*>(ptr);
        if (info->Relationship == RelationCache) {
          auto const& cache = info->Cache;
          if (cache.LineSize > 0) {
            topo.cache_line_size = cache.LineSize;
          }
          if (cache.Level == 1) {
            if (cache.Type == CacheData || cache.Type == CacheUnified) {
              topo.l1d_size_bytes = cache.CacheSize;
            } else if (cache.Type == CacheInstruction) {
              topo.l1i_size_bytes = cache.CacheSize;
            }
          } else if (cache.Level == 2) {
            topo.l2_per_core_bytes = cache.CacheSize;
          } else if (cache.Level == 3) {
            topo.l3_shared_bytes = cache.CacheSize;
          }
        }
        ptr += info->Size;
      }
      topo.detected = true;
    }
  }
#endif
  return topo;
}

}  // namespace internal

using internal::CpuCacheTopology;
using internal::DetectCpuCacheTopology;

}  // namespace _cpu_cache_topology
}  // namespace physics
}  // namespace principia
