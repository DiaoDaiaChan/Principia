// .\Release\x64\benchmarks.exe --benchmark_repetitions=10 --benchmark_filter=ThreadPool  // NOLINT(whitespace/line_length)

#include <cmath>
#include <cstdint>
#include <future>
#include <random>
#include <vector>

#include "absl/synchronization/mutex.h"
#include "base/thread_pool.hpp"
#include "benchmark/benchmark.h"

namespace principia {
namespace base {
namespace {

using namespace principia::base::_thread_pool;

absl::Mutex lock;
std::mt19937_64 random(42);
std::uniform_int_distribution<int> distribution(0, 1e5);

double ComsumeCpuNoLock(std::int64_t const n) {
  double result;
  {
    absl::ReaderMutexLock l(&lock);
    result = distribution(random);
  }
  for (int i = 0; i < n; ++i) {
    result += std::sqrt(i);
  }
  return result;
}

double ComsumeCpuSharedLock(std::int64_t const n) {
  absl::ReaderMutexLock l(&lock);
  double result = distribution(random);
  for (int i = 0; i < n; ++i) {
    result += std::sqrt(i);
  }
  return result;
}

double ComsumeCpuExclusiveLock(std::int64_t const n) {
  absl::MutexLock l(&lock);
  double result = distribution(random);
  for (int i = 0; i < n; ++i) {
    result += std::sqrt(i);
  }
  return result;
}

void BM_ThreadPoolNoLock(benchmark::State& state) {
  ThreadPool<void> pool(/*pool_size=*/state.range(0));
  std::int64_t const count = 1000;
  for (auto _ : state) {
    std::vector<std::future<void>> futures;
    futures.reserve(count);
    for (int i = 0; i < count; ++i) {
      futures.push_back(pool.Add([]() {
        double const result = ComsumeCpuNoLock(1e5);
        benchmark::DoNotOptimize(result);
      }));
    }
    for (auto const& future : futures) {
      future.wait();
    }
  }
}

void BM_ThreadPoolSharedLock(benchmark::State& state) {
  ThreadPool<void> pool(/*pool_size=*/state.range(0));
  std::int64_t const count = 1000;
  for (auto _ : state) {
    std::vector<std::future<void>> futures;
    futures.reserve(count);
    for (int i = 0; i < count; ++i) {
      futures.push_back(pool.Add([]() {
        double const result = ComsumeCpuSharedLock(1e5);
        benchmark::DoNotOptimize(result);
      }));
    }
    for (auto const& future : futures) {
      future.wait();
    }
  }
}

void BM_ThreadPoolExclusiveLock(benchmark::State& state) {
  ThreadPool<void> pool(/*pool_size=*/state.range(0));
  std::int64_t const count = 1000;
  for (auto _ : state) {
    std::vector<std::future<void>> futures;
    futures.reserve(count);
    for (int i = 0; i < count; ++i) {
      futures.push_back(pool.Add([]() {
        double const result = ComsumeCpuExclusiveLock(1e5);
        benchmark::DoNotOptimize(result);
      }));
    }
    for (auto const& future : futures) {
      future.wait();
    }
  }
}

BENCHMARK(BM_ThreadPoolNoLock)
    ->Arg(1)->Arg(2)->Arg(4)->Arg(8)->Arg(16)
    ->Unit(benchmark::kMillisecond);
BENCHMARK(BM_ThreadPoolSharedLock)
    ->Arg(1)->Arg(2)->Arg(4)->Arg(8)->Arg(16)
    ->Unit(benchmark::kMillisecond);
BENCHMARK(BM_ThreadPoolExclusiveLock)
    ->Arg(1)->Arg(2)->Arg(4)->Arg(8)->Arg(16)
    ->Unit(benchmark::kMillisecond);

// ============================================================================
// 1. ContinuousTrajectory: Master Shared Atomic vs Branch Thread-Local Cache
// ============================================================================
struct MasterTrajectoryLookup {
  struct Poly {
    double t_min = 0;
    double t_max = 0;
    double coef[8] = {1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005};
    double Eval(double const t) const {
      double r = 0;
      double dt = t - t_min;
      double p = 1.0;
      for (int i = 0; i < 8; ++i) {
        r += coef[i] * p;
        p *= dt;
      }
      return r;
    }
  };
  std::vector<Poly> polynomials;
  mutable std::atomic<Poly const*> last_poly{nullptr};

  explicit MasterTrajectoryLookup(int const num_segments) : polynomials(num_segments) {
    for (int i = 0; i < num_segments; ++i) {
      polynomials[i].t_min = i * 100.0;
      polynomials[i].t_max = (i + 1) * 100.0;
    }
  }

  double Evaluate(double const t) const {
    auto const* p = last_poly.load(std::memory_order_relaxed);
    if (p != nullptr && p->t_min <= t && t <= p->t_max) {
      return p->Eval(t);
    }
    auto it = std::upper_bound(
        polynomials.begin(), polynomials.end(), t,
        [](double val, Poly const& elem) { return val < elem.t_min; });
    if (it != polynomials.begin()) --it;
    p = &*it;
    last_poly.store(p, std::memory_order_relaxed);
    return p->Eval(t);
  }
};

struct NewTrajectoryLookup {
  using Poly = MasterTrajectoryLookup::Poly;
  std::vector<Poly> polynomials;

  explicit NewTrajectoryLookup(int const num_segments) : polynomials(num_segments) {
    for (int i = 0; i < num_segments; ++i) {
      polynomials[i].t_min = i * 100.0;
      polynomials[i].t_max = (i + 1) * 100.0;
    }
  }

  double Evaluate(double const t) const {
    struct Slot {
      double t_min = -1.0;
      double t_max = -1.0;
      Poly const* poly = nullptr;
    };
    thread_local Slot cache[32] = {};
    std::uint64_t hash = static_cast<std::uint64_t>(t * 0.01);
    std::size_t slot = (hash ^ (hash >> 5)) & 31;
    if (cache[slot].poly != nullptr && cache[slot].t_min <= t && t <= cache[slot].t_max) {
      return cache[slot].poly->Eval(t);
    }
    auto it = std::upper_bound(
        polynomials.begin(), polynomials.end(), t,
        [](double val, Poly const& elem) { return val < elem.t_min; });
    if (it != polynomials.begin()) --it;
    auto const* p = &*it;
    cache[slot] = {p->t_min, p->t_max, p};
    return p->Eval(t);
  }
};

MasterTrajectoryLookup master_traj(500);
NewTrajectoryLookup new_traj(500);

void BM_ContinuousTrajectory_Master_SharedAtomic(benchmark::State& state) {
  ThreadPool<void> pool(/*pool_size=*/state.range(0));
  int const threads = state.range(0);
  int const evals_per_thread = 50000;
  for (auto _ : state) {
    std::vector<std::future<void>> futures;
    futures.reserve(threads);
    for (int t = 0; t < threads; ++t) {
      futures.push_back(pool.Add([t, evals_per_thread]() {
        double acc = 0;
        // Simulating interleaved vessel evaluation across different time windows
        double base_time = (t % 10) * 4500.0;
        for (int i = 0; i < evals_per_thread; ++i) {
          double query_t = base_time + (i % 500) * 0.1;
          acc += master_traj.Evaluate(query_t);
        }
        benchmark::DoNotOptimize(acc);
      }));
    }
    for (auto const& f : futures) {
      f.wait();
    }
  }
}

void BM_ContinuousTrajectory_New_ThreadLocalCache(benchmark::State& state) {
  ThreadPool<void> pool(/*pool_size=*/state.range(0));
  int const threads = state.range(0);
  int const evals_per_thread = 50000;
  for (auto _ : state) {
    std::vector<std::future<void>> futures;
    futures.reserve(threads);
    for (int t = 0; t < threads; ++t) {
      futures.push_back(pool.Add([t, evals_per_thread]() {
        double acc = 0;
        double base_time = (t % 10) * 4500.0;
        for (int i = 0; i < evals_per_thread; ++i) {
          double query_t = base_time + (i % 500) * 0.1;
          acc += new_traj.Evaluate(query_t);
        }
        benchmark::DoNotOptimize(acc);
      }));
    }
    for (auto const& f : futures) {
      f.wait();
    }
  }
}

BENCHMARK(BM_ContinuousTrajectory_Master_SharedAtomic)
    ->Arg(1)->Arg(4)->Arg(8)->Arg(16)->Arg(24)->Arg(32)->Arg(64)
    ->Unit(benchmark::kMillisecond);

BENCHMARK(BM_ContinuousTrajectory_New_ThreadLocalCache)
    ->Arg(1)->Arg(4)->Arg(8)->Arg(16)->Arg(24)->Arg(32)->Arg(64)
    ->Unit(benchmark::kMillisecond);

// ============================================================================
// 2. Adaptive ODE: Master Per-Stage CAS Locking vs Branch Scope-Level Lock
// ============================================================================
absl::Mutex ephemeris_lock;

double SimulateGravitationalAcceleration(double x, double y, double z) {
  double ax = 0, ay = 0, az = 0;
  // 10 massive celestial bodies
  for (int b = 0; b < 10; ++b) {
    double bx = b * 1e7;
    double by = b * 5e6;
    double bz = b * 1e6;
    double dx = bx - x;
    double dy = by - y;
    double dz = bz - z;
    double r2 = dx * dx + dy * dy + dz * dz + 1.0;
    double r3 = r2 * std::sqrt(r2);
    ax += 1e11 * dx / r3;
    ay += 1e11 * dy / r3;
    az += 1e11 * dz / r3;
  }
  return ax + ay + az;
}

// Master: Each stage (16 stages per step, 200 steps = 3200 lock acquisitions)
void BM_AdaptiveODE_Master_PerStageLock(benchmark::State& state) {
  ThreadPool<void> pool(/*pool_size=*/state.range(0));
  int const threads = state.range(0);
  int const steps = 200;
  int const stages = 16;
  for (auto _ : state) {
    std::vector<std::future<void>> futures;
    futures.reserve(threads);
    for (int t = 0; t < threads; ++t) {
      futures.push_back(pool.Add([steps, stages]() {
        double acc = 0;
        double x = 1e6, y = 2e6, z = 3e6;
        for (int step = 0; step < steps; ++step) {
          for (int stage = 0; stage < stages; ++stage) {
            absl::ReaderMutexLock l(&ephemeris_lock);
            acc += SimulateGravitationalAcceleration(x + stage, y, z);
          }
          x += 10.0;
        }
        benchmark::DoNotOptimize(acc);
      }));
    }
    for (auto const& f : futures) {
      f.wait();
    }
  }
}

// Our Branch: Scope holds reader lock ONCE for all steps (1 lock acquisition)
void BM_AdaptiveODE_New_ScopeLocked(benchmark::State& state) {
  ThreadPool<void> pool(/*pool_size=*/state.range(0));
  int const threads = state.range(0);
  int const steps = 200;
  int const stages = 16;
  for (auto _ : state) {
    std::vector<std::future<void>> futures;
    futures.reserve(threads);
    for (int t = 0; t < threads; ++t) {
      futures.push_back(pool.Add([steps, stages]() {
        double acc = 0;
        double x = 1e6, y = 2e6, z = 3e6;
        {
          absl::ReaderMutexLock l(&ephemeris_lock);
          for (int step = 0; step < steps; ++step) {
            for (int stage = 0; stage < stages; ++stage) {
              acc += SimulateGravitationalAcceleration(x + stage, y, z);
            }
            x += 10.0;
          }
        }
        benchmark::DoNotOptimize(acc);
      }));
    }
    for (auto const& f : futures) {
      f.wait();
    }
  }
}

BENCHMARK(BM_AdaptiveODE_Master_PerStageLock)
    ->Arg(1)->Arg(4)->Arg(8)->Arg(16)->Arg(24)->Arg(32)->Arg(64)
    ->Unit(benchmark::kMillisecond);

BENCHMARK(BM_AdaptiveODE_New_ScopeLocked)
    ->Arg(1)->Arg(4)->Arg(8)->Arg(16)->Arg(24)->Arg(32)->Arg(64)
    ->Unit(benchmark::kMillisecond);

// ============================================================================
// 3. Ephemeris Pacer: Background Exclusive Lock Chunk Size vs Main Thread Frame Latency
// ============================================================================
void BM_PacerLock_Master_4096Steps(benchmark::State& state) {
  absl::Mutex pacer_lock;
  std::atomic<bool> stop{false};
  std::thread writer([&]() {
    while (!stop.load(std::memory_order_relaxed)) {
      {
        absl::MutexLock l(&pacer_lock);
        double acc = 0;
        for (int i = 0; i < 4096 * 30; ++i) {
          acc += std::sqrt(i);
        }
        benchmark::DoNotOptimize(acc);
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
  });

  for (auto _ : state) {
    absl::ReaderMutexLock l(&pacer_lock);
    benchmark::DoNotOptimize(1);
  }
  stop.store(true);
  writer.join();
}

void BM_PacerLock_New_64Steps(benchmark::State& state) {
  absl::Mutex pacer_lock;
  std::atomic<bool> stop{false};
  std::thread writer([&]() {
    while (!stop.load(std::memory_order_relaxed)) {
      {
        absl::MutexLock l(&pacer_lock);
        double acc = 0;
        for (int i = 0; i < 64 * 30; ++i) {
          acc += std::sqrt(i);
        }
        benchmark::DoNotOptimize(acc);
      }
      std::this_thread::yield();
    }
  });

  for (auto _ : state) {
    absl::ReaderMutexLock l(&pacer_lock);
    benchmark::DoNotOptimize(1);
  }
  stop.store(true);
  writer.join();
}

BENCHMARK(BM_PacerLock_Master_4096Steps)
    ->Iterations(200)
    ->Unit(benchmark::kMillisecond);

BENCHMARK(BM_PacerLock_New_64Steps)
    ->Iterations(200)
    ->Unit(benchmark::kMillisecond);

}  // namespace
}  // namespace base
}  // namespace principia
