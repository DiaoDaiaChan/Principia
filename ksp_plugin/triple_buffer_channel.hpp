#pragma once

#include <atomic>
#include <cstdint>
#include <type_traits>

namespace principia {
namespace ksp_plugin {
namespace _triple_buffer_channel {

// Lock-free triple buffer channel for asynchronous inter-thread data exchange.
// Writer produces data continuously; Reader consumes the latest available snapshot
// without any locking or thread contention.
template <typename T>
class alignas(64) TripleBufferChannel {
 public:
  TripleBufferChannel() : state_(0x000102) {}  // Reader=0, Ready=1, Writer=2

  // Writer thread: gets direct mutable reference to the private writing buffer.
  T& GetWriteBuffer() noexcept {
    uint32_t const current = state_.load(std::memory_order_relaxed);
    uint8_t const writer = current & 0xFF;
    return buffers_[writer];
  }

  // Writer thread: commits the written buffer and publishes it as Ready.
  void CommitWrite() noexcept {
    uint32_t current = state_.load(std::memory_order_relaxed);
    uint32_t next;
    do {
      uint8_t const reader = (current >> 16) & 0xFF;
      uint8_t const ready = (current >> 8) & 0xFF;
      uint8_t const writer = current & 0xFF;
      // Swap writer with ready. The next write will happen in the previous ready slot.
      next = (reader << 16) | (writer << 8) | ready;
    } while (!state_.compare_exchange_weak(
        current, next, std::memory_order_release, std::memory_order_relaxed));
  }

  // Reader thread: acquires the latest published snapshot.
  // Returns true if a newer buffer was swapped in, false if reading the same snapshot.
  bool AcquireRead(T const*& out_buffer) noexcept {
    uint32_t current = state_.load(std::memory_order_relaxed);
    uint32_t next;
    uint8_t ready;
    do {
      uint8_t const reader = (current >> 16) & 0xFF;
      ready = (current >> 8) & 0xFF;
      uint8_t const writer = current & 0xFF;
      // If ready == reader, no new commit has occurred.
      next = (ready << 16) | (reader << 8) | writer;
    } while (!state_.compare_exchange_weak(
        current, next, std::memory_order_acquire, std::memory_order_relaxed));

    out_buffer = &buffers_[ready];
    return true;
  }

  // Reader thread: gets the current reading buffer directly.
  T const& GetReadBuffer() const noexcept {
    uint32_t const current = state_.load(std::memory_order_relaxed);
    uint8_t const reader = (current >> 16) & 0xFF;
    return buffers_[reader];
  }

 private:
  // Encoded state: [Byte 2: reader] [Byte 1: ready] [Byte 0: writer]
  std::atomic<uint32_t> state_;
  T buffers_[3];
};

}  // namespace _triple_buffer_channel

using _triple_buffer_channel::TripleBufferChannel;

}  // namespace ksp_plugin
}  // namespace principia
