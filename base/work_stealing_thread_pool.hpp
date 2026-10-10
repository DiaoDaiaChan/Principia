#pragma once

#include <atomic>
#include <deque>
#include <functional>
#include <future>
#include <memory>
#include <optional>
#include <random>
#include <thread>
#include <utility>
#include <vector>

#include "absl/base/thread_annotations.h"
#include "absl/status/status.h"
#include "absl/synchronization/mutex.h"
#include "absl/time/time.h"
#include "base/macros.hpp"

#if OS_WIN
#include <windows.h>
#undef interface
#endif

namespace principia {
namespace base {
namespace _work_stealing_thread_pool {
namespace internal {

// Topology information for hybrid Performance (P-Core) and Efficiency (E-Core) systems.
struct ProcessorCoreTopology {
  std::uint64_t p_core_mask{0};
  std::uint64_t e_core_mask{0};
  int p_core_count{0};
  int e_core_count{0};
  bool is_hybrid{false};
};

// Detects CPU core efficiency classes via Win32 GetLogicalProcessorInformationEx.
inline ProcessorCoreTopology DetectProcessorCoreTopology() {
  ProcessorCoreTopology topo;
#if OS_WIN
  DWORD length = 0;
  if (!GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &length) &&
      GetLastError() == ERROR_INSUFFICIENT_BUFFER) {
    std::vector<char> buffer(length);
    if (GetLogicalProcessorInformationEx(
            RelationProcessorCore,
            reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data()),
            &length)) {
      char const* ptr = buffer.data();
      char const* end = ptr + length;
      std::uint8_t max_efficiency = 0;
      std::uint8_t min_efficiency = 255;

      while (ptr < end) {
        auto const* info =
            reinterpret_cast<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX const*>(ptr);
        if (info->Relationship == RelationProcessorCore) {
          std::uint8_t const eff = info->Processor.EfficiencyClass;
          if (eff > max_efficiency) max_efficiency = eff;
          if (eff < min_efficiency) min_efficiency = eff;
        }
        ptr += info->Size;
      }

      if (max_efficiency > min_efficiency) {
        topo.is_hybrid = true;
      }

      ptr = buffer.data();
      while (ptr < end) {
        auto const* info =
            reinterpret_cast<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX const*>(ptr);
        if (info->Relationship == RelationProcessorCore) {
          std::uint64_t mask = 0;
          if (info->Processor.GroupCount > 0) {
            mask = static_cast<std::uint64_t>(info->Processor.GroupMask[0].Mask);
          }
          if (topo.is_hybrid && info->Processor.EfficiencyClass == max_efficiency) {
            topo.p_core_mask |= mask;
            ++topo.p_core_count;
          } else if (topo.is_hybrid) {
            topo.e_core_mask |= mask;
            ++topo.e_core_count;
          } else {
            topo.p_core_mask |= mask;
            ++topo.p_core_count;
          }
        }
        ptr += info->Size;
      }
    }
  }
#endif
  if (topo.p_core_count == 0 && topo.e_core_count == 0) {
    topo.p_core_count = static_cast<int>(std::thread::hardware_concurrency());
  }
  return topo;
}

template<typename T>
void ExecuteAndSetValue(std::function<T()> const& function,
                        std::promise<T>& promise) {
  promise.set_value(function());
}

template<>
inline void ExecuteAndSetValue<void>(std::function<void()> const& function,
                                     std::promise<void>& promise) {
  function();
  promise.set_value();
}

// Lock-free circular work-stealing deque implementing Chase-Lev algorithm.
// Single-producer (owner) Push/Pop at bottom (LIFO for data cache locality).
// Multi-consumer (thieves) Steal at top (FIFO for fairness).
template<typename TaskType>
class alignas(64) ChaseLevDeque final {
 public:
  static constexpr std::int64_t kCapacity = 1024;
  static constexpr std::int64_t kMask = kCapacity - 1;

  ChaseLevDeque() {
    buffer_ = std::make_unique<TaskType[]>(kCapacity);
  }

  // Owner pushes to bottom (wait-free)
  bool Push(TaskType task) {
    std::int64_t const b = bottom_.load(std::memory_order_relaxed);
    std::int64_t const t = top_.load(std::memory_order_acquire);
    if (b - t >= kCapacity - 1) {
      return false;  // Deque full
    }
    buffer_[b & kMask] = std::move(task);
    bottom_.store(b + 1, std::memory_order_release);
    return true;
  }

  // Owner pops from bottom (lock-free with CAS on last item)
  std::optional<TaskType> Pop() {
    std::int64_t const b = bottom_.load(std::memory_order_relaxed) - 1;
    bottom_.store(b, std::memory_order_relaxed);
    std::atomic_thread_fence(std::memory_order_seq_cst);
    std::int64_t t = top_.load(std::memory_order_relaxed);
    if (t <= b) {
      if (t == b) {
        // Last element race against thieves
        if (!top_.compare_exchange_strong(
                t, t + 1, std::memory_order_seq_cst, std::memory_order_relaxed)) {
          bottom_.store(b + 1, std::memory_order_relaxed);
          return std::nullopt;  // Stolen by thief
        }
        bottom_.store(b + 1, std::memory_order_relaxed);
      }
      return std::move(buffer_[b & kMask]);
    } else {
      bottom_.store(b + 1, std::memory_order_relaxed);
      return std::nullopt;
    }
  }

  // Thief steals from top (lock-free CAS)
  std::optional<TaskType> Steal() {
    std::int64_t t = top_.load(std::memory_order_acquire);
    std::atomic_thread_fence(std::memory_order_seq_cst);
    std::int64_t const b = bottom_.load(std::memory_order_acquire);
    if (t < b) {
      TaskType task = buffer_[t & kMask];
      if (top_.compare_exchange_strong(
              t, t + 1, std::memory_order_seq_cst, std::memory_order_relaxed)) {
        return task;
      }
    }
    return std::nullopt;
  }

  std::int64_t size() const {
    std::int64_t const b = bottom_.load(std::memory_order_relaxed);
    std::int64_t const t = top_.load(std::memory_order_relaxed);
    return (b >= t) ? (b - t) : 0;
  }

 private:
  std::unique_ptr<TaskType[]> buffer_;
  alignas(64) std::atomic<std::int64_t> top_{0};
  alignas(64) std::atomic<std::int64_t> bottom_{0};
};

// Work-Stealing Thread Pool with Chase-Lev Deques and P-Core / E-Core Affinity.
template<typename T>
class WorkStealingThreadPool final {
 public:
  struct Call {
    std::function<T()> function;
    std::promise<T> promise;
  };
  using TaskPtr = std::shared_ptr<Call>;

  explicit WorkStealingThreadPool(std::int64_t const pool_size) {
    topology_ = DetectProcessorCoreTopology();

    std::int64_t const count = std::max<std::int64_t>(1, pool_size);
    if (topology_.is_hybrid && topology_.p_core_count > 0 && topology_.e_core_count > 0) {
      // Partition threads proportionally between P-cores and E-cores
      double const p_ratio = static_cast<double>(topology_.p_core_count) /
                             (topology_.p_core_count + topology_.e_core_count);
      p_worker_count_ = std::clamp<int>(
          static_cast<int>(std::round(count * p_ratio)), 1, static_cast<int>(count) - 1);
      e_worker_count_ = static_cast<int>(count) - p_worker_count_;
    } else {
      p_worker_count_ = static_cast<int>(count);
      e_worker_count_ = 0;
    }

    workers_.resize(count);
    for (int i = 0; i < count; ++i) {
      workers_[i] = std::make_unique<Worker>();
      workers_[i]->index = i;
      workers_[i]->is_p_core = (i < p_worker_count_);
    }

    for (int i = 0; i < count; ++i) {
      threads_.emplace_back([this, i]() {
        WorkerThreadLoop(i);
      });

#if OS_WIN
      // Bind worker thread affinity to physical P-cores or E-cores
      if (topology_.is_hybrid) {
        std::uint64_t const mask = (i < p_worker_count_)
                                       ? topology_.p_core_mask
                                       : topology_.e_core_mask;
        if (mask != 0) {
          SetThreadAffinityMask(threads_.back().native_handle(),
                                static_cast<DWORD_PTR>(mask));
        }
      }
#endif
    }
  }

  ~WorkStealingThreadPool() {
    {
      absl::MutexLock l(&lock_);
      shutdown_ = true;
    }
    for (auto& thread : threads_) {
      if (thread.joinable()) {
        thread.join();
      }
    }
  }

  // Adds a task with core preference:
  // prefer_p_core = true: Pushed to high-performance P-Core worker deque.
  // prefer_p_core = false: Pushed to energy-efficient E-Core worker deque.
  std::future<T> AddWithAffinity(std::function<T()> function, bool const prefer_p_core) {
    auto call = std::make_shared<Call>();
    call->function = std::move(function);
    std::future<T> future = call->promise.get_future();

    bool pushed = false;
    int const start_idx = prefer_p_core ? 0 : p_worker_count_;
    int const range = prefer_p_core ? p_worker_count_ : e_worker_count_;

    if (range > 0) {
      // Round-robin dispatch into target worker deque
      int const target = start_idx + (dispatch_counter_.fetch_add(1, std::memory_order_relaxed) % range);
      pushed = workers_[target]->deque.Push(call);
    }

    if (!pushed) {
      // Fallback to global overflow queue
      absl::MutexLock l(&lock_);
      overflow_calls_.push_back(std::move(call));
    }

    return future;
  }

  // Compatible default Add API: distributes across P-Cores by default
  std::future<T> Add(std::function<T()> function) {
    return AddWithAffinity(std::move(function), /*prefer_p_core=*/true);
  }

  std::optional<std::future<T>> TryAdd(std::function<T()> function) {
    std::int64_t const idle = static_cast<std::int64_t>(threads_.size()) - busy_threads_.load(std::memory_order_relaxed);
    if (idle <= 0) {
      return std::nullopt;
    }
    return Add(std::move(function));
  }

  bool WaitUntilIdleFor(absl::Duration const duration) {
    absl::ReaderMutexLock l(&lock_);
    auto const is_idle = [this]() {
      return busy_threads_.load(std::memory_order_relaxed) == 0 &&
             overflow_calls_.empty();
    };
    return lock_.AwaitWithTimeout(absl::Condition(&is_idle), duration);
  }

  std::int64_t pool_size() const {
    return static_cast<std::int64_t>(threads_.size());
  }

  std::int64_t busy_threads() const {
    return busy_threads_.load(std::memory_order_relaxed);
  }

  std::int64_t pending_tasks() const {
    std::int64_t count = 0;
    for (auto const& worker : workers_) {
      if (worker != nullptr) {
        count += worker->deque.size();
      }
    }
    absl::ReaderMutexLock l(&lock_);
    count += overflow_calls_.size();
    return count;
  }

  std::int64_t stolen_tasks() const {
    return stolen_tasks_.load(std::memory_order_relaxed);
  }

  int p_core_workers() const { return p_worker_count_; }
  int e_core_workers() const { return e_worker_count_; }

 private:
  struct Worker {
    int index{0};
    bool is_p_core{true};
    ChaseLevDeque<TaskPtr> deque;
  };

  void WorkerThreadLoop(int const worker_index) {
    auto& local_worker = *workers_[worker_index];
    int const total_workers = static_cast<int>(workers_.size());

    while (true) {
      TaskPtr task = nullptr;

      // 1. Try local deque (LIFO for highest L1/L2 cache locality)
      auto popped = local_worker.deque.Pop();
      if (popped.has_value()) {
        task = std::move(*popped);
      }

      // 2. Try global overflow queue
      if (task == nullptr) {
        absl::MutexLock l(&lock_);
        if (shutdown_) {
          return;
        }
        if (!overflow_calls_.empty()) {
          task = std::move(overflow_calls_.front());
          overflow_calls_.pop_front();
        }
      }

      // 3. Work-Stealing: Steal from other workers (FIFO for fairness)
      if (task == nullptr && total_workers > 1) {
        int victim_offset = 1;
        for (int i = 0; i < total_workers - 1; ++i) {
          int const victim_idx = (worker_index + victim_offset++) % total_workers;
          auto stolen = workers_[victim_idx]->deque.Steal();
          if (stolen.has_value()) {
            task = std::move(*stolen);
            stolen_tasks_.fetch_add(1, std::memory_order_relaxed);
            break;
          }
        }
      }

      // 4. Execute acquired task
      if (task != nullptr) {
        busy_threads_.fetch_add(1, std::memory_order_relaxed);
        ExecuteAndSetValue(task->function, task->promise);
        busy_threads_.fetch_sub(1, std::memory_order_relaxed);
      } else {
        // Sleep briefly to yield CPU when queues are completely drained
        absl::SleepFor(absl::Microseconds(100));
        absl::ReaderMutexLock l(&lock_);
        if (shutdown_) {
          return;
        }
      }
    }
  }

  ProcessorCoreTopology topology_{};
  int p_worker_count_{0};
  int e_worker_count_{0};

  std::vector<std::unique_ptr<Worker>> workers_;
  std::vector<std::thread> threads_;

  std::atomic<std::int64_t> dispatch_counter_{0};
  std::atomic<std::int64_t> busy_threads_{0};
  std::atomic<std::int64_t> stolen_tasks_{0};

  mutable absl::Mutex lock_;
  bool shutdown_ ABSL_GUARDED_BY(lock_) = false;
  std::deque<TaskPtr> overflow_calls_ ABSL_GUARDED_BY(lock_);
};

}  // namespace internal

using internal::ProcessorCoreTopology;
using internal::DetectProcessorCoreTopology;
using internal::ChaseLevDeque;
using internal::WorkStealingThreadPool;

}  // namespace _work_stealing_thread_pool
}  // namespace base
}  // namespace principia
