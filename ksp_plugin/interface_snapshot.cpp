#include "ksp_plugin/dense_polynomial_slice.hpp"
#include "ksp_plugin/triple_buffer_channel.hpp"
#include "ksp_plugin/spsc_command_buffer.hpp"

#include <memory>

#if defined(_MSC_VER)
#define PRINCIPIA_EXPORT extern "C" __declspec(dllexport)
#else
#define PRINCIPIA_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace principia {
namespace interface {

using ksp_plugin::DensePolynomialSlice;
using ksp_plugin::TripleBufferChannel;
using ksp_plugin::CommandPacket;
using ksp_plugin::SpscCommandRingBuffer;

using StateChannel = TripleBufferChannel<DensePolynomialSlice>;
using CommandQueue = SpscCommandRingBuffer<1024>;

PRINCIPIA_EXPORT StateChannel* __cdecl principia__StateBusCreateChannel() {
  return new StateChannel();
}

PRINCIPIA_EXPORT void __cdecl principia__StateBusDestroyChannel(StateChannel** channel) {
  if (channel != nullptr && *channel != nullptr) {
    delete *channel;
    *channel = nullptr;
  }
}

PRINCIPIA_EXPORT void __cdecl principia__StateBusCommitWrite(
    StateChannel* const channel,
    DensePolynomialSlice const* const slice) {
  if (channel != nullptr && slice != nullptr) {
    channel->GetWriteBuffer() = *slice;
    channel->CommitWrite();
  }
}

PRINCIPIA_EXPORT bool __cdecl principia__StateBusAcquireRead(
    StateChannel* const channel,
    DensePolynomialSlice* const out_slice) {
  if (channel == nullptr || out_slice == nullptr) {
    return false;
  }
  DensePolynomialSlice const* buffer = nullptr;
  channel->AcquireRead(buffer);
  if (buffer != nullptr) {
    *out_slice = *buffer;
    return true;
  }
  return false;
}

PRINCIPIA_EXPORT CommandQueue* __cdecl principia__CommandBusCreate() {
  return new CommandQueue();
}

PRINCIPIA_EXPORT void __cdecl principia__CommandBusDestroy(CommandQueue** queue) {
  if (queue != nullptr && *queue != nullptr) {
    delete *queue;
    *queue = nullptr;
  }
}

PRINCIPIA_EXPORT bool __cdecl principia__CommandBusTryPush(
    CommandQueue* const queue,
    CommandPacket const* const packet) {
  if (queue != nullptr && packet != nullptr) {
    return queue->TryPush(*packet);
  }
  return false;
}

PRINCIPIA_EXPORT bool __cdecl principia__CommandBusTryPop(
    CommandQueue* const queue,
    CommandPacket* const out_packet) {
  if (queue != nullptr && out_packet != nullptr) {
    return queue->TryPop(*out_packet);
  }
  return false;
}

}  // namespace interface
}  // namespace principia
