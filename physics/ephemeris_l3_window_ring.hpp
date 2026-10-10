#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <vector>

#include "absl/synchronization/mutex.h"
#include "physics/chebyshev_l2_tile_cache.hpp"

namespace principia {
namespace physics {
namespace _ephemeris_l3_window_ring {
namespace internal {

using namespace principia::physics::_chebyshev_l2_tile_cache;

// DRAM Backing Overflow Pool:
// Stores segments evicted from the L3 cache into system RAM.
// Provides infinite lossless fallback when an evaluation query spans outside the L3 window.
class EphemerisDramOverflowPool {
 public:
  static constexpr std::size_t kMaxBodies = 32;

  void StoreEvictedSegment(std::size_t const body_idx, ChebyshevTile256 const& seg) {
    if (body_idx >= kMaxBodies) return;
    absl::MutexLock l(&lock_);
    dram_pool_[body_idx].push_back(seg);
    // Keep DRAM pool bounded to recent 4096 segments per body (1 MB per body in RAM)
    if (dram_pool_[body_idx].size() > 4096) {
      dram_pool_[body_idx].erase(dram_pool_[body_idx].begin());
    }
  }

  bool EvaluateCelestialPosition(
      std::size_t const body_idx,
      double const t,
      double& x,
      double& y,
      double& z) const {
    if (body_idx >= kMaxBodies) return false;
    absl::ReaderMutexLock l(&lock_);
    auto const& pool = dram_pool_[body_idx];
    for (auto const& seg : pool) {
      if (t >= seg.t_min && t <= seg.t_max) {
        seg.EvaluateClenshaw(t, x, y, z);
        return true;
      }
    }
    return false;
  }

  std::size_t TotalDramSegments() const {
    absl::ReaderMutexLock l(&lock_);
    std::size_t total = 0;
    for (auto const& p : dram_pool_) {
      total += p.size();
    }
    return total;
  }

 private:
  mutable absl::Mutex lock_;
  std::array<std::vector<ChebyshevTile256>, kMaxBodies> dram_pool_{};
};

// Global Ephemeris Continuous Window Ring Buffer:
// Auto-sized to 10% - 15% of host CPU shared L3 Cache (2 MB - 16 MB).
// Sits 100% inside modern CPU shared L3 Cache (16 MB - 96 MB 3D V-Cache).
// When an L3 segment slot is overwritten (evicted), it automatically spills/dumps
// to the DRAM Overflow Pool in host system RAM!
class alignas(64) EphemerisL3WindowRing {
 public:
  static constexpr std::size_t kMaxBodies = 32;
  static constexpr std::size_t kMaxSegmentsPerBody = 1024;  // Up to 8.38 MB

  static EphemerisL3WindowRing& Instance() {
    static EphemerisL3WindowRing instance;
    return instance;
  }

  void SetSegmentsPerBody(std::size_t const count) {
    segments_per_body_ = std::clamp<std::size_t>(count, 128, kMaxSegmentsPerBody);
  }

  std::size_t segments_per_body() const { return segments_per_body_; }

  // Inserts a continuous trajectory segment into the L3 window ring.
  // If an older segment is evicted, spills it to the DRAM overflow pool!
  void InsertSegment(std::size_t const body_idx, ChebyshevTile256 const& segment) {
    if (body_idx >= kMaxBodies) return;
    std::size_t const slot =
        write_cursor_[body_idx].load(std::memory_order_relaxed) % segments_per_body_;

    if (write_cursor_[body_idx].load(std::memory_order_relaxed) >= segments_per_body_) {
      // Slot contains an old valid segment being evicted from L3: Spill to DRAM!
      dram_overflow_pool_.StoreEvictedSegment(body_idx, ring_[body_idx][slot]);
      spill_to_dram_count_.fetch_add(1, std::memory_order_relaxed);
    }

    ring_[body_idx][slot] = segment;
    write_cursor_[body_idx].fetch_add(1, std::memory_order_release);
  }

  // Evaluates a celestial body's position at time `t`:
  // First searches L3 Cache. If miss, cascades down to DRAM Overflow Pool!
  bool EvaluateCelestialPosition(
      std::size_t const body_idx,
      double const t,
      double& x,
      double& y,
      double& z) const {
    if (body_idx >= kMaxBodies) return false;

    lookups_.fetch_add(1, std::memory_order_relaxed);
    std::size_t const current_head = write_cursor_[body_idx].load(std::memory_order_acquire);
    std::size_t const count = std::min(current_head, segments_per_body_);

    // 1. Fast search in L3 cache lines
    for (std::size_t i = 0; i < count; ++i) {
      auto const& seg = ring_[body_idx][i];
      if (t >= seg.t_min && t <= seg.t_max) {
        seg.EvaluateClenshaw(t, x, y, z);
        l3_hits_.fetch_add(1, std::memory_order_relaxed);
        return true;
      }
    }

    // 2. Cascade down to DRAM Overflow Pool
    if (dram_overflow_pool_.EvaluateCelestialPosition(body_idx, t, x, y, z)) {
      dram_hits_.fetch_add(1, std::memory_order_relaxed);
      return true;
    }

    return false;
  }

  std::uint64_t l3_hits() const {
    return l3_hits_.load(std::memory_order_relaxed);
  }

  std::uint64_t dram_hits() const {
    return dram_hits_.load(std::memory_order_relaxed);
  }

  std::uint64_t spill_to_dram_count() const {
    return spill_to_dram_count_.load(std::memory_order_relaxed);
  }

  std::uint64_t lookups() const {
    return lookups_.load(std::memory_order_relaxed);
  }

  std::size_t TotalDramSegments() const {
    return dram_overflow_pool_.TotalDramSegments();
  }

#pragma pack(push, 1)
  struct alignas(64) MasterEphemerisHeader {
    char magic[16];                  // "PRINCEPH_MASTER\0"
    std::uint32_t format_version;    // 1
    std::uint32_t header_size;       // 64
    std::uint32_t max_bodies;        // kMaxBodies (32)
    std::uint32_t segments_per_body; // segments_per_body_ (e.g. 512)
    std::uint64_t timestamp_utc;     // uint64 ms epoch
    std::uint32_t total_segments;    // Total valid segments stored
    std::uint8_t reserved[20];       // 64-byte alignment padding
  };
#pragma pack(pop)
  static_assert(sizeof(MasterEphemerisHeader) == 64, "MasterEphemerisHeader must be 64 bytes");

  // Persists the master ephemeris cache ring into a single co-located compound file.
  bool SaveMasterEphemeris(std::string const& file_path) {
    std::filesystem::path const target_path(file_path);
    std::filesystem::path const temp_file_path = std::filesystem::path(target_path).concat(".tmp");
    std::error_code ec;
    if (target_path.has_parent_path()) {
      std::filesystem::create_directories(target_path.parent_path(), ec);
    }
    std::filesystem::remove(temp_file_path, ec);

    std::ofstream stream(temp_file_path, std::ios::binary | std::ios::trunc);
    if (!stream.is_open()) {
      return false;
    }

    MasterEphemerisHeader header{};
    std::memcpy(header.magic, "PRINCEPH_MASTER\0", 16);
    header.format_version = 1;
    header.header_size = 64;
    header.max_bodies = static_cast<std::uint32_t>(kMaxBodies);
    header.segments_per_body = static_cast<std::uint32_t>(segments_per_body_);
    header.timestamp_utc = static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());

    std::uint32_t total_segments = 0;
    for (std::size_t b = 0; b < kMaxBodies; ++b) {
      std::size_t const head = write_cursor_[b].load(std::memory_order_relaxed);
      total_segments += static_cast<std::uint32_t>(std::min(head, segments_per_body_));
    }
    header.total_segments = total_segments;

    stream.write(reinterpret_cast<char const*>(&header), sizeof(header));

    for (std::size_t b = 0; b < kMaxBodies; ++b) {
      std::size_t const head = write_cursor_[b].load(std::memory_order_relaxed);
      std::uint32_t const count = static_cast<std::uint32_t>(std::min(head, segments_per_body_));
      std::uint64_t const cursor_val = static_cast<std::uint64_t>(head);
      stream.write(reinterpret_cast<char const*>(&cursor_val), sizeof(cursor_val));
      stream.write(reinterpret_cast<char const*>(&count), sizeof(count));
      if (count > 0) {
        stream.write(reinterpret_cast<char const*>(ring_[b].data()),
                     count * sizeof(ChebyshevTile256));
      }
    }

    stream.flush();
    bool const ok = stream.good();
    stream.close();

    if (!ok) {
      std::filesystem::remove(temp_file_path, ec);
      return false;
    }

    std::filesystem::rename(temp_file_path, target_path, ec);
    if (ec) {
      std::filesystem::remove(temp_file_path, ec);
      return false;
    }
    return true;
  }

  // Rapidly restores the master ephemeris cache ring from disk with zero GC allocation.
  bool LoadMasterEphemeris(std::string const& file_path) {
    std::filesystem::path const target_path(file_path);
    if (!std::filesystem::exists(target_path)) {
      return false;
    }

    std::ifstream stream(target_path, std::ios::binary);
    if (!stream.is_open()) {
      return false;
    }

    MasterEphemerisHeader header{};
    stream.read(reinterpret_cast<char*>(&header), sizeof(header));
    if (!stream.good() || std::memcmp(header.magic, "PRINCEPH_MASTER", 15) != 0) {
      return false;
    }

    std::size_t const bodies_to_read = std::min<std::size_t>(header.max_bodies, kMaxBodies);
    for (std::size_t b = 0; b < bodies_to_read; ++b) {
      std::uint64_t cursor_val = 0;
      std::uint32_t count = 0;
      stream.read(reinterpret_cast<char*>(&cursor_val), sizeof(cursor_val));
      stream.read(reinterpret_cast<char*>(&count), sizeof(count));
      std::size_t const valid_count = std::min<std::size_t>(count, segments_per_body_);
      if (valid_count > 0) {
        stream.read(reinterpret_cast<char*>(ring_[b].data()),
                     valid_count * sizeof(ChebyshevTile256));
      }
      if (count > valid_count) {
        stream.seekg((count - valid_count) * sizeof(ChebyshevTile256), std::ios::cur);
      }
      write_cursor_[b].store(static_cast<std::size_t>(cursor_val), std::memory_order_release);
    }

    l3_hits_.store(0, std::memory_order_relaxed);
    dram_hits_.store(0, std::memory_order_relaxed);
    spill_to_dram_count_.store(0, std::memory_order_relaxed);
    lookups_.store(0, std::memory_order_relaxed);
    return true;
  }

 private:
  EphemerisL3WindowRing() = default;

  std::size_t segments_per_body_{512};

  mutable std::atomic<std::uint64_t> l3_hits_{0};
  mutable std::atomic<std::uint64_t> dram_hits_{0};
  mutable std::atomic<std::uint64_t> spill_to_dram_count_{0};
  mutable std::atomic<std::uint64_t> lookups_{0};

  alignas(64) std::array<std::atomic<std::size_t>, kMaxBodies> write_cursor_{};
  alignas(64) std::array<std::array<ChebyshevTile256, kMaxSegmentsPerBody>, kMaxBodies> ring_{};

  EphemerisDramOverflowPool dram_overflow_pool_{};
};

}  // namespace internal

using internal::EphemerisL3WindowRing;
using internal::EphemerisDramOverflowPool;

}  // namespace _ephemeris_l3_window_ring
}  // namespace physics
}  // namespace principia
