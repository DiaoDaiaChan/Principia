#pragma once

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <chrono>
#include <vector>

#include "base/macros.hpp"
#include "geometry/instant.hpp"
#include "ksp_plugin/dxgi_adapter_factory.hpp"
#include "ksp_plugin/gpu_interop_types.hpp"
#include "ksp_plugin/time_sliced_scheduler.hpp"
#include "physics/degrees_of_freedom.hpp"
#include "physics/discrete_trajectory_view.hpp"
#include "physics/trajectory_view.hpp"
#include "quantities/quantities.hpp"
#include "quantities/si.hpp"

#if OS_WIN
#include <windows.h>
#include <d3d11.h>
#undef interface
#endif

#if defined(__AVX2__) || (defined(_M_AMD64) && !defined(__clang__)) || defined(__x86_64__)
#include <immintrin.h>
#endif

namespace principia {
namespace ksp_plugin {
namespace _chebyshev_segment_export {
namespace internal {

using geometry::_instant::Instant;
using physics::_degrees_of_freedom::DegreesOfFreedom;
using physics::_discrete_trajectory_view::DiscreteTrajectoryView;
using physics::_trajectory_view::TrajectoryView;
using quantities::_quantities::Time;
using quantities::_si::Second;
using _dxgi_adapter_factory::DxgiAdapterFactory;
using _gpu_interop_types::ChebyshevSegmentData;
using _gpu_interop_types::DmaTransferMode;
using _gpu_interop_types::kMaxChebyshevDegree;
using physics::_chebyshev_l2_tile_cache::ChebyshevTile256;
using _time_sliced_scheduler::TimeSlicedScheduler;

// Fast lookup table for Chebyshev-Gauss-Lobatto transform matrix:
// M[k][j] = (2.0 / (N * c_k * c_j)) * cos(j * k * pi / N) for N = 16.
class ChebyshevLobattoTransform16 final {
 public:
  static ChebyshevLobattoTransform16 const& Instance() {
    static ChebyshevLobattoTransform16 const instance;
    return instance;
  }

  // Precalculated cosine nodes tau_j = cos(j * pi / 16) in [1.0, -1.0].
  std::array<float, kMaxChebyshevDegree + 1> tau;
  // DCT-I transform matrix of size 17x17.
  std::array<std::array<float, kMaxChebyshevDegree + 1>, kMaxChebyshevDegree + 1> matrix;

 private:
  ChebyshevLobattoTransform16() {
    constexpr double pi = 3.14159265358979323846;
    for (int j = 0; j <= kMaxChebyshevDegree; ++j) {
      tau[j] = static_cast<float>(std::cos(j * pi / kMaxChebyshevDegree));
    }
    for (int k = 0; k <= kMaxChebyshevDegree; ++k) {
      double const c_k = (k == 0 || k == kMaxChebyshevDegree) ? 2.0 : 1.0;
      for (int j = 0; j <= kMaxChebyshevDegree; ++j) {
        double const c_j = (j == 0 || j == kMaxChebyshevDegree) ? 2.0 : 1.0;
        matrix[k][j] = static_cast<float>(
            (2.0 / (kMaxChebyshevDegree * c_k * c_j)) *
            std::cos(j * k * pi / kMaxChebyshevDegree));
      }
    }
  }
};

// Stream-stores a single 256-byte Chebyshev segment without pipeline flush.
inline void StreamStoreSegment(
    ChebyshevSegmentData const& src,
    ChebyshevSegmentData* const dst) {
#if defined(__AVX__) || defined(__AVX2__)
  auto const* in = reinterpret_cast<__m256i const*>(&src);
  auto* out = reinterpret_cast<__m256i*>(dst);
  // 256 bytes per segment = 8 * 32 bytes AVX non-temporal streaming stores
  _mm256_stream_si256(&out[0], _mm256_loadu_si256(&in[0]));
  _mm256_stream_si256(&out[1], _mm256_loadu_si256(&in[1]));
  _mm256_stream_si256(&out[2], _mm256_loadu_si256(&in[2]));
  _mm256_stream_si256(&out[3], _mm256_loadu_si256(&in[3]));
  _mm256_stream_si256(&out[4], _mm256_loadu_si256(&in[4]));
  _mm256_stream_si256(&out[5], _mm256_loadu_si256(&in[5]));
  _mm256_stream_si256(&out[6], _mm256_loadu_si256(&in[6]));
  _mm256_stream_si256(&out[7], _mm256_loadu_si256(&in[7]));
#elif defined(_M_AMD64) || defined(__x86_64__)
  auto const* in = reinterpret_cast<__m128i const*>(&src);
  auto* out = reinterpret_cast<__m128i*>(dst);
  // 256 bytes per segment = 16 * 16 bytes SSE2 non-temporal streaming stores
  for (int i = 0; i < 16; ++i) {
    _mm_stream_si128(&out[i], _mm_loadu_si128(&in[i]));
  }
#else
  std::memcpy(dst, &src, sizeof(ChebyshevSegmentData));
#endif
}

inline void FlushStoreBuffers() {
#if defined(_M_AMD64) || defined(__x86_64__)
  _mm_sfence();
#endif
}

inline void WriteSegmentData(
    ChebyshevSegmentData const& src,
    ChebyshevSegmentData* const dst,
    DmaTransferMode const mode) {
  if (mode == DmaTransferMode::DirectDma) {
    StreamStoreSegment(src, dst);
    FlushStoreBuffers();
    return;
  }
  std::memcpy(dst, &src, sizeof(ChebyshevSegmentData));
}

// Ultra-fast streaming batch transfer: unrolls all segments into write-combining buffer,
// and issues a SINGLE sfence once at the end of the entire batch!
inline void WriteSegmentDataBatch(
    ChebyshevSegmentData const* const src,
    ChebyshevSegmentData* const dst,
    int const count,
    DmaTransferMode const mode) {
  if (mode == DmaTransferMode::DirectDma) {
    for (int i = 0; i < count; ++i) {
#if defined(_M_AMD64) || defined(__x86_64__)
      if (i + 2 < count) {
        _mm_prefetch(reinterpret_cast<char const*>(&src[i + 2]), _MM_HINT_T0);
      }
#endif
      StreamStoreSegment(src[i], &dst[i]);
    }
    FlushStoreBuffers();
    return;
  }
  std::memcpy(dst, src, count * sizeof(ChebyshevSegmentData));
}

// Converts a TrajectoryView into a sequence of continuous Chebyshev polynomial segments.
template<typename Frame, typename PlottingFn>
int ExportTrajectoryViewToChebyshevSegments(
    TrajectoryView<Frame> const& view,
    PlottingFn const& to_scaled_space_point,
    ChebyshevSegmentData* const buffer,
    int const max_segments,
    bool const is_d3d11_buffer) {
  if (buffer == nullptr || max_segments <= 0) {
    return 0;
  }

  Instant const t_min = view.t_min();
  Instant const t_max = view.t_max();
  if (t_max <= t_min) {
    return 0;
  }

  // Determine target destination pointer (mapped D3D11 buffer or host memory)
  ChebyshevSegmentData* dst_buffer = buffer;
#if OS_WIN
  ID3D11Buffer* d3d11_buffer = nullptr;
  ID3D11DeviceContext* context = nullptr;
  ID3D11Device* device = nullptr;
  if (is_d3d11_buffer) {
    d3d11_buffer = reinterpret_cast<ID3D11Buffer*>(buffer);
    d3d11_buffer->GetDevice(&device);
    if (device != nullptr) {
      device->GetImmediateContext(&context);
      if (context != nullptr) {
        D3D11_MAPPED_SUBRESOURCE mapped{};
        HRESULT const hr = context->Map(
            d3d11_buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        if (SUCCEEDED(hr)) {
          dst_buffer = reinterpret_cast<ChebyshevSegmentData*>(mapped.pData);
        } else {
          context->Release();
          device->Release();
          return 0;
        }
      } else {
        device->Release();
        return 0;
      }
    } else {
      return 0;
    }
  }
#else
  if (is_d3d11_buffer) {
    return 0;
  }
#endif

  auto const start_time = std::chrono::high_resolution_clock::now();
  auto const& transform = ChebyshevLobattoTransform16::Instance();
  DmaTransferMode const dma_mode = DxgiAdapterFactory::Instance().GetDmaMode();

  Time const total_duration = t_max - t_min;
  // Partition into segments of at most ~300s duration, clamped to max_segments.
  int num_segments = static_cast<int>(std::ceil((total_duration / Second) / 300.0));
  if (num_segments < 1) num_segments = 1;
  if (num_segments > max_segments) num_segments = max_segments;

  Time const segment_dt = total_duration / num_segments;

  for (int s = 0; s < num_segments; ++s) {
    Instant const seg_t_start = t_min + s * segment_dt;
    Instant const seg_t_end = (s == num_segments - 1) ? t_max : (seg_t_start + segment_dt);
    Time const seg_span = seg_t_end - seg_t_start;
    Instant const seg_mid = seg_t_start + 0.5 * seg_span;

    // Sample at 17 Gauss-Lobatto points
    float samples_x[kMaxChebyshevDegree + 1];
    float samples_y[kMaxChebyshevDegree + 1];
    float samples_z[kMaxChebyshevDegree + 1];
    for (int j = 0; j <= kMaxChebyshevDegree; ++j) {
      float const tau_j = transform.tau[j];
      Instant const t_j = seg_mid + tau_j * (0.5 * seg_span);
      auto const pt = to_scaled_space_point(t_j);
      samples_x[j] = pt.x;
      samples_y[j] = pt.y;
      samples_z[j] = pt.z;
    }

    // Compute Chebyshev expansion coefficients via matrix multiplication
    ChebyshevSegmentData seg{};
    seg.t_min = static_cast<float>((seg_t_start - Instant{}) / Second);
    seg.t_max = static_cast<float>((seg_t_end - Instant{}) / Second);
    seg.degree = kMaxChebyshevDegree;
    seg.padding = 0.0f;

    for (int k = 0; k <= kMaxChebyshevDegree; ++k) {
      float cx = 0.0f;
      float cy = 0.0f;
      float cz = 0.0f;
      for (int j = 0; j <= kMaxChebyshevDegree; ++j) {
        float const w = transform.matrix[k][j];
        cx += w * samples_x[j];
        cy += w * samples_y[j];
        cz += w * samples_z[j];
      }
      seg.coeffs[k][0] = cx;
      seg.coeffs[k][1] = cy;
      seg.coeffs[k][2] = cz;
    }

    // Write to mapped VRAM / system buffer via streaming store without per-segment fence
#if defined(_M_AMD64) || defined(__x86_64__)
    if (s + 2 < num_segments) {
      _mm_prefetch(reinterpret_cast<char const*>(&dst_buffer[s + 2]), _MM_HINT_T0);
    }
#endif
    if (dma_mode == DmaTransferMode::DirectDma) {
      StreamStoreSegment(seg, &dst_buffer[s]);
    } else {
      std::memcpy(&dst_buffer[s], &seg, sizeof(ChebyshevSegmentData));
    }

    // Populate CPU L2 tile cache and cascade-spill to L3/DRAM if evicted
    ChebyshevTile256 l2_tile{};
    l2_tile.t_min = (seg_t_start - Instant{}) / Second;
    l2_tile.t_max = (seg_t_end - Instant{}) / Second;
    l2_tile.degree = kMaxChebyshevDegree;
    l2_tile.body_or_vessel_id = 0;
    for (int k = 0; k <= kMaxChebyshevDegree; ++k) {
      l2_tile.cx[k] = seg.coeffs[k][0];
      l2_tile.cy[k] = seg.coeffs[k][1];
      l2_tile.cz[k] = seg.coeffs[k][2];
    }
    TimeSlicedScheduler::Instance().InsertL2Tile(l2_tile);
  }

  // Drain write-combining store buffer once after entire trajectory view completes!
  if (dma_mode == DmaTransferMode::DirectDma) {
    FlushStoreBuffers();
  }

#if OS_WIN
  if (is_d3d11_buffer && context != nullptr) {
    context->Unmap(d3d11_buffer, 0);
    context->Release();
    device->Release();
  }
#endif

  auto const end_time = std::chrono::high_resolution_clock::now();
  std::chrono::duration<double> const elapsed = end_time - start_time;
  std::size_t const bytes_transferred = num_segments * sizeof(ChebyshevSegmentData);
  DxgiAdapterFactory::Instance().RecordDmaWrite(bytes_transferred, elapsed.count());

  return num_segments;
}

}  // namespace internal

using internal::ChebyshevLobattoTransform16;
using internal::WriteSegmentData;
using internal::WriteSegmentDataBatch;
using internal::StreamStoreSegment;
using internal::FlushStoreBuffers;
using internal::ExportTrajectoryViewToChebyshevSegments;

}  // namespace _chebyshev_segment_export
}  // namespace ksp_plugin
}  // namespace principia
