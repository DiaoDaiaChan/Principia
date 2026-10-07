# Principia GPGPU 与多级缓存管线落地实施追踪表 (Implementation Tracker)

## (GPGPU Low-Precision & Multi-Level Caching Pipeline Tracker)

最后更新时间: 2026-10-07
当前开发分支: `feature/multi-level-caching-pipeline`（领先 `master` 30 commits）

> **⚠️ 2026-10-07 状态校准说明**
> 本表在 2026-10-05/06 期间的旧版本曾将"猪排图扫描、DEM 碰撞、球谐体素、日食 SRP、碎片对撞、幅宽覆盖、气动截面"等 8 个模块标记为"已完成"。
> 这些模块的 C# 封装与 Compute Shader 已在清理提交 **`e4163820f`（chore(cleanup): remove unused compute shaders…）** 中**整体移除**。
> 本表已按当前代码树的真实状态重写。被移除模块的记录见第四节，供后续评估是否重启。

---

### 一、 核心铁律与架构规范 (Core Architectural Directives)

1. **原生双精度绝对保真 (Native Double Precision FP64 Preservation)**:
   - 游戏内 GUI（`MainWindow`、`BurnEditor`、`GpuDiagnosticsWindow` 等）必须无条件保留**原生双精度**选项。
   - 当模式为 `ComputationPipelineMode.NativeDoublePrecision` 时，**底层 C++ 经典实现与原生管线 100% 保持不动**，不得拦截、不得修改、不得派发 GPU。诊断中心将所有 GPU 模块以绿色 `[FP64 旁路]` 标识，保持 0 显存侵入、0 GPU 开销。
2. **显式切换与无缝热切换 (Explicit Gating & Hot Swapping)**:
   - 仅当用户显式选择 `ComputationPipelineMode.GpuAccelerated` 时，系统才接入 GPGPU 异构加速新管线。
   - 动态调度总督 `HeterogeneousDynamicGovernor` 执行回压与帧率控制；若检测到显卡过载或不支持，优雅安全降级。
3. **零 GC 与零等待回读 (Zero GC & Asynchronous Readback)**:
   - 所有 GPU 交互采用预分配 ComputeBuffer 与 RenderTexture，杜绝一帧一配。
   - 采用 `AsyncGPUReadback` 消除主线程阻塞等待。
4. **统筹监控与全模块透明感知 (Unified Telemetry & Diagnostics)**:
   - 集中监控中心（`GpuDiagnosticsWindow` + `GpuModuleTelemetry`）统一统筹 GPU 计算模块的调用次数、单次耗时、平均耗时、显存资源与就绪状态，支持独立单项压测与一键全模块综合压测。

---

### 二、 当前落实现状快照 (Current Reality Snapshot)

> 以 `GpuModuleTelemetry.cs` 注册表与 `tools/BuildPrincipiaGpuAssetBundle.cs` 打包清单为准。

| 项 | 当前状态 |
| :--- | :--- |
| **已注册 GPU 模块数** | **3**（`GpuModuleId`: FlightPlanOptimizer / TrajectoryTessellator / BatchOrbitIntegrator） |
| **C# 封装文件** | `GpuFlightPlanOptimizer.cs`、`GpuFlightPlanShootingOptimizer.cs`、`GpuTrajectoryRenderer.cs`、`GpuVirtualProjection.cs` |
| **着色器资产（5）** | `FlightPlanShootingOptimizer.compute`、`BatchOrbitIntegrator.compute`、`TrajectoryTessellator.compute`、`TrajectoryProcedural.shader`（以上 4 个进 AssetBundle）、`DoubleSingle.hlsl`（被 include 的公共库） |
| **AssetBundle** | `principia_gpu.bundle`，含上述 4 个着色器 |
| **上下文管理** | `PrincipiaGPGPUContext.cs`：bundle 加载 + `Shader.Find` 兜底 |
| **诊断中心** | `gpu_diagnostics_window.cs`：模块遥测表、单项/全模块压测、Phase 5 熔断器实时面板 |

---

### 三、 在役模块任务清单 (Active Module Checklist)

| 序号 | 模块名称与目标 | 核心着色器 / C# 文件 | 当前状态 | 验证标准 |
| :---: | :--- | :--- | :---: | :--- |
| **0** | **机动规划 16K 并行打靶优化器（Phase 6 重构版）** | `shaders/FlightPlanShootingOptimizer.compute`<br/>`GpuFlightPlanShootingOptimizer.cs`<br/>`GpuFlightPlanOptimizer.cs`<br/>`burn_editor.cs` | <font color="lime">✔ [已完成]</font> | 16,384 轨道并行打靶演化与两级规约，异步回读写入 BurnEditor，原生模式安全旁路。 |
| **7** | **切比雪夫/Hermite 流形密集求值与投射** | `shaders/TrajectoryTessellator.compute`<br/>`shaders/TrajectoryProcedural.shader`<br/>`GpuTrajectoryRenderer.cs`<br/>`plotter.cs` | <font color="lime">✔ [已完成]</font> | GPU 硬件 Clenshaw 递推直接展开切比雪夫多项式，与 GpuTrajectoryRenderer 间接无拷贝绘制打通。 |
| **11** | **批量轨道积分管线（Phase 6）** | `shaders/BatchOrbitIntegrator.compute`<br/>`GpuModuleTelemetry.cs` | <font color="lime">✔ [已完成]</font> | 多轨道批传播 GPU 流水线，遥测注册与压测入口齐备。 |

---

### 四、 已移除模块档案 (Removed Modules Archive)

以下模块曾于 2026-10-05 里程碑 1~8 期间落地，后经审计确认存在硬编码、mock 遥测与物理失真问题，在 **`e4163820f`（2026-10-06）** 中整体移除（-2,617 行）。相关审计结论沉淀于 `Principia_GPGPU_Native_Acceleration_Specification.md`，若重启须按新规格重写：

| 原序号 | 模块 | 已删除文件 | 移除原因摘要 | 新规格章节 |
| :---: | :--- | :--- | :--- | :--- |
| 1 | 猪排图 Lambert 扫描 | `PorkchopLambertScanner.compute` 等 | 物理口径未对齐原生实现 | OPT_DIFF 关联 |
| 2 | 地表 DEM 光线步进碰撞 | `TerrainCollisionRaymarcher.compute` 等 | 高程数据路径与原生 PushPullExecutor 不一致 | — |
| 3 | 高阶球谐引力场体素 | `HighDegreeGeopotentialGrid.compute` 等 | 递推基与 C++ `GeneralSphericalHarmonicsAcceleration` 未对齐 | GEOPOT_REC（未落地） |
| 4 | 日食遮挡与 SRP 光压 | `SolarEclipseOcclusion.compute` 等 | 恒星光度与视圆盘求积失真 | — |
| 5 | 碎片全域对撞初筛 | `AllPairsConjunctionBroadphase.compute` 等 | 无真实数据源接入 | — |
| 6 | 全球地表覆盖度统计 | `GroundTrackSwathAccumulator.compute` 等 | 无真实数据源接入 | ORBIT_ANAL（未落地） |
| 8 | 迎角受风截面统计 | `AerodynamicAreaReduction.compute` 等 | 微光栅化原子越界等工程缺陷 | — |
| — | WSB 低能转移束 / 等势面 Marching Cubes | `WSBBundleEvaluator.compute`、`EquipotentialMarchingCubes.compute` | 未接通真实调用链 | EQUIPOT_2D（未落地） |

> 上述模块在本追踪表旧版本中的"已完成"记录**作废**。`Principia_Low_Precision_GPGPU_Architecture_Audit.md` 与 `Principia_GPU_Implementation_Code_Audit.md` 中对这些文件的描述为历史快照，仅供考古。

---

### 五、 已知问题与待办 (Known Issues & Backlog)

1. **"0 警告 0 错误"声明的上下文**：里程碑 10 基线（`614b2ccd5`）构建通过后，实际又出现了 5 个运行时崩溃（见第七节修复记录）。**构建零告警 ≠ 运行时稳定**，后续里程碑的验收应附 KSP 实机冒烟测试结论，而非仅编译结果。
2. **`ScaledSpaceDisplacement.shader` 死资产**：无任何 C# 引用、不在 AssetBundle 打包列表。Phase 4 的 `GpuVirtualProjection.cs` 只设置 `_WorldOffset`/`_VirtualDisplacement` 材质属性，真正消费这些属性的是 `TrajectoryProcedural.shader`（轨迹线）。若要给 ScaledSpace 天体球启用顶点偏移，需将其加入打包并替换天体材质；否则应删除。（已于 2026-10-07 移除，见第八节）
3. **`deploy_to_ksp.ps1` 个人路径**：候选列表中曾含 `"Kerbal Space Program_newmod"` 私有 Steam 路径。（已于 2026-10-07 清理）
4. 提交 `ff126fe6d`（Phase 5）的 message 含字面 `\n` 而非真实换行，历史已深处，不做 rebase 重写，仅记录警示：**多行提交信息必须用真实换行**。

---

### 六、 里程碑总览 (Milestone Log)

> 完整细节见各 commit body 与 `plan/` 目录下的分阶段计划书。

* **2026-10-05**
  - `ffd3da904` 多级缓存架构与无缝滚动星历管线（L2/L3，`rolling_ephemeris_driver`、`sidecar_storage` 首版）。
  - `52823a038` GPGPU 异构加速管线、compute shader 家族与 `HeterogeneousDynamicGovernor` 首版（含后来被移除的 8 模块）。
  - `2c6704ad2` Stage 3 Phase 1 天体与轨迹 GPU 解耦、分段收获。
  - `614b2ccd5` **里程碑 10**：硬编码与魔法数字深度清洗（动态 gravParameter、Clairaut 扁率、动态缓冲容量等），基线固化。
* **2026-10-06**
  - `a046da838` `TrajectoryProcedural.shader` 原生程序化线材质管线。
  - `8671d4cb9` 预测轨迹缓存修复（geeForce 对齐 + 多秒 coasting mesh 复用）。
  - `e4163820f` **冗余模块清理**：移除 8 个未达标模块与 9 个 compute shader，重写 `GpuModuleTelemetry` 为 3 模块注册表，沉淀 `Principia_GPGPU_Native_Acceleration_Specification.md`。
  - `2250ee0b4` loader 支持可选原生符号与预编译二进制安全兜底。
  - `55223397e` **Phase 2**（实施编号）：无锁三缓冲状态总线 + SPSC 命令队列（`ksp_plugin/triple_buffer_channel.hpp`、`spsc_command_buffer.hpp`、`TripleBufferStateBus.cs`）。
  - `3cc9ccf9d` **Phase 3**（实施编号）：批量状态同步 + 双时钟物理桥（`BatchStateSync.cs`、`DualClockBridge.cs`、`interface_batch.cpp`）。
  - `31e9a72f0` **Phase 4**（实施编号）：场景图解耦 + GPU 虚拟投影（`SceneGraphDecoupler.cs`、`GpuVirtualProjection.cs`、`SmoothPlottingBridge.cs`）。
  - `ff126fe6d` **Phase 5**（实施编号）：物理死循环熔断器 + 零分配力缓存（`FrameBudgetCircuitBreaker.cs`、`ZeroAllocationForceCache.cs`，接入完整 Unity 生命周期钩子与诊断面板）。
  - `caa3cb2c3` **Phase 6**（实施编号）：GPGPU 并行打靶飞行计划优化器 + 批量轨道管线（`GpuFlightPlanShootingOptimizer.cs` 重构，594 行新增）。
* **2026-10-06 ~ 10-07**（稳定性修复，详见第七节）
  - `b5053b882`、`44bbc8e97`、`803c94edb`、`e812c2a0b`、`b540a29be`。

---

### 七、 运行时崩溃修复记录 (Crash-Fix Log)

> 里程碑 10 之后连续 5 个提交均为实机崩溃修复，印证第五节 #1 的警示。

| Commit | 症状 | 根因与修复 |
| :--- | :--- | :--- |
| `b5053b882` | 从跟踪站进入飞行场景崩溃 | `DensePolynomialSlice` 边界缺陷 + 熔断器状态污染；补齐空态校验与生命周期重置 |
| `44bbc8e97` | 存档保存时活动 sidecar 被误删，加载即丢缓存 | 保存前修剪逻辑误伤活动文件；加载路径损坏时优雅恢复而非崩溃 |
| `803c94edb` | sidecar base64 反序列化损坏 | 读写编解码不对称；顺带落地 `CelestialTrajectoryBinaryCache` 天体位置连续缓存 |
| `e812c2a0b` | `RollingEphemerisDriver` 构造函数无限递归栈溢出 | 移除自引用构造路径 |
| `b540a29be` | `EphemerisAnchor` 崩溃、standby carrier 选择异常 | 重构锚点生命周期，恢复干净待机载波选择 |

---

### 八、 2026-10-07 文档与工程卫生批次 (Docs & Hygiene Pass)

- 重写本追踪表：与代码树对齐（3 模块 + 4+1 着色器），补录 Phase 2~6 与崩溃修复记录。
- 在 `Principia_Long_Term_Performance_Architecture_Roadmap.md` 中声明 Roadmap 编号与实施编号的映射（消除两套 "Phase N" 歧义）。
- `Principia_GPGPU_Native_Acceleration_Specification.md` 实施矩阵增加"落地状态"列。
- 新建 `documentation/README.md` 文档索引，确立单一事实来源；移除 `fix/` 下 6 份与 `documentation/` 完全重复的文档副本。
- 删除死资产 `shaders/ScaledSpaceDisplacement.shader`（无引用、未打包）。
- 清理 `scripts/deploy_to_ksp.ps1` 个人 Steam 路径。

---

### 九、 2026-10-07 Precalc 性能优化批次 (Probe-Driven Performance Pass)

**触发数据**（实机探针，平滑值）：Precalc 0.98ms（峰值 1.5）为最大单项 | FixedUpdate 0.02 | WaitedForFixed 0.61 | BetterLate 0.45 | LateUpdate 0.13。预期"星历有缓存应接近零"，实测不符，逐层定位出两个根因：

1. **`Ephemeris::Prolong` 无条件独占写锁（C++ 根因，`physics/ephemeris_body.hpp`）**：
   即使后台滚动星历（载体飞行计划）已覆盖当前时刻、无需积分，主线程 `AdvanceTime → Prolong` 仍要拿写锁排队，后台积分持锁期间主线程被阻塞。
   **修复**：新增读锁快速路径——`t_max ≥ t` 时直接返回 `OkStatus`（t_max 单调不减，单次读锁校验安全；行为与原 while 循环空转严格等价）。
2. **`Precalc` 阻塞收割与 staged harvesting 设计矛盾（C#，`ksp_plugin_adapter.cs`）**：
   本分支在 Precalc 中加入的 `HarvestVesselsAndApplyOnRails(non_blocking: false)`（上游无此调用）会在物理线程上阻塞等待上一帧残留 futures；而 staged harvesting 的设计意图（见 `Early()` 注释）是收割全部推迟到 BetterLateThanNever (+8008) 非阻塞执行。
   **修复**：`EnableStagedHarvesting == true` 时 Precalc 收割改为非阻塞，残留 futures 由同帧 BetterLate 阶段收割；关闭 staged harvesting 时保留原阻塞语义作为兜底。
3. **探针细分（`performance_probe.cs`）**：新增 `VesselHarvest`（收割，含 ApplyToVesselsOnRails）与 `BodyFramesUpdate`（SetBodyFrames）两个独立桶；`PhysicsAdvance` 现在只测 `AdvanceTime` 本身，HUD 核心子系统区新增两行。**下次实测即可精确定位 Precalc 剩余开销构成。**
4. **顺带**：`ksp_plugin_adapter.csproj` 中 `KSPManaged` 兜底链的 `Kerbal Space Program_newmod` 个人路径替换为通用路径（已核实两处安装的 Assembly-CSharp.dll 字节级一致，构建不受影响）。

> **验证状态**：C# 侧改动已于 2026-10-07 通过 `dotnet build -c Release`（0 警告 0 错误，见第十节）；C++ 侧 `ephemeris_body.hpp` 的读锁快速路径仍为人工复核，需随原生库一并构建。实机复测：Precalc 中 `AdvanceTime` **已确认降至 0.01 ms**（见第十节第二轮探针），on-rails 船舶位置需继续观察。

---

### 十、 2026-10-07 轨迹绘制开销定位与递归退化修复 (Trajectory Plotting Pass)

**第二轮探针数据**（实机，平滑 / 峰值）：

| 桶 | 平滑 | 峰值 | 备注 |
| :--- | ---: | ---: | :--- |
| 物理推进 (AdvanceTime) | **0.01 ms** | 0.0 | 第九节根因 1 修复完全生效（原 0.98 ms） |
| 载体收割 (Harvest) | 0.44 ms | 0.6 | 非阻塞收割后的纯工作量 |
| 天体框架 (BodyFrames) | 0.57 ms | 1.0 | `SetBodyFrames`（已有 LOD + 帧门控） |
| **轨迹绘制 (Plotting)** | **2.89 ms** | 4.0 | **新的头号开销** |
| 网格细分 (Mesh Gen) | 0.00 ms | 0.0 | — |
| GPU 绘制直出 | 0.00 ms | 0.0 | — |
| Unity 生命周期 | Precalc 1.46 / WaitedForFixed 0.69 / BetterLate 0.58 / LateUpdate 0.34（峰值 **7.6**） | | |

**分析**：`MeshGeneration` 与 `GpuCompute` 两桶均为 0，而它们只覆盖 `UpdateMesh`（原生缓冲 → `mesh.vertices`）与 `Draw`（`Graphics.DrawMesh`）——说明网格生成与绘制几乎不耗时。`TrajectoryPlotting` 桶还包含 C++ 侧 `PlanetariumPlot*` 的 P/Invoke 重算，这是 2.89 ms 的主要候选。

**本轮改动**：

1. **探针再细分**：新增 `CelestialPlot`（天体轨迹）、`VesselPlot`（飞船轨迹）、`EquipotentialPlot`（等势线）三个子桶，把 2.89 ms 拆到可归因粒度（HUD 核心子系统区缩进三行）。
2. **失效计数器**：`PlotTrajectories` 开头的 `ScaledToLocalSpace` 位移检测一旦越过阈值即调用 `InvalidateAll()`，会强制下一帧对**全部**缓存网格经 C++ 重算。新增 `PerformanceProbe.PlotInvalidations` 累计计数，并在 HUD「3D 网格多级缓存」行显示。**若该值以约"每帧 1 次"的速率攀升，说明缓存从未生效、多级缓存被完全绕过**（当前仅诊断，未改阈值，以免破坏 FloatingOrigin 重置的正确性）。
3. **消除递归退化（确定性优化）**：`PlotSubtreeTrajectories` 原先在**每个天体的递归访问**中重新计算 `FrameParameters()`、`GetUniversalTime()`、`is_time_advancing`、`realtime` 与 `frame_type`。其中 `FrameParameters()` 返回 `ReferenceFrameParameters`，再经 `interface.cs` 的隐式转换构造 `FrameKey` 所需的 `PlottingFrameParameters`——**每次转换分配 1 个对象 + 2 个 `int[]`**，即每帧每天体约 4 次分配。现全部逐帧不变量提升至 `PlotCelestialTrajectories` 顶层只算一次并递归传参；`show_celestial_trajectory(root)` 也改为每层只求值一次（原为 2 次）。

**验证**：`dotnet build -c Release -p:PreBuildEvent=` **编译通过，0 警告 0 错误**（`-p:PreBuildEvent=` 用于绕过沙箱中会失败的 PowerShell 前置生成步骤；`Properties/AssemblyInfo.cs` 已存在，故不影响）。

> **下一步（需实机复跑探针）**：① 若 `CelestialPlot` 主导 → 属 C++ 细分密度问题，可调 `RenderTrajectories` 的 `resolution_multiplier`（当前 0.75）；② 若 `VesselPlot` 主导且失效计数每帧攀升 → 修正 `PlotTrajectories` 的失效检测阈值/条件。
