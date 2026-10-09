#pragma once

#include <atomic>
#include <chrono>
#include <memory>
#include <thread>

#include "absl/status/status.h"
#include "absl/synchronization/mutex.h"
#include "base/not_null.hpp"
#include "base/stoppable_thread.hpp"
#include "geometry/instant.hpp"
#include "ksp_plugin/frames.hpp"
#include "physics/ephemeris.hpp"
#include "quantities/quantities.hpp"
#include "quantities/si.hpp"

namespace principia {
namespace ksp_plugin {
namespace _ephemeris_pacer {
namespace internal {

using namespace principia::base::_not_null;
using namespace principia::base::_stoppable_thread;
using namespace principia::geometry::_instant;
using namespace principia::ksp_plugin::_frames;
using namespace principia::physics::_ephemeris;
using namespace principia::quantities::_quantities;
using namespace principia::quantities::_si;

// Autonomous native background thread for prolonging the Ephemeris.
// Eliminates the need for Unity / C# to create artificial dummy carrier
// vessels and flight plans to drive numerical integration of the solar system.
class EphemerisPacer {
 public:
  static constexpr Time default_lookahead_horizon = 100.0 * Day;
  static constexpr Time default_refresh_threshold = 30.0 * Day;

  explicit EphemerisPacer(not_null<Ephemeris<Barycentric>*> ephemeris);
  ~EphemerisPacer();

  void Start();
  void Stop();

  void NotifyCurrentTime(Instant const& current_time);

  Time current_horizon() const;
  Instant target_extended_time() const;
  bool is_prolonging() const;

  void ForceProlong();
  void AdjustHorizon(Time const& delta);
  void TogglePause();
  bool is_paused() const;

 private:
  not_null<Ephemeris<Barycentric>*> const ephemeris_;

  mutable absl::Mutex lock_;
  Time lookahead_horizon_ ABSL_GUARDED_BY(lock_) = default_lookahead_horizon;
  Time refresh_threshold_ ABSL_GUARDED_BY(lock_) = default_refresh_threshold;

  Instant current_time_ ABSL_GUARDED_BY(lock_);
  Instant target_extended_time_ ABSL_GUARDED_BY(lock_);
  bool is_prolonging_ ABSL_GUARDED_BY(lock_) = false;
  bool force_prolong_ ABSL_GUARDED_BY(lock_) = false;
  std::atomic<bool> paused_{false};

  std::jthread worker_;
};

}  // namespace internal

using internal::EphemerisPacer;

}  // namespace _ephemeris_pacer

namespace ephemeris_pacer = _ephemeris_pacer;
}  // namespace ksp_plugin
}  // namespace principia
