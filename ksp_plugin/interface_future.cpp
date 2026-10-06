#include "ksp_plugin/interface.hpp"

#include <utility>

#include "absl/log/check.h"
#include "absl/log/log.h"
#include "journal/method.hpp"
#include "journal/profiles.hpp"  // 🧙 For generated profiles.
#include "ksp_plugin/identification.hpp"
#include "ksp_plugin/iterators.hpp"

namespace principia {
namespace interface {

using namespace principia::journal::_method;
using namespace principia::ksp_plugin::_identification;
using namespace principia::ksp_plugin::_iterators;

PileUpFuture* __cdecl principia__FutureCatchUpVessel(
    Plugin* const plugin,
    char const* const vessel_guid) {
  journal::Method<journal::FutureCatchUpVessel> m({plugin, vessel_guid});
  CHECK(plugin != nullptr);
  CHECK(vessel_guid != nullptr);
  auto future = plugin->CatchUpVessel(vessel_guid);
  return m.Return(future.release());
}

bool __cdecl principia__FutureTryHarvestVesselCatchUp(
    Plugin* const plugin,
    PileUpFuture** const future,
    Iterator** const collided_vessels) {
  journal::Method<journal::FutureTryHarvestVesselCatchUp> m(
      {plugin, future}, {future, collided_vessels});
  CHECK(plugin != nullptr);
  CHECK(future != nullptr);
  CHECK(*future != nullptr);
  VesselSet collided_vessel_set;
  if (plugin->TryHarvestVesselCatchUp(**future, collided_vessel_set)) {
    TakeOwnership(future);
    *collided_vessels =
        new TypedIterator<VesselSet>(std::move(collided_vessel_set), plugin);
    return m.Return(true);
  } else {
    *collided_vessels = nullptr;
    return m.Return(false);
  }
}

void __cdecl principia__FutureWaitForVesselToCatchUp(
    Plugin* const plugin,
    PileUpFuture** const future,
    Iterator** const collided_vessels) {
  journal::Method<journal::FutureWaitForVesselToCatchUp> m(
      {plugin, future}, {future, collided_vessels});
  CHECK(plugin != nullptr);
  CHECK(future != nullptr);
  auto const owned_future = TakeOwnership(future);
  VesselSet collided_vessel_set;
  plugin->WaitForVesselToCatchUp(*owned_future, collided_vessel_set);
  *collided_vessels =
      new TypedIterator<VesselSet>(std::move(collided_vessel_set), plugin);
  return m.Return();
}

}  // namespace interface
}  // namespace principia
