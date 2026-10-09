#include "ksp_plugin/ephemeris_pacer.hpp"

#include <chrono>
#include <thread>
#include <utility>

#include "absl/log/log.h"
#include "base/macros.hpp"

namespace principia {
namespace ksp_plugin {
namespace _ephemeris_pacer {
namespace internal {

using namespace std::chrono_literals;

constexpr std::int64_t max_ephemeris_steps_per_frame = 4096;

EphemerisPacer::EphemerisPacer(not_null<Ephemeris<Barycentric>*> const ephemeris)
    : ephemeris_(ephemeris) {}

EphemerisPacer::~EphemerisPacer() {
  Stop();
}

void EphemerisPacer::Start() {
  absl::MutexLock l(&lock_);
  if (worker_.joinable()) {
    return;
  }
  worker_ = MakeStoppableThread([this]() -> absl::Status {
    while (true) {
      RETURN_IF_STOPPED;
      std::this_thread::sleep_for(50ms);
      RETURN_IF_STOPPED;

      if (paused_.load(std::memory_order_relaxed)) {
        continue;
      }

      Instant t_now;
      Time lookahead;
      Time threshold;
      bool force = false;
      {
        absl::MutexLock l(&lock_);
        t_now = current_time_;
        lookahead = lookahead_horizon_;
        threshold = refresh_threshold_;
        force = force_prolong_;
        force_prolong_ = false;
      }

      if (t_now == Instant{}) {
        continue;
      }

      Instant const t_max = ephemeris_->t_max();
      if (!force && t_max - t_now >= threshold) {
        continue;
      }

      Instant const target = t_now + lookahead;
      {
        absl::MutexLock l(&lock_);
        target_extended_time_ = target;
        is_prolonging_ = true;
      }

      while (ephemeris_->t_max() < target) {
        RETURN_IF_STOPPED;
        std::this_thread::sleep_for(20ms);
        RETURN_IF_STOPPED;
        ephemeris_->Prolong(target, max_ephemeris_steps_per_frame).IgnoreError();
      }

      {
        absl::MutexLock l(&lock_);
        is_prolonging_ = false;
      }
    }
    return absl::OkStatus();
  });
}

void EphemerisPacer::Stop() {
  worker_ = std::jthread();
  absl::MutexLock l(&lock_);
  is_prolonging_ = false;
}

void EphemerisPacer::NotifyCurrentTime(Instant const& current_time) {
  absl::MutexLock l(&lock_);
  current_time_ = current_time;
}

Time EphemerisPacer::current_horizon() const {
  absl::MutexLock l(&lock_);
  if (current_time_ == Instant{}) {
    return Time{};
  }
  Instant const t_max = ephemeris_->t_max();
  if (t_max > current_time_) {
    return t_max - current_time_;
  }
  return Time{};
}

Instant EphemerisPacer::target_extended_time() const {
  absl::MutexLock l(&lock_);
  return target_extended_time_;
}

bool EphemerisPacer::is_prolonging() const {
  absl::MutexLock l(&lock_);
  return is_prolonging_;
}

void EphemerisPacer::ForceProlong() {
  absl::MutexLock l(&lock_);
  force_prolong_ = true;
}

void EphemerisPacer::AdjustHorizon(Time const& delta) {
  absl::MutexLock l(&lock_);
  lookahead_horizon_ = std::max(50.0 * Day, lookahead_horizon_ + delta);
  refresh_threshold_ = std::max(20.0 * Day, lookahead_horizon_ * 0.333);
}

void EphemerisPacer::TogglePause() {
  paused_.store(!paused_.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
}

bool EphemerisPacer::is_paused() const {
  return paused_.load(std::memory_order_relaxed);
}

}  // namespace internal
}  // namespace _ephemeris_pacer
}  // namespace ksp_plugin
}  // namespace principia
