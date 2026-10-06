# Phase 4: Scene Graph Decoupling & GPU Virtual Projection Implementation Plan

## 1. 架构目标与技术愿景
依据《Principia Unity 深度解耦架构方案》与《Principia 长期性能演进路线图》：
- 终结 Unity 场景图（Scene Graph）和 `Transform` 对 Principia 高精度物理的制约。
- 解决天体 `bodyTransform.rotation` 与 `body.CBUpdate()` 导致的 PQS（Procedural Quad Sphere）球面四叉树地形引擎每帧脏化（Dirty Invalidation）与 LOD 重建开销。
- 实现 ScaledSpace 缩放空间的 GPU 顶点偏置着色器（Vertex Shader Displacement），将天体缩放网格与轨迹渲染的微位移补偿移至 GPU 常量缓冲区计算。
- 深度整合 GPU 零拷贝程序化轨迹渲染管线（`GpuTrajectoryRenderer`）与平滑样条拟合（Smooth Plotting Interop），实现高刷新率（60/144 FPS）下丝滑平顺的轨道渲染。

---

## 2. 关键子系统与技术实现方案

### 2.1 天体场景图解耦器与 PQS 脏化拦截器 (SceneGraphDecoupler)
- **核心职责**：
  1. 动态评估天体在视口与物理中的显著度（Saliency）：
     - 主物理加载范围（Active Vessel SOI / Physics Bubble，距地表 < 10x 半径）为 Tier 0（实时物理同步）。
     - 镜头视口直接观察或相机聚焦天体为 Tier 1（平滑视觉同步，PQS 适度更新）。
     - 远距离主要天体为 Tier 2（降频至 1Hz~2Hz）。
     - 背景小天体/远距小行星为 Tier 3（降频至 0.2Hz~0.5Hz，彻底冻结场景树）。
  2. 拦截不必要的 `body.bodyTransform.rotation` 与 `CBUpdate` 写入：
     - 当天体远离相机且非物理参考体时，跳过对 Unity GameObject Transform 的暴力修改，防止触发 PQS 内部四叉树节点全量递归脏化。
     - 物理核心继续在 `TripleBufferStateBus` 中保持超高精度的稠密多项式切片，确保物理计算不受场景图降频影响。
  3. 统计并暴露 PQS 脏化拦截次数与场景图跳过率指标。

### 2.2 ScaledSpace GPU 顶点偏移与虚拟投影 (GpuVirtualProjection)
- **核心职责**：
  1. 编写专用着色器 `shaders/ScaledSpaceDisplacement.shader`：
     - 在顶点着色器中接入常量缓冲区参数：
       - `_ScaledSpaceOriginOffset`：ScaledSpace 摄像机与浮点原点偏移。
       - `_KrakensbaneDeltaOffset`：Krakensbane 速度反冲与帧位移补偿。
       - `_VirtualDisplacement`：天体在两次场景图刷新之间的微增量插值位移。
     - 在 GPU 顶点阶段直接叠加偏置，消除在 CPU 端每帧改写 GameObject 位置的开销。
  2. C# 侧控制器 `GpuVirtualProjection`：
     - 复用 `MaterialPropertyBlock`，做到单帧 0 GC 堆分配。
     - 与 `DualClockBridge` 实时同步，直接将 FloatingOrigin 与 Krakensbane 速度补偿矩阵推送到材质属性中。

### 2.3 零拷贝平滑轨迹渲染整合 (Smooth Plotting & Procedural Renderer)
- **核心职责**：
  1. 升级 `shaders/TrajectoryProcedural.shader`：
     - 增加抗锯齿边缘羽化（Smoothstep Screen-Space Feathering）。
     - 支持虚拟投影矩阵与 Krakensbane 偏置参数直接传入。
     - 支持多段渐变色彩与自发光发光度（Emission Multiplier）。
  2. 增强 `GpuTrajectoryRenderer.cs` 与 `plotter.cs`：
     - 提供 Catmull-Rom / Hermite 局部平滑插值算法，在 GPU 间接绘制或紧凑缓冲区中输出平滑曲线。
     - 兼容社区 `PrincipiaSmoothPlotting` 补丁，若检测到该模块，自动接驳其平滑输出并进入零拷贝 GPU 流水线。

### 2.4 实时诊断与监控界面 (GPU Diagnostics UI)
- 在 `gpu_diagnostics_window.cs` 中扩展 Phase 4 面板：
  - 天体场景图解耦状态（实时/降频/冻结天体数、PQS 脏化阻止计数、场景图节省率）。
  - GPU 虚拟投影状态（ScaledSpace 偏置、Krakensbane GPU 补偿量、MaterialPropertyBlock 更新计数）。
  - 轨迹渲染模式（GPU Procedural Direct vs Mesh Fallback、平滑样条状态、顶点吞吐量）。

---

## 3. 实施步骤与验收路线

1. **第 1 步：创建与优化着色器**
   - 编写 `shaders/ScaledSpaceDisplacement.shader` 与更新 `shaders/TrajectoryProcedural.shader`。
2. **第 2 步：实现 C# 核心架构类**
   - `ksp_plugin_adapter/SceneGraphDecoupler.cs`：天体显著度评级、Transform 降频与 PQS 脏化拦截。
   - `ksp_plugin_adapter/GpuVirtualProjection.cs`：GPU 偏置常量缓冲区同步与 MaterialPropertyBlock 管理。
3. **第 3 步：生命周期与管线整合**
   - 接入 `ksp_plugin_adapter.cs`（`Precalc`、`UpdateBody`、天体 Transform 写入切面）。
   - 接入 `plotter.cs` 与 `GpuTrajectoryRenderer.cs`（虚拟投影与平滑样条）。
4. **第 4 步：诊断窗口扩展**
   - 在 `gpu_diagnostics_window.cs` 渲染 Phase 4 遥测仪表盘。
5. **第 5 步：工程编译、NetFX 验证与部署**
   - 更新 `ksp_plugin_adapter.csproj`。
   - 执行 MSBuild Release 编译。
   - 通过 PowerShell 检验 .NET Framework 4.7.2 运行时行为。
   - 通过 `deploy_to_ksp.ps1` 同步着色器和程序集至 `GameData/Principia/`。
   - Git 提交记录。
