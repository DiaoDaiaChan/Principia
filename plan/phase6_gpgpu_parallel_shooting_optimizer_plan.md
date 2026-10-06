# Principia Phase 6: GPGPU 并行打靶飞行计划优化器与批处理轨道流水线实施方案
*(Phase 6: GPGPU Parallel Shooting Flight Plan Optimizer & Batch Orbit Pipeline Plan)*

---

## 一、架构背景与痛点分析 (Executive Summary & Problem Statement)

### 1. 现有飞行计划优化器瓶颈
在现有的 Principia 实现中，飞行计划中的机动变轨优化（例如寻找奔月轨道最小近心点距离、轨道倾角调整或深空交会）由 C++ 核心的 `FlightPlanOptimizationDriver` 驱动。其底层采用单线程顺序的局部搜索算法（Nelder-Mead 单纯形法或共轭梯度下降）：
- **搜索维度大**：机动变轨优化是一个 4 维非凸优化问题（$\Delta t_0, \Delta v_{\text{tangent}}, \Delta v_{\text{normal}}, \Delta v_{\text{binormal}}$）。
- **极易陷入局部最优**：由于引力摄动和多天体引力势能的高非线性，局部优化器经常收敛到远离真实霍曼转移的伪极小值点，甚至在跨越天体引力影响范围（SOI）边缘时发生梯度震荡与发散。
- **CPU 耗时高与长时卡顿**：为了收敛，CPU 必须顺序积分成百上千次连续多体轨道，导致用户界面在点击“Optimize”后出现长达数秒的旋转等待，无法实现实时拖拽参数与交互式响应。

### 2. GPGPU 16,384 候选打靶与并行归约方案 (GPGPU Parallel Shooting & Reduction)
利用 GPU 强大的大规模并行 SIMD 计算能力与着色器 `shaders/FlightPlanShootingOptimizer.compute`：
1. **网格候选并行化 (Grid Generation)**：在单次调度中生成 16,384 个涵盖时间与三轴速度微扰的 4D 候选变轨参数网格。
2. **Double-Single 辛积分 (Forward Symplectic Integration)**：每个 GPU 线程独立对一条候选轨道执行 256~1024 步辛速度-Verlet 积分，使用双单精度（DoubleSingle，模拟 64 位双精度浮点）追踪多体引力与目标天体近心点距离。
3. **共享内存分级归约 (Parallel Reduction in Shared Memory)**：在 Group-Shared 内存中通过 2 次跨线程归约，以 $O(\log N)$ 纳秒级提取全局最小代价变轨参数。
4. **异步显存回读 (Async GPU Readback)**：通过 `AsyncGPUReadback` 在后台完成结果回读，完全不阻塞 Unity 渲染主线程。
5. **健壮多线程 CPU 回退 (Resilient Multi-Threaded CPU Fallback)**：当环境缺少 Compute Shader 支持或未加载 AssetBundle 时，平滑回退至托管端多线程网格求值，保证 100% 可用性。

---

## 二、架构拓扑与核心组件 (Architecture & Data Flow)

```mermaid
flowchart TD
    subgraph UI_Layer [用户界面与规划前端]
        BE[BurnEditor / FlightPlanner] -->|点击优化 / 参数变动| Bridge[GpuFlightPlanShootingOptimizer]
    end

    subgraph Dispatcher [GpuFlightPlanShootingOptimizer.cs]
        Bridge --> Check{GPU Compute 可用?}
        Check -->|是| GPU_Pipeline[GPU Compute Shader 流程]
        Check -->|否| CPU_Fallback[CPU 多线程并行回退]
        
        GPU_Pipeline --> SetParams[绑定常量与天体状态 StructuredBuffer]
        SetParams --> K1[Kernel 0: GenerateCandidateGrid (16,384 候选)]
        K1 --> K2[Kernel 1: EvaluateTrajectoryBundle (并行辛积分)]
        K2 --> K3[Kernel 2: ParallelReduceOptimalBurn (64 组内极小值)]
        K3 --> K4[Kernel 3: ParallelReduceOptimalBurnFinal (全局最优解)]
        K4 --> AsyncRead[AsyncGPUReadback / 零阻塞回读]
        
        CPU_Fallback --> ParallelFor[Parallel.For 多核网格求值]
    end

    AsyncRead --> Callback[优化完成回调: 注入最优 Delta-V]
    ParallelFor --> Callback
    Callback --> BE
```

---

## 三、显存缓冲区与结构对齐规范 (Buffer & Memory Layout)

### 1. 结构化缓冲区 (Structured Buffers)
1. **`_CelestialBodies`**：每个天体 32 字节 (16 字节对齐)
   - `float3 position` (12 字节)
   - `float gravitational_parameter` (4 字节, $GM$)
   - `float3 velocity` (12 字节)
   - `float padding` (4 字节)
2. **`_CandidateGrid`**：16,384 个元素 $\times$ 16 字节
   - `float delta_t, delta_v_tangent, delta_v_normal, delta_v_binormal`
3. **`_Evaluations`**：16,384 个元素 $\times$ 32 字节
   - `uint candidate_index` (4 字节)
   - `float periapsis_distance` (4 字节)
   - `float target_relative_speed` (4 字节)
   - `float cost_metric` (4 字节)
   - `float delta_t, delta_v_tangent, delta_v_normal, delta_v_binormal` (16 字节)
4. **`_ReducedResults`**：64 个元素 $\times$ 32 字节

---

## 四、实施计划步骤 (Implementation Steps)

1. **Step 1: 制定 Phase 6 实施方案** (`plan/phase6_gpgpu_parallel_shooting_optimizer_plan.md`)。
2. **Step 2: 实现核心调度器 `ksp_plugin_adapter/GpuFlightPlanShootingOptimizer.cs`**
   - 托管端缓冲池与常量缓冲打包。
   - 4 阶段 GPU Kernel 调度链路与异步回读。
   - 健壮的多线程 CPU 回退网格搜索。
3. **Step 3: 适配层与飞行规划器集成**
   - 在 `burn_editor.cs` 和 `flight_planner.cs` 中增加 GPU 极速打靶优化入口。
4. **Step 4: 遥测与诊断面板集成**
   - 在 `gpu_diagnostics_window.cs` 中加入 Phase 6 优化耗时、候选数、最优解指标及 GPU/CPU 模式标记。
5. **Step 5: 工程编译、NetFX 4.7.2 验证、部署与 Git 提交**
   - 更新 csproj，并通过 MSBuild 与本地脚本验证部署。
