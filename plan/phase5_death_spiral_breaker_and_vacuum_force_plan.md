# Phase 5: Physics Death Spiral Circuit Breaker & Zero-Allocation Vacuum Force Bypass Pipeline
*(物理死循环硬核熔断器与真空零分配力学旁路流水线实施规划)*

## 1. 架构目标与工程愿景 (Architecture Goals & Vision)

在 Unity 2019 / KSP 单线程主循环中，当物理计算耗时突增时，Unity `TimeManager` 会在单渲染帧内累积时间并塞入多个 `FixedUpdate` 循环（$N = \lfloor \Delta t_{\text{render}} / \Delta t_{\text{fixed}} \rfloor$）。当单个物理步耗时超过 15ms 时，多步迭代将帧时间推高至 60ms+（< 15 FPS），导致下一帧累积更多物理时间，陷入不可逆的“物理死循环（Physics Death Spiral）”。

同时，现有 `FashionablyLate` 与 `FlightIntegrator`（`JaiFailliAttendre`）在单个物理帧内逐部件分配 `PartCentredForceHolder[]` 数组并频繁发起 `PartDragTorqueFromAngularVelocity` P/Invoke，在大部件空间站上每秒产生数兆字节托管堆垃圾并导致 Mono GC 严重卡顿。

**Phase 5 核心交付目标**：
1. **Unity 物理死循环硬核熔断器 (`FrameBudgetCircuitBreaker`)**：
   - 建立高精度物理时钟预算巡航器（默认 8.0 ms 预算）。
   - 三级梯次熔断：Level 1（次要载具冻结）、Level 2（活跃载具外推切断）、Level 3（累加器时间债硬吸收），从根本上扑灭物理死循环。
   - 联动 `DualClockBridge` 与 `TripleBufferStateBus`，在熔断期间利用连续多项式瞬时外推抹平视觉位移，确保 60/144 FPS 丝滑输出。
2. **真空零分配力学与气动旁路流水线 (`ZeroAllocationForceCache`)**：
   - 为真空稳定态载具提供全局单例静态零开销力持有数组，彻底消灭 `new PartCentredForceHolder[]` 堆分配。
   - 真空环境下彻底阻断 `PartDragTorqueFromAngularVelocity` 等 500+ 次冗余 P/Invoke。
   - 消除 95%+ 的力学堆内存碎片与 Mono GC 微顿挫。
3. **全链路实时遥测面板与诊断增强 (`gpu_diagnostics_window`)**：
   - 实时呈现物理帧耗时、熔断拦截次数、零分配内存节约量与 GC 免疫指标。
4. **生产环境验证与自动化部署**：
   - MSBuild 编译、NetFX 4.7.2 运行时反射验证、KSP GameData 部署与 Git 检查点。

---

## 2. 详细实施步骤 (Implementation Steps)

### Step 1: 物理死循环硬核熔断器 (`FrameBudgetCircuitBreaker.cs`)
- 纳秒级帧耗时统计与滑动平均（EMA）。
- 三级防线判定逻辑（Normal / Level 1 / Level 2 / Level 3）。
- 连续时间运动外推降级接管。

### Step 2: 真空零分配力学缓存与气动旁路 (`ZeroAllocationForceCache.cs`)
- 静态空力持有者池（Zero-Alloc Empty Holders）。
- 预分配环形缓冲容器，避免字典与数组重新分配。
- 真空态气动扭矩 P/Invoke 智能旁路网关。

### Step 3: 主适配器生命周期深度织入 (`ksp_plugin_adapter.cs`)
- 在 `ObscenelyEarly`、`Precalc`、`FashionablyLate`、`JaiFailliAttendre`、`WaitedForFixedUpdate` 中接入熔断与零分配旁路。
- 联动 `VacuumBypassGovernor` 与 `HeterogeneousDynamicGovernor`。

### Step 4: 诊断监控面板与遥测展示 (`gpu_diagnostics_window.cs`)
- 展示 Phase 5 实时指标：熔断状态、物理帧耗时柱状图、GC 内存节省字节数、P/Invoke 削减数。

### Step 5: 项目配置、编译构建、运行时验证与部署
- 更新 `ksp_plugin_adapter.csproj`。
- MSBuild Release/AnyCPU 编译。
- NetFX 4.7.2 反射验证布局与无分配特性。
- `scripts/deploy_to_ksp.ps1` 同步至 KSP GameData。
- Git 提交归档。
