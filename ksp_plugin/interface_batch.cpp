#include "ksp_plugin/batch_state_sync.hpp"
#include "ksp_plugin/interface.hpp"

#if defined(_MSC_VER)
#define PRINCIPIA_EXPORT extern "C" __declspec(dllexport)
#else
#define PRINCIPIA_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace principia {
namespace interface {

using ksp_plugin::batch_state_sync::PartBatchInsertState;
using ksp_plugin::batch_state_sync::PartBatchApparentMotion;
using ksp_plugin::batch_state_sync::BatchStateProcessor;

PRINCIPIA_EXPORT void __cdecl principia__BatchInsertOrKeepLoadedParts(
    Plugin* const plugin,
    PartBatchInsertState const* const states,
    int const count) {
  BatchStateProcessor::ProcessBatchInsertOrKeepLoadedParts(plugin, states, count);
}

PRINCIPIA_EXPORT void __cdecl principia__BatchPartSetApparentRigidMotion(
    Plugin* const plugin,
    PartBatchApparentMotion const* const states,
    int const count) {
  BatchStateProcessor::ProcessBatchPartSetApparentRigidMotion(plugin, states, count);
}

PRINCIPIA_EXPORT void __cdecl principia__BatchPartGetActualRigidMotion(
    Plugin const* const plugin,
    Origin const* const origin,
    uint32_t const* const part_ids,
    QPRW* const motions_out,
    int const count) {
  BatchStateProcessor::ProcessBatchPartGetActualRigidMotion(
      plugin, origin, part_ids, motions_out, count);
}

}  // namespace interface
}  // namespace principia
