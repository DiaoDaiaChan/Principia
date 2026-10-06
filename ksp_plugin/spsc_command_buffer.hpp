#pragma once

#include <atomic>
#include <cstdint>
#include <cstring>

namespace principia {
namespace ksp_plugin {
namespace _spsc_command_buffer {

// Command types supported by the asynchronous engine command bus.
enum class CommandType : uint32_t {
  None = 0,
  SetManœuvre = 1,
  DeleteManœuvre = 2,
  ApplyIntrinsicForce = 3,
  ApplyIntrinsicTorque = 4,
  SetBodyFrame = 5,
  VesselTeleport = 6,
  ResetSimulation = 7
};

// Compact 64-byte command packet.
struct alignas(8) CommandPacket {
  CommandType type;
  uint32_t vessel_id_high;
  uint64_t vessel_id_low;
  double timestamp;
  double params[4];  // Delta-V, epoch, or torque vector components.
  uint64_t flags;
};

// Lock-free Single-Producer Single-Consumer (SPSC) command ring buffer.
// Producer: Unity UI / Main Thread
// Consumer: Native Physics Worker Thread
template <size_t Capacity = 1024>
class SpscCommandRingBuffer {
  static_assert((Capacity & (Capacity - 1)) == 0, "Capacity must be a power of two");

 public:
  SpscCommandRingBuffer() : head_(0), tail_(0) {}

  // Push a command from the producer thread (Unity main thread).
  // Returns true on success, false if buffer is full.
  bool TryPush(CommandPacket const& packet) noexcept {
    size_t const current_head = head_.load(std::memory_order_relaxed);
    size_t const current_tail = tail_.load(std::memory_order_acquire);

    if ((current_head - current_tail) >= Capacity) {
      return false;  // Buffer full (backpressure)
    }

    buffer_[current_head & (Capacity - 1)] = packet;
    head_.store(current_head + 1, std::memory_order_release);
    return true;
  }

  // Pop a command from the consumer thread (Physics worker thread).
  // Returns true on success, false if buffer is empty.
  bool TryPop(CommandPacket& packet) noexcept {
    size_t const current_tail = tail_.load(std::memory_order_relaxed);
    size_t const current_head = head_.load(std::memory_order_acquire);

    if (current_tail == current_head) {
      return false;  // Buffer empty
    }

    packet = buffer_[current_tail & (Capacity - 1)];
    tail_.store(current_tail + 1, std::memory_order_release);
    return true;
  }

  // Returns number of pending commands.
  size_t Size() const noexcept {
    size_t const current_head = head_.load(std::memory_order_relaxed);
    size_t const current_tail = tail_.load(std::memory_order_relaxed);
    return (current_head >= current_tail) ? (current_head - current_tail) : 0;
  }

 private:
  alignas(64) std::atomic<size_t> head_;
  alignas(64) std::atomic<size_t> tail_;
  alignas(64) CommandPacket buffer_[Capacity];
};

}  // namespace _spsc_command_buffer

using _spsc_command_buffer::CommandType;
using _spsc_command_buffer::CommandPacket;
using _spsc_command_buffer::SpscCommandRingBuffer;

}  // namespace ksp_plugin
}  // namespace principia
