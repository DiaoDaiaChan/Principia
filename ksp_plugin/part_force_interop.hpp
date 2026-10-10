#pragma once
#include <cstddef>
#include <cstdint>
#include "ksp_plugin/identification.hpp"

namespace principia {
namespace ksp_plugin {
namespace _part_force_interop {
namespace internal {

using _identification::PartId;

#pragma pack(push, 8)
struct PartForceEntry {
  PartId part_id;
  double force_in_kilonewtons[3];
  double lever_arm[3];  // Relative to CoM, all zeros if at CoM
};
#pragma pack(pop)

static_assert(sizeof(PartForceEntry) == 56, "PartForceEntry binary layout mismatch with C# struct");
static_assert(offsetof(PartForceEntry, force_in_kilonewtons) == 8, "force_in_kilonewtons offset mismatch");
static_assert(offsetof(PartForceEntry, lever_arm) == 32, "lever_arm offset mismatch");

}  // namespace internal

using internal::PartForceEntry;

}  // namespace _part_force_interop
}  // namespace ksp_plugin
}  // namespace principia