# Principia 阶段三第三阶段：批量状态同步管线与异步双时钟物理桥接实施规划
*(Phase 3: Batch State Synchronization & Autonomous Physics Worker Bridge Implementation Plan)*

## 一、阶段三目标与定位 (Mission & Objectives)

在 Phase 1 与 Phase 2 中，我们分别实现了：
1. **显存直通零拷贝管线与非阻塞异步收割回退机制**（切断轨迹数据拷贝，解决 `Precalc -> Early` 阻塞）；
2. **无锁三缓冲状态总线与 SPSC 异步控制命令环形队列**（切断渲染与物理时钟强绑定，实现纳秒级 Horner 代数采样求值）。

**Phase 3（阶段三）的核心使命**：
> **攻克 Principia 运行生命周期中耗时最大、开销最密集的两大痛点：**
> 1. **`WaitedForFixedUpdate` 阶段的部件级 P/Invoke 调用风暴（单物理步 2,500 ~ 5,000 次跨语言跳转，严重消耗 CPU 缓存与调用栈）；**
> 2. **反海怪（Krakensbane / FloatingOrigin）坐标系瞬移引发的异步双时钟坐标撕裂与微抖动。**
>
> 通过构建**零堆分配紧凑批量状态同步管线（Batch State Synchronization Pipeline）**，将单帧数千次跨语言跳转骤降为 $\le 5$ 次批量原生调用；并通过**原点代数纪元（Origin Epoch Sequence）与微位移漂移补偿桥接（Dual-Clock Bridge）**，实现异步连续状态总线与 Unity 场景图的无缝高精度对齐。

---

## 二、架构设计与核心组件 (Architecture & Technical Blueprint)

### 2.1 紧凑对齐批量部件状态结构与接口 (`BatchStateSync`)
设计 64 位对齐的紧凑内存结构，支持单次跨语言批量传输数百个部件的动力学自由度、力、力矩与惯性张量：

1. **`PartBatchInsertLoadedState` (加载部件批量插入状态)**:
   - 部件 ID (`uint32_t part_id`)
   - 固推标志与力学标志 (`uint32_t flags`)
   - 部件质量 (`double mass_in_tonnes`)
   - 质心坐标 (`XYZ center_of_mass`)
   - 转动惯量主轴与四元数 (`XYZ moments_of_inertia`, `WXYZ principal_axes_rotation`)
   - 部件自由度 (`QP part_world_dof`)
   - 部件姿态四元数与角速度 (`WXYZ part_rotation`, `XYZ part_angular_velocity`)
   - 固有合外力与外力矩 (`XYZ intrinsic_force`, `XYZ intrinsic_torque`)
   - 作用点外力 (`XYZ intrinsic_force_at_lever`, `XYZ lever_arm`)
   - 飞船 GUID 哈希/索引、主天体索引 (`int32_t main_body_index`, `int32_t vessel_index`)
   - 步长 $\Delta t$

2. **`PartBatchApparentMotion` (表观刚体运动批量设置)**:
   - `uint32_t part_id`
   - `QP degrees_of_freedom` (位置与速度)
   - `WXYZ rotation` (四元数)
   - `XYZ angular_velocity` (角速度)

3. **`PartBatchActualMotion` (真实刚体运动批量获取)**:
   - 输入：`uint32_t part_id`
   - 输出：`QPRW actual_motion` (位置、速度、旋转、角速度)

### 2.2 托管端零 GC 内存池 (`NativeBatchBufferPool`)
- 预分配固定容量（例如 1024 部件）的非托管内存块或固定缓冲区（Pinned Buffer）。
- 在每次物理步重用该缓冲区，消除 Mono GC 分配（0 字节/物理步）。
- 统计每物理步节省的 P/Invoke 次数（单步达 1,500 ~ 4,000 次），上报遥测监控。

### 2.3 异步双时钟原点纪元与坐标漂移补偿桥接 (`DualClockBridge`)
- **Krakensbane / FloatingOrigin 漂移机理**：
  - KSP 在飞行器高速移动或大尺度机动时，会突发调用 `FloatingOrigin.SetOffset` 或更新 `krakensbane.FrameVel`。
- **原点纪元序列号 (`OriginEpochSequence`)**：
  - 每当检测到原点平移事件，`origin_epoch` 递增。
  - 记录瞬时坐标漂移补偿矢量 $\mathbf{X}_{\text{drift}} = \sum \Delta \mathbf{x}_{\text{floating}} + \int \mathbf{v}_{\text{frame}} dt$。
  - 异步采样时校验纪元版本，自动叠加微位移差分补偿，彻底杜绝坐标撕裂与飞船视觉瞬移。

### 2.4 物理断路器与自适应外推器 (`PhysicsCircuitBreaker` 增强)
- 结合 Phase 2 的 `TripleBufferStateBus` 与 `DensePolynomialSlice`：
  - 当物理帧用时超过预算（$\ge 15\text{ ms}$）或堆积固定步数超标时，触发断路器截断非活跃载具的物理开销。
  - 从无锁三缓冲状态总线中执行代数 Horner 采样快速外推，维持视觉 60/144 FPS 平滑输出，根除物理死循环卡死。

### 2.5 三级韧性防御与平滑回退 (Resilient Fallback Matrix)
- 保持对官方发布原生二进制 `principia.dll`（173 导出函数）的 100% 动态兼容：
  - 检测批量状态同步原生导出符号是否存在 (`principia__BatchInsertOrKeepLoadedParts`, `principia__BatchPartSetApparentRigidMotion`, `principia__BatchPartGetActualRigidMotion`)。
  - 若原生库导出，则走单次 P/Invoke 原生批量写入路径（99.8% 调用压降）；
  - 若原生库缺失，由托管端 `BatchStateSync` 自动循环分发到原有各基础 P/Invoke，无缝兼容，平稳运行且 0 崩溃。

---

## 三、文件修改与新建清单 (File Manifest)

### 3.1 C++ 原生层 (Native C++)
1. `ksp_plugin/batch_state_sync.hpp` (新建)
   - 紧凑内存对齐批量部件状态定义与批量执行核心逻辑。
2. `ksp_plugin/interface_batch.cpp` (新建)
   - 导出批量插入、批量表观运动、批量真实运动 C 风格 ABI 接口。

### 3.2 C# 托管层 (C# Adapter)
1. `ksp_plugin_adapter/BatchStateSync.cs` (新建)
   - 紧凑批量状态结构体定义（StructLayout Sequential）、非托管预分配缓冲池、批量同步分发器及安全回退。
2. `ksp_plugin_adapter/DualClockBridge.cs` (新建)
   - 异步双时钟推进桥接、FloatingOrigin / Krakensbane 坐标漂移补偿器与原点纪元跟踪器。
3. `ksp_plugin_adapter/interface.cs` (更新)
   - 增加 `HasBatchSyncSupport` 运行时能力检测特性。
4. `ksp_plugin_adapter/interface.generated.cs` (更新)
   - 注册阶段三可选批量导出函数委托与安全加载代理。
5. `ksp_plugin_adapter/ksp_plugin_adapter.cs` (更新)
   - 在 `WaitedForFixedUpdate` 中接入 `BatchStateSync` 批量同步管线与 `DualClockBridge` 原点纪元补偿。
6. `ksp_plugin_adapter/gpu_diagnostics_window.cs` (更新)
   - 接入 Phase 3 批量同步遥测指标（单步 P/Invoke 节省数、批量部件吞吐量、原点纪元偏移、断路器外推状态）。

---

## 四、验证基准与交付标准 (Verification & Acceptance Criteria)
1. **编译构建**：C# `ksp_plugin_adapter.csproj` 编译通过，0 错误、0 警告。
2. **结构体布局验证**：在 .NET Framework 4.7.2 环境下反射验证结构体大小与内存对齐。
3. **符号韧性**：在纯官方原生环境（173 导出）与阶段三批量增强环境（新增导出）下均能安全稳定运行。
4. **部署同步**：运行 `scripts/deploy_to_ksp.ps1`，自动部署至 KSP GameData，哈希比对一致。
