#include "ksp_plugin/flight_plan_optimization_driver.hpp"

#include <memory>
#include <optional>
#include <thread>
#include <utility>

#include "absl/log/check.h"
#include "absl/log/log.h"
#include "absl/status/status.h"
#include "absl/synchronization/mutex.h"
#include "base/stoppable_thread.hpp"

#include <algorithm>
#include <cmath>
#include "geometry/instant.hpp"
#include "geometry/space.hpp"
#include "ksp_plugin/compute_pipeline.hpp"
#include "ksp_plugin/frames.hpp"
#include "ksp_plugin/gpu_interop_types.hpp"
#include "numerics/elementary_functions.hpp"
#include "physics/degrees_of_freedom.hpp"
#include "physics/ephemeris.hpp"
#include "physics/reference_frame.hpp"
#include "quantities/si.hpp"

namespace principia {
namespace ksp_plugin {
namespace _flight_plan_optimization_driver {
namespace internal {

using geometry::_instant::Instant;
using geometry::_space::Velocity;
using ksp_plugin::_frames::Barycentric;
using ksp_plugin::_frames::Navigation;
using ksp_plugin::_frames::NavigationManœuvre;
using physics::_reference_frame::Frenet;
using namespace principia::base::_stoppable_thread;
using numerics::_elementary_functions::Pow;
using physics::_degrees_of_freedom::DegreesOfFreedom;
using quantities::_si::Metre;
using quantities::_si::Second;

FlightPlanOptimizationDriver::FlightPlanOptimizationDriver(
    not_null<std::shared_ptr<FlightPlan>> const& flight_plan,
    FlightPlanOptimizer::MetricFactory metric_factory)
    : flight_plan_under_optimization_(*flight_plan),  // Copy.
      flight_plan_optimizer_(&flight_plan_under_optimization_,
                             std::move(metric_factory),
                             [this](FlightPlan const& flight_plan) {
                               UpdateLastFlightPlan(flight_plan);
                             }),
      last_flight_plan_(flight_plan) {}

FlightPlanOptimizationDriver::~FlightPlanOptimizationDriver() {
  // Ensure that we do not have a thread still running with references to the
  // members of this class when those are destroyed.
  Interrupt();
}

std::shared_ptr<FlightPlan>
FlightPlanOptimizationDriver::last_flight_plan() const {
  absl::ReaderMutexLock l(&lock_);
  return last_flight_plan_;
}

bool FlightPlanOptimizationDriver::done() const {
  absl::ReaderMutexLock l(&lock_);
  return optimizer_idle_;
}

void FlightPlanOptimizationDriver::Interrupt() {
  optimizer_ = std::jthread();
  // We are single-threaded here, no need to lock.
  optimizer_idle_ = true;
}

void FlightPlanOptimizationDriver::RequestOptimization(
    Parameters const& parameters) {
  // Only process this request if there is no analysis in progress.
  absl::MutexLock l(&lock_);
  if (optimizer_idle_) {
    last_parameters_ = parameters;
    optimizer_idle_ = false;
    optimizer_ = MakeStoppableThread([this, parameters]() {
      // Stage 1: GPU Massive Parallel Coarse Candidate Burn Search
      std::optional<FlightPlanOptimizer::Argument> coarse_guess;
      do {
        if (parameters.index < 0 ||
            parameters.index >= flight_plan_under_optimization_.number_of_manœuvres()) {
          break;
        }
        NavigationManœuvre const& manœuvre =
            flight_plan_under_optimization_.GetManœuvre(parameters.index);
        Instant const initial_time = manœuvre.initial_time();
        auto const& all_segments = flight_plan_under_optimization_.GetAllSegments();
        if (all_segments.empty() || initial_time < all_segments.t_min()) {
          break;
        }

        DegreesOfFreedom<Barycentric> const dof =
            all_segments.EvaluateDegreesOfFreedom(initial_time);
        auto const pos = (dof.position() - Barycentric::origin).coordinates();
        auto const vel = dof.velocity().coordinates();

        _gpu_interop_types::ShootingContextGpu context{};
        context.initial_time = static_cast<float>((initial_time - Instant{}) / Second);
        context.initial_pos[0] = static_cast<float>(pos.x / Metre);
        context.initial_pos[1] = static_cast<float>(pos.y / Metre);
        context.initial_pos[2] = static_cast<float>(pos.z / Metre);

        context.initial_vel[0] = static_cast<float>(vel.x / (Metre / Second));
        context.initial_vel[1] = static_cast<float>(vel.y / (Metre / Second));
        context.initial_vel[2] = static_cast<float>(vel.z / (Metre / Second));

        auto const dv_coords = manœuvre.Δv().coordinates();
        context.base_delta_v[0] = static_cast<float>(dv_coords.x / (Metre / Second));
        context.base_delta_v[1] = static_cast<float>(dv_coords.y / (Metre / Second));
        context.base_delta_v[2] = static_cast<float>(dv_coords.z / (Metre / Second));

        context.span_delta_t = static_cast<float>(manœuvre.duration() / Second);
        if (context.span_delta_t < 10.0f) context.span_delta_t = 60.0f;

        float const dv_mag = std::sqrt(
            context.base_delta_v[0] * context.base_delta_v[0] +
            context.base_delta_v[1] * context.base_delta_v[1] +
            context.base_delta_v[2] * context.base_delta_v[2]);
        float const span_dv = std::max(dv_mag * 0.3f, 50.0f);
        context.span_delta_v[0] = span_dv;
        context.span_delta_v[1] = span_dv;
        context.span_delta_v[2] = span_dv;

        context.step_size_seconds = 10.0f;
        context.candidate_count = 16384;
        context.step_count = 256;

        auto const ephemeris = flight_plan_under_optimization_.ephemeris();
        if (ephemeris != nullptr) {
          std::uint32_t c_idx = 0;
          for (auto const& body : ephemeris->bodies()) {
            if (c_idx >= _gpu_interop_types::kMaxCelestialsForGpu) break;
            DegreesOfFreedom<Barycentric> const body_dof =
                ephemeris->trajectory(body)->EvaluateDegreesOfFreedom(initial_time);
            auto const bpos = (body_dof.position() - Barycentric::origin).coordinates();
            auto const bvel = body_dof.velocity().coordinates();

            context.celestials[c_idx].position[0] = static_cast<float>(bpos.x / Metre);
            context.celestials[c_idx].position[1] = static_cast<float>(bpos.y / Metre);
            context.celestials[c_idx].position[2] = static_cast<float>(bpos.z / Metre);
            context.celestials[c_idx].velocity[0] = static_cast<float>(bvel.x / (Metre / Second));
            context.celestials[c_idx].velocity[1] = static_cast<float>(bvel.y / (Metre / Second));
            context.celestials[c_idx].velocity[2] = static_cast<float>(bvel.z / (Metre / Second));
            context.celestials[c_idx].gravitational_parameter = static_cast<float>(
                body->gravitational_parameter() / (Pow<3>(Metre) / Pow<2>(Second)));
            ++c_idx;
          }
          context.celestial_count = c_idx;
        }

        _gpu_interop_types::OptimalCandidateBurn optimal{};
        if (_compute_pipeline::ComputePipeline::Instance().OptimizeFlightPlanShooting(
                context, optimal)) {
          FlightPlanOptimizer::Argument guess{};
          guess.Δinitial_time = optimal.delta_t * Second;
          guess.ΔΔv = Velocity<Frenet<Navigation>>({
              optimal.delta_v_tangent * (Metre / Second),
              optimal.delta_v_normal * (Metre / Second),
              optimal.delta_v_binormal * (Metre / Second)});
          coarse_guess = guess;
          LOG(INFO) << "FlightPlanOptimizer: GPU coarse initial guess found (cost="
                    << optimal.cost_metric << ", periapsis="
                    << optimal.periapsis_distance << "m)";
        }
      } while (false);

      // Stage 2: Precision BFGS local optimization seeded with coarse initial guess
      const absl::Status optimization_status = flight_plan_optimizer_.Optimize(
          parameters.index, parameters.Δv_tolerance, coarse_guess);

      absl::MutexLock l(&lock_);
      if (optimization_status.ok()) {
        last_flight_plan_ =
            make_not_null_shared<FlightPlan>(flight_plan_under_optimization_);
        last_flight_plan_->EnableAnalysis(/*enabled=*/true);
      } else {
        LOG(WARNING) << "Optimization returned " << optimization_status;
      }
      optimizer_idle_ = true;
    });
  }
}

std::optional<FlightPlanOptimizationDriver::Parameters> const&
FlightPlanOptimizationDriver::last_parameters() const {
  absl::ReaderMutexLock l(&lock_);
  return last_parameters_;
}

void FlightPlanOptimizationDriver::Wait() const {
  absl::ReaderMutexLock l(&lock_);
  lock_.Await(absl::Condition(&optimizer_idle_));
}

void FlightPlanOptimizationDriver::UpdateLastFlightPlan(
    FlightPlan const& flight_plan) {
  absl::MutexLock l(&lock_);
  last_flight_plan_ = make_not_null_shared<FlightPlan>(flight_plan);
  // TODO(phl): This is wasteful, but otherwise, what happens if we interrupt
  // optimization?
  last_flight_plan_->EnableAnalysis(/*enabled=*/true);
}

}  // namespace internal
}  // namespace _flight_plan_optimization_driver
}  // namespace ksp_plugin
}  // namespace principia
