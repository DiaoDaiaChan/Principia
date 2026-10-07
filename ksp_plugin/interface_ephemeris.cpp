#include "ksp_plugin/ephemeris_pacer.hpp"
#include "ksp_plugin/interface.hpp"
#include "ksp_plugin/plugin.hpp"

#if defined(_MSC_VER)
#define PRINCIPIA_EXPORT extern "C" __declspec(dllexport)
#else
#define PRINCIPIA_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace principia {
namespace interface {

PRINCIPIA_EXPORT double __cdecl principia__EphemerisGetHorizonSeconds(
    Plugin const* const plugin) {
  if (plugin == nullptr || plugin->ephemeris_pacer() == nullptr) {
    return 0.0;
  }
  return plugin->ephemeris_pacer()->current_horizon() / quantities::si::Second;
}

PRINCIPIA_EXPORT double __cdecl principia__EphemerisGetTargetExtendedTime(
    Plugin const* const plugin) {
  if (plugin == nullptr || plugin->ephemeris_pacer() == nullptr) {
    return 0.0;
  }
  return ToGameTime(*plugin, plugin->ephemeris_pacer()->target_extended_time());
}

PRINCIPIA_EXPORT bool __cdecl principia__EphemerisIsProlonging(
    Plugin const* const plugin) {
  if (plugin == nullptr || plugin->ephemeris_pacer() == nullptr) {
    return false;
  }
  return plugin->ephemeris_pacer()->is_prolonging();
}

PRINCIPIA_EXPORT void __cdecl principia__EphemerisForceProlong(
    Plugin* const plugin) {
  if (plugin != nullptr && plugin->ephemeris_pacer() != nullptr) {
    plugin->ephemeris_pacer()->ForceProlong();
  }
}

PRINCIPIA_EXPORT void __cdecl principia__EphemerisAdjustHorizon(
    Plugin* const plugin,
    double const delta_seconds) {
  if (plugin != nullptr && plugin->ephemeris_pacer() != nullptr) {
    plugin->ephemeris_pacer()->AdjustHorizon(
        delta_seconds * quantities::si::Second);
  }
}

PRINCIPIA_EXPORT void __cdecl principia__EphemerisTogglePause(
    Plugin* const plugin) {
  if (plugin != nullptr && plugin->ephemeris_pacer() != nullptr) {
    plugin->ephemeris_pacer()->TogglePause();
  }
}

PRINCIPIA_EXPORT bool __cdecl principia__EphemerisIsPaused(
    Plugin const* const plugin) {
  if (plugin == nullptr || plugin->ephemeris_pacer() == nullptr) {
    return false;
  }
  return plugin->ephemeris_pacer()->is_paused();
}

}  // namespace interface
}  // namespace principia
