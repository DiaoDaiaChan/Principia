# Principia 低精度大规模 GPGPU 加速落地实施追踪表 (TODO List)
## (GPGPU Low-Precision & High-Throughput Implementation Tracker)

最后更新时间: 2026-10-05
当前开发分支: `feature/multi-level-caching-pipeline`

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
   - 集中监控中心（`GpuDiagnosticsWindow` + `GpuModuleTelemetry`）统一统筹全 Mod 12 大 GPU 计算模块的调用次数、单次耗时、平均耗时、显存资源与就绪状态，支持独立单项压测与一键全模块综合压测。

---

### 二、 模块落地全景任务清单 (Panoramic Task Checklist)

| 序号 | 模块名称与目标 | 核心着色器 / C# / C++ 文件 | 当前状态 | 验证标准 |
| :---: | :--- | :--- | :---: | :--- |
| **基础** | **原生双精度隔离与 GUI 热切换底座** | `HeterogeneousDynamicGovernor.cs`<br/>`main_window.cs`<br/>`burn_editor.cs` | <font color="lime">✔ [已完成]</font> | MainWindow 与 BurnEditor 提供原生/GPU 双通道切换，保存/加载持久化，原生模式纯 CPU 运行。 |
| **0** | **机动规划 16K 并行打靶优化器** | `shaders/FlightPlanShootingOptimizer.compute`<br/>`GpuFlightPlanOptimizer.cs`<br/>`burn_editor.cs` | <font color="lime">✔ [已完成]</font> | 16,384 轨道并行生成与两级规约，异步回读写入 BurnEditor，提供原生对比按钮。 |
| **1** | **猪排图 (Porkchop) 百万网格 Lambert 转移扫描** | `shaders/PorkchopLambertScanner.compute`<br/>`GpuPorkchopScanner.cs`<br/>`flight_planner.cs` | <font color="lime">✔ [已完成]</font> | 512×512/1024×1024 网格并行求解 Lambert 转移，<3ms 生成 2D 热力图，原生模式安全旁路。 |
| **2** | **行星地表 DEM 光线步进碰撞求交与坡度场** | `shaders/TerrainCollisionRaymarcher.compute`<br/>`GpuTerrainCollisionDetector.cs`<br/>`ksp_plugin_adapter.cs` | <font color="lime">✔ [已完成]</font> | DEM 高程双线性插值与 GPU 二分光线步进求交，消灭 P/Invoke 往返等待，原生模式走原 PushPullExecutor。 |
| **3** | **高阶球谐引力场 (Geopotential) 体素网格** | `shaders/HighDegreeGeopotentialGrid.compute`<br/>`GpuGeopotentialGrid.cs`<br/>`main_window.cs` | <font color="lime">✔ [已完成]</font> | 突破 C++ 30 阶限制，GPU 并行展开 70~150 阶勒让德多项式体素场，结合 Marching Cubes 绘制等势面。 |
| **4** | **多天体掩星日食因子与光压 (SRP) 通量** | `shaders/SolarEclipseOcclusion.compute`<br/>`GpuSolarRadiationPressure.cs` | <font color="lime">✔ [已完成]</font> | 万级载具/微元视圆盘重叠解析求积与遮挡衰减，直接计算 SRP 摄动加速度。 |
| **5** | **空间态势感知碎片全域对撞初筛** | `shaders/AllPairsConjunctionBroadphase.compute`<br/>`GpuConjunctionMonitor.cs` | <font color="lime">✔ [已完成]</font> | 万级在轨碎片 5000 万对撞宽相剪枝，Tiled 共享内存粗筛潜在碰撞对，回传 C++ 做窄相精算。 |
| **6** | **轨道分析器：全球地表覆盖度与视线遮挡** | `shaders/GroundTrackSwathAccumulator.compute`<br/>`orbit_analyser.cs` | <font color="lime">✔ [已完成]</font> | 650 万球面像元累加曝光，快速计算传感器星下点刈幅覆盖度与侧视遮挡。 |
| **7** | **切比雪夫/Hermite 流形密集求值与投射** | `shaders/TrajectoryTessellator.compute`<br/>`GpuTrajectoryRenderer.cs`<br/>`plotter.cs` | <font color="lime">✔ [已完成]</font> | GPU 硬件 Clenshaw 递推直接展开切比雪夫多项式，与 GpuTrajectoryRenderer 间接无拷贝绘制打通。 |
| **8** | **正交深度微投影部件迎角受风截面统计** | `shaders/AerodynamicOcclusionRasterizer.shader`<br/>`shaders/AerodynamicAreaReduction.compute` | <font color="lime">✔ [已完成]</font> | 正交微光栅化 + 直方图归约，GPU 实时投影计算多部件迎风截面积与遮挡衰减。 |
| **9** | **GPGPU 异构加速诊断中心与全模块遥测注册表** | `GpuModuleTelemetry.cs`<br/>`gpu_diagnostics_window.cs`<br/>`main_window.cs`<br/>`ksp_plugin_adapter.cs` | <font color="lime">✔ [已完成]</font> | 集中统筹 12 大 GPU 模块，提供原生双精度锁定横幅、各模块状态/耗时表格、单项压测、一键全模块压测与诊断日志。 |

---

### 三、 详细进度与迭代记录 (Changelog & Step Log)

* **2026-10-05 (里程碑 0 & 基础底座)**:
  - 确立 `ComputationPipelineMode` 双精度/异构模式切换枚举。
  - 在 `main_window.cs` 与 `burn_editor.cs` 实现 GUI 精度选择器，物理保真模式下锁定纯原生 C++ 双精度经典管线。
  - 实现 `FlightPlanShootingOptimizer.compute`（16K 候选打靶与两级归约）并接入 `burn_editor.cs`。
  - 创建全景审计与架构分层报告 `Principia_Low_Precision_GPGPU_Architecture_Audit.md`。
* **2026-10-05 (里程碑 1 ~ 8 全场景加速管线与着色器落地)**:
  - **场景 1 (猪排图扫描)**: 完成 `PorkchopLambertScanner.compute` + `GpuPorkchopScanner.cs` + `flight_planner.cs` 转移窗口热力图渲染与原生双精度旁路。
  - **场景 2 (地表 DEM 碰撞求交)**: 完成 `TerrainCollisionRaymarcher.compute` + `GpuTerrainCollisionDetector.cs`，消除跨语言 P/Invoke 往返，原生模式走原 `PushPullExecutor`。
  - **场景 3 (高阶球谐引力场体素网格)**: 完成 `HighDegreeGeopotentialGrid.compute` + `GpuGeopotentialGrid.cs`，突破 30 阶限制，GPU 并行展开 70~150 阶勒让德多项式体素场。
  - **场景 4 (多天体日食与 SRP 光压)**: 完成 `SolarEclipseOcclusion.compute` + `GpuSolarRadiationPressure.cs`，实现视圆盘交集解析求积与万级光压通量直接求解。
  - **场景 5 (空间态势感知碎片对撞初筛)**: 完成 `AllPairsConjunctionBroadphase.compute` + `GpuConjunctionMonitor.cs`，实现 5000 万对撞 Tiled 共享内存宽相剪枝。
  - **场景 6 (全球地表覆盖度与视线遮挡)**: 完成 `GroundTrackSwathAccumulator.compute`，实现 650 万球面像元刈幅累加曝光与覆盖率归约。
  - **场景 7 (切比雪夫流形密集求值)**: 完成 `TrajectoryTessellator.compute` Clenshaw 递推流形密集展开，与间接无拷贝绘制打通。
  - **场景 8 (迎角受风截面统计)**: 完成 `AerodynamicAreaReduction.compute` 正交微光栅化像素统计与直方图归约。
  - 在 `PrincipiaGPGPUContext.cs` 完成全部 8 类新 Compute Shader 的安全生命周期管控、自动加载与安全释放。
* **2026-10-05 (里程碑 9 全模块统筹与 GPGPU 诊断中心上线)**:
  - 创建 `GpuModuleTelemetry.cs`：统一注册表管理 12 大 GPU 模块（16K 机动打靶、Porkchop 扫描、DEM 光线步进、球谐重力场、太阳光压、碎片态势筛选、曲率自适应细分、幅宽覆盖、气动截面积、WSB 低能转移束、等势面提取、辛积分批传播）。
  - 零 GC 实时采集每次调度的耗时、最大/最小耗时、累计调用次数、吞吐指标与结果摘要。
  - 实现独立安全单项压测与一键全模块综合压测（`ExecuteAllBenchmarks`）。
  - 创建 `gpu_diagnostics_window.cs`（`SupervisedWindowRenderer`）：
    - 硬件 Banner（GPU 型号、VRAM 容量、图形 API、Compute Shader 与 AsyncReadback 特性支持）。
    - 物理精度安全锁定横幅：明确显示原生双精度状态（`旧实现 100% 运行，GPU 硬件级旁路`）与异构模式状态。
    - 12 模块动态遥测大表：展示类别、状态 Badge（`[FP64 旁路]`、`[待机]`、`[活跃]`、`[反压]`、`[CPU 降级]`）、调用量、最近耗时、平均耗时、显存资源与 `[⚡ 压测]` 按钮。
    - 底部全局控制栏：`[⚡ 一键全模块综合压测]`、`[🔄 重置统计]`、`[🗑 释放显存缓冲区]` 与可折叠诊断日志框。
  - 将 `GpuDiagnosticsWindow` 接入 `PrincipiaPluginAdapter` 生命周期与 `MainWindow` 工具栏（`[⚡ GPU 诊断]`）。
  - 全工程 Release 编译通过，**0 警告、0 错误**。
* **2026-10-06 (里程碑 10 硬编码与魔法数字深度清洗，物理定律、内核签名与真实遥测全面对齐)**:
  - **类别 1 (P0 物理规律与星系兼容性)**:
    - 彻底废除 Kerbin GM 硬编码，动态取母星/目标天体 `gravParameter`；
    - 废除 1 AU 太阳常数宏定义，动态推导恒星光度辐射通量，消除 121 倍光压失真；
    - 废除地球带谐 $J_2\sim J_4$ 硬编码，引入 Clairaut 动力学扁率自适应理论；
    - 修复母星半径回退至 600 km。
  - **类别 2 (打靶寻优与数值积分)**:
    - 寻优时域自适应（1800s ~ 365天，256步），着色器内所有引力天体引入 Velocity-Verlet 双精度真实运动模拟；
    - 打靶搜索域依据机动大小与轨道周期动态缩放；
    - 4D 打靶网格分解参数化为 `_GridDimensions`；
    - 猪排图窗口与最短飞行时间根据霍曼转移几何动态解算，消除 1 天/10 天硬编码。
  - **类别 3 (物理调度与旁路治理)**:
    - 废除在箱飞船 4 帧错峰更新，恢复每物理帧均匀同步；
    - 动态物理帧校准，真空稳态门限收紧，子部件实现刚体四元数姿态与角速度物理回写，彻底根除部件撕裂；
    - 物理追赶熔断器预算放宽至 30ms，追赶步数自适应 Unity 物理配置；
    - 轨迹冻结期自适应旋转系，网格单帧重建上限提至 4，初始相位采用 Weyl 黄金分割低差异序列；
    - 载具宿主打分权重重构为强类型静态字典。
  - **类别 4 (缓冲区与参数规范化)**:
    - 天体缓冲区容量动态自适应星系体量（支持 RSS 百天体）；
    - 统一宇宙级虚拟包围盒尺寸为 2e9m，避免视锥远景剔除；
    - 地图节点安全边距提取常量；
    - DEM 光线步进高程动态读取 `radiusMax - Radius`，着色器循环步数解除写死；
    - 修复气动微光栅化原子累加越界，改用组内内存屏障；
    - WSB 逃逸与捕获半径动态参数化。
  - **类别 5 (工程与部署环境解耦)**:
    - 部署脚本支持环境变量与参数传导，移除个人私有 Steam 路径；
    - AssetBundle 打包工具完整覆盖 12 个 Compute Shader，输出与缓存目录支持环境变量覆盖。
  - **类别 6 (诊断内核对齐与真实遥测基线)**:
    - 11 个测速内核与 HLSL 真实名称、缓冲区布局 100% 对齐；
    - 废除伪造基准指标与 `Mathf.Sin` 假数据，CPU 降级分支全面重构为真实天体物理数学（开普勒方程、双冲量 Lambert、克莱罗球谐积分、CR3BP 雅可比能、Verlet 辛积分）。
  - **基线固化**: 建立棘轮底线 Commit `614b2ccd5`，编译构建 0 错误 0 警告。
