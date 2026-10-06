# Principia 原版算法 GPGPU 加速落地工程规格说明书
## (Engineering Specification: Untapped GPGPU Acceleration Opportunities in Principia)

- **文档版本**: 1.0.0 (Release)
- **基线分支**: `master` (经典 C++ 原生实现) & `feature/multi-level-caching-pipeline` (异构加速分支)
- **文档目标**: 将审查审计中确认的原版 CPU 算力瓶颈与数学机理，转化为高吞吐、零 GC、低延迟且可直接落地的 GPU 异构工程实现规范。

---

## 目录
1. [工程全景与实施矩阵](#1-工程全景与实施矩阵)
2. [模块 1：视口自适应轨迹投影与积分 (PlotMethod4)](#2-模块-1视口自适应轨迹投影与积分-plotmethod4)
3. [模块 2：多项式流形密集展开基底纠偏 (Estrin/Horner)](#3-模块-2多项式流形密集展开基底纠偏-estrinhorner)
4. [模块 3：2D 旋转脉动等势线流形 (Marching Squares)](#4-模块-32d-旋转脉动等势线流形-marching-squares)
5. [模块 4：高阶球谐引力场递推 (Geopotential)](#5-模块-4高阶球谐引力场递推-geopotential)
6. [模块 5：轨道分析器平根数求积与星下点交点提取](#6-模块-5轨道分析器平根数求积与星下点交点提取)
7. [模块 6：飞行计划连续拟牛顿有限差分并发寻优](#7-模块-6飞行计划连续拟牛顿有限差分并发寻优)
8. [反模式与严禁 GPU 化场景规范](#8-反模式与严禁-gpu-化场景规范)
9. [分阶段落地实施排期](#9-分阶段落地实施排期)

---

## 1. 工程全景与实施矩阵

| 优先级 | 模块代号 | 核心算法与原版实现文件 | 原版 CPU 核心瓶颈 | GPGPU 加速范式 | 预期指标与延迟目标 |
| :---: | :--- | :--- | :--- | :--- | :--- |
| **P0** | **TRAJ_PLOT** | `ksp_plugin/planetarium_body.hpp`<br/>`PlotMethod4` | 主线程阻塞 2~8ms；4 点高斯求积步长自适应坍缩 | 屏幕空间曲率自适应细分 + `DrawProceduralIndirect` | 主线程消耗降至 **< 0.1 ms**，消灭视角旋转掉帧 |
| **P0** | **POLY_EVAL** | `physics/continuous_trajectory_body.hpp`<br/>`EstrinEvaluator` (单项式基) | 跨语言每帧批量查询坐标；现有分支着色器误用切比雪夫基 | 单项式基 Estrin 树展开 + 3 次 Hermite 并行基函数 | 彻底纠正坐标失真，实现 **0 拷贝流式展开** |
| **P1** | **EQUIPOT_2D** | `physics/lagrange_equipotentials_body.hpp`<br/>`physics/equipotential_body.hpp` | 鞍点 $L_1/L_2$ 处正交梯度步进坍缩，10 飞米极值积分超时断裂 | 2D 真实脉动势场网格 + 2D Marching Squares 线段直出 | 耗时从 100~500ms 降至 **< 0.2 ms**，拓扑 100% 闭合 |
| **P1** | **GEOPOT_REC** | `physics/geopotential_body.hpp`<br/>`GeneralSphericalHarmonicsAcceleration` | CPU 强制硬编码 30 阶上限，高阶展开占单步积分 60%~80% 耗时 | 寄存器级 Clenshaw/Pines 递推，FMA 密集多极矩展开 | 支持 **150 阶** 全球精细引力异常，吞吐提升 **50~150×** |
| **P1** | **ORBIT_ANAL** | `astronomy/orbital_elements_body.hpp`<br/>`physics/apsides_body.hpp` | 滑动平根数 8 维 ODE 指数制动卡死；地面交点标量 Brent 迭代 | 单线程局部高斯求积 + Cardano 三次方程解析闭式解 | 消除 100% 卡死，万级离散点交点耗时 **< 0.05 ms** |
| **P2** | **OPT_DIFF** | `ksp_plugin/flight_plan_optimizer.cpp`<br/>BFGS 前向有限差分与线搜索 | 每次偏导全量重新积分后续多航段，调参交互顿挫 2~5 秒 | 16K 并行打靶定位吸引子 + 64 路扰动与 Armijo 并发积分 | 拟牛顿步进整体响应耗时 **< 50 ms**，拖拽实时响应 |

---

## 2. 模块 1：视口自适应轨迹投影与积分 (PlotMethod4)

### 2.1 原版机理与缺陷分析
- **原版文件**: [`ksp_plugin/planetarium_body.hpp:150-265`](file:///c:/Users/43701/Documents/github/Principia/ksp_plugin/planetarium_body.hpp#L150-L265)
- **调用时机**: Unity 渲染管线 `OnPreCull` 同步阻塞调用。
- **痛点机理**: 原版对视口内每条轨迹运行 `do { ... } while (rms > angular_resolution)` 步进循环。每步构造 3 次 Hermite 多项式并调用 `GaussLegendre<4>` 计算投影角位移均方根误差：
  $$\text{rms} = \sqrt{\frac{1}{\Delta t}\int_{previous\_t}^t \|\text{error}(\tau)\|^2 d\tau}$$
  在近心点、双星共振区与 WSB 区域，误差超标引发连续回退，单帧耗时可达 2~8ms，严重阻塞渲染线程。

### 2.2 GPGPU 落地架构设计
```
[C++ 离散航迹 / 多项式] ──Zero-Copy──> [GPU StructuredBuffer]
                                                │
                                    (TessellateTrajectory CS)
                                    - 并行投影至裁剪空间
                                    - 计算弦高差 Sagitta
                                    - 并行自适应细分生成线段
                                                │
                                       [RWStructuredBuffer<Vertex>]
                                       [RWStructuredBuffer<IndirectArgs>]
                                                │
                                     (Graphics.DrawProceduralIndirect)
                                     - 0 CPU 拷贝，直接光栅化绘制
```

### 2.3 数据结构与 Compute Kernel 规范
- **着色器路径**: `shaders/TrajectoryTessellator.compute`
- **常量缓冲区 (`TessellationConfig`)**:
  ```hlsl
  cbuffer TessellationConfig : register(b0) {
      float4x4 _WorldToCameraMatrix;
      float4x4 _CameraToScreenMatrix;
      float4 _ViewportSizePixels;     // xy: width, height; zw: 1/width, 1/height
      float _AngularToleranceRadians; // 对应 parameters_.angular_resolution_
      float _MaxPixelSagitta;         // 屏幕空间最大弦高容差 (像素，如 1.5px)
      uint _SegmentCount;
      uint _MaxOutputVertices;
  };
  ```
- **核心内核逻辑**:
  1. 线程对应输入的基础轨迹跨度 $[t_i, t_{i+1}]$；
  2. 将两端点及切向量投影至屏幕像素空间，评估中心点三次插值投影与直线投影之差（弦高差 $\delta$）；
  3. 若 $\delta \le \text{tolerance}$，通过 `InterlockedAdd(_IndirectArgs[0], 2, base_out_idx)` 分配 2 顶点输出；
  4. 若 $\delta > \text{tolerance}$，动态计算细分步数 $K = \text{clamp}(\lceil\sqrt{\delta / \text{tol}}\rceil, 2, 16)$，输出 $K$ 段细分折线；
  5. 绘制阶段调用 `Graphics.DrawProceduralIndirect(MeshTopology.Lines, _IndirectArgsBuffer, 0)`。

---

## 3. 模块 2：多项式流形密集展开基底纠偏 (Estrin/Horner)

### 3.1 纠偏审计依据
- **原版源码事实**:
  [`numerics/newhall_body.hpp:84-112`](file:///c:/Users/43701/Documents/github/Principia/numerics/newhall_body.hpp#L84-L112) 在拟合完成时已将切比雪夫基转化为**单项式基**；[`physics/continuous_trajectory_body.hpp:260-299`](file:///c:/Users/43701/Documents/github/Principia/physics/continuous_trajectory_body.hpp#L260-L299) 明确定义：
  `PolynomialInMonomialBasis<Displacement<Frame>, Instant, degree, EstrinEvaluator>`。
- **着色器修复要求**: 废除当前 `TrajectoryTessellator.compute` 中的 `EvaluateChebyshevSegmentClenshaw`，全面替换为针对单项式基的 **Horner 算法** 或 **Estrin 硬件 FMA 并行求值**。

### 3.2 显存布局与求值 Kernel 规范
- **数据结构**:
  ```hlsl
  struct MonomialSegmentData {
      float t_min;
      float t_max;
      uint degree; // 常见阶数 3 ~ 17
      float padding;
      float3 coeffs[18]; // 单项式系数 c_0, c_1, ..., c_d
  };
  ```
- **HLSL 求值实现 (Horner 与 FMA 展开)**:
  ```hlsl
  float3 EvaluateMonomialSegmentHorner(MonomialSegmentData seg, float t) {
      float tau = t - seg.t_min;
      float3 pos = seg.coeffs[seg.degree];
      [loop]
      for (int i = int(seg.degree) - 1; i >= 0; --i) {
          pos = mad(pos, tau, seg.coeffs[i]); // 硬件 FMA: pos * tau + c_i
      }
      return pos;
  }
  ```
- **Hermite3 离散段基函数求值**:
  对飞船离散航迹采用归一化参数 $u = \frac{t - t_0}{\Delta t} \in [0, 1]$：
  ```hlsl
  float3 EvaluateHermite3(float3 p0, float3 v0, float3 p1, float3 v1, float dt, float u) {
      float u2 = u * u;
      float u3 = u2 * u;
      float h00 = 2.0 * u3 - 3.0 * u2 + 1.0;
      float h10 = u3 - 2.0 * u2 + u;
      float h01 = -2.0 * u3 + 3.0 * u2;
      float h11 = u3 - u2;
      return h00 * p0 + (h10 * dt) * v0 + h01 * p1 + (h11 * dt) * v1;
  }
  ```

---

## 4. 模块 3：2D 旋转脉动等势线流形 (Marching Squares)

### 4.1 原版缺陷与数学定义穿透
- **原版文件**: [`physics/lagrange_equipotentials_body.hpp:56-114`](file:///c:/Users/43701/Documents/github/Principia/physics/lagrange_equipotentials_body.hpp#L56-L114)
- **数学本质**: 原版等势线严格定义在双星连线及共面自转构成的 **2D 轨道切片平面**（$\text{Plane} \perp \{0, 0, 1\}$）。
- **旋转脉动势严格物理公式**:
  $$V^P(q^P) = \frac{\ddot{r}}{2r} \|q^P\|^2 + \frac{1}{r^2} \left( \sum_{b} \left(-\frac{\mu_b}{\|q - q_b\|} + U_{\text{harmonics}, b}\right) + \langle q^R, a_{\text{origin}} \rangle - \frac{1}{2}\|\mathbf{\Omega} \times q^R\|^2 \right)$$
- **优化点**: 瞬间脉动因子 $C_{\text{pulsate}} = \frac{\ddot{r}}{2r}$、尺度因子 $S = \frac{1}{r^2}$、自转矢量 $\mathbf{\Omega}$ 与原点加速度 $a_{\text{origin}}$ 在全平面内均为空间常量，由 C++ 端单次计算后直接传入 GPU。

### 4.2 GPGPU 2D 管线设计
- **文件**: `shaders/RotatingPulsatingPotentialGrid.compute`
- **步骤 1：生成 2D 有效势纹理 (`GridResolution = 1024x1024`)**
  - 单线程计算一个像元的 $V^P(x, y)$，写入 `RWTexture2D<float> _PotentialField`；
  - 耗时: 现代 GPU 上单次 Dispatch 耗时 **< 0.12 ms**。
- **步骤 2：2D Marching Squares 闭合折线提取**
  - 读取指定等势能级（$L_1, L_2, L_3$ 及 8 档能量标尺）；
  - 根据 4 顶点势能符号查 16 项查找表，线性插值出线段端点坐标，通过 `AppendStructuredBuffer<float2>` 写入顶点缓冲区；
  - 彻底消灭原版在 CPU 上容易断裂坍缩的 10,000 步 DOPRI5 积分与拓扑绕数（Winding Number）几何验证。

---

## 5. 模块 4：高阶球谐引力场递推 (Geopotential)

### 5.1 原版缺陷与重构目标
- **原版文件**: [`physics/geopotential_body.hpp:742-813`](file:///c:/Users/43701/Documents/github/Principia/physics/geopotential_body.hpp#L742-L813)
- **现状**: CPU 端展开 30 阶（宏上限 50 阶）勒让德多项式循环占用过多时间。
- **目标**: 将勒让德多项式与三角谐波递推下放至 Compute Shader，突破至 **150 阶** 球谐展开，直接为轨道积分束与引力场可视化提供超高精度 ALU 算力。

### 5.2 Pines / Clenshaw 递推着色器规范
- **着色器路径**: `shaders/HighDegreeGeopotentialRecurrence.compute`
- **常量缓冲**:
  ```hlsl
  cbuffer BodyHarmonicsConfig : register(b0) {
      float _GM;
      float _ReferenceRadius;
      uint _MaxDegree; // 70 ~ 150
      float padding;
  };
  StructuredBuffer<float2> _HarmonicsCoeffs : register(t0); // Packed C_nm, S_nm
  ```
- **算法细节**:
  1. 采用 Pines (1973) 无奇异度递推法，避免高纬度极点除以 $\cos\phi$ 导致的数值溢出；
  2. 递推中间变量（缔合勒让德函数 $P_{nm}$）全部保存在 GPU 寄存器阵列中，完全消除显存访存；
  3. 为批处理点（批量飞船状态或 3D 采样点）提供矢量化加速度矢量 $\mathbf{a} = -\nabla U$。

---

## 6. 模块 5：轨道分析器平根数求积与星下点交点提取

### 6.1 平根数求积纠偏与局部并发方案
- **原版文件**: [`astronomy/orbital_elements_body.hpp:369, 467-530`](file:///c:/Users/43701/Documents/github/Principia/astronomy/orbital_elements_body.hpp#L369)
- **原版卡顿机理**: 为计算滑动平均，原版使用 DOPRI5 解微分方程，加入指数制动因子 `exp(6 - 18 * step / period)` 强行加密步长，导致分析器在 100% 处假死卡顿数十秒。
- **严禁全局前缀和扫描**: 平黄经 $\lambda = \varpi + M$ 为单调递增解缠角，$S(t) \sim O(t^2)$ 随时间单调爆炸，全局前缀和差分相减必然引发灾难性浮点抵消！
- **正确 GPU 方案：独立局部高斯求积 (Local Quadrature per Thread)**:
  1. 目标输出 $M$ 个等间隔时刻 $t_k$（例如 $M = 1024$）；
  2. **每个 GPU 线程独立负责一个时刻 $t_k$**；
  3. 线程 $k$ 在其局部周期区间 $[t_k - T/2, t_k + T/2]$ 内直接采样 8 点或 16 点 Gauss-Legendre 节点计算定积分：
     $$\bar{\boldsymbol{e}}(t_k) = \frac{1}{T}\sum_{j=1}^{16} w_j \boldsymbol{e}(t_k - T/2 + x_j T)$$
  4. 线程间完全无锁、无依赖，彻底消灭 DOPRI5 积分假死，耗时降至 **< 0.1 ms**。

### 6.2 星下点地面交点提取 (Nodes)
- **原版文件**: [`physics/apsides_body.hpp:480-536`](file:///c:/Users/43701/Documents/github/Principia/physics/apsides_body.hpp#L480-L536) (`ComputeNodes`)
- **GPU 方案**:
  1. 1D 线程块并行遍历连续航迹段 $[t_i, t_{i+1}]$，检测赤道平面赤纬 $z_i \cdot z_{i+1} < 0$；
  2. 命中变号后，该区间内三次多项式 $z(u) = a u^3 + b u^2 + c u + d$ 单调；在 HLSL 中直接执行 **Cardano（卡尔丹）三次方程解析求根** 或 2 步 Newton-Raphson 迭代，彻底剔除 CPU 端标量 Brent 二分迭代。

---

## 7. 模块 6：飞行计划连续拟牛顿有限差分并发寻优

### 7.1 原版瓶颈与互补架构
- **原版文件**: [`ksp_plugin/flight_plan_optimizer.cpp:524-750`](file:///c:/Users/43701/Documents/github/Principia/ksp_plugin/flight_plan_optimizer.cpp#L524-L750)
- **痛点**: 原版 BFGS 拟牛顿法每算一次 4 维前向差分梯度，需调用 5 次 `flight_plan_->Replace(...)`，串行重跑后续全部机动段与滑行段积分；线搜索阶段又要尝试数次，拖动滑块时耗时 2~5 秒。
- **两级融合加速流程**:
  ```
  [玩家拖拽/目标调整] 
          │
          ▼
  [1. 全局初筛] ──> FlightPlanShootingOptimizer.compute (16,384 点打靶网格)
          │         - GPU 2ms 内输出全局最优吸引子盆地参数 (t0, Δvt, Δvn, Δvb)
          ▼
  [2. 局部精修] ──> BatchedPerturbationEvaluator.compute
                    - 并发推进 5 组有限差分微扰轨迹 (Central/Forward Difference)
                    - 并发推进 32~64 组 Armijo 回溯线搜索步长 α_k
                    - GPU 内部规约最优步长后，回传 C++ 极速收敛 (总耗时 < 50ms)
  ```

---

## 8. 反模式与严禁 GPU 化场景规范

在后续实施中，必须严格遵守以下物理与工程禁界，防止负优化：

```
                    ┌───────────────────────────────┐
                    │  严禁迁移至 GPGPU 的场景红线  │
                    └──────────────┬────────────────┘
                                   │
        ┌──────────────────────────┼──────────────────────────┐
        ▼                          ▼                          ▼
【N体主星历推演】             【CPI 替代辛积分】         【6-DOF 刚体欧拉求解】
天体仅 N ≤ 30 颗，对数 ≤ 435。 非辛配点迭代，强引力/大气  加载泡内仅 1~3 艘飞船，
GPU 启动延迟远超纯计算；      发散；不可作为通用积分器。  单次耗时 < 1 μs，
CPU AVX-512 原生计算最优。                                派发 GPU 开销严重倒挂。
```

1. **禁止将少体行星星历 (`Ephemeris::Prolong`) 搬上 GPU**：天体数量极小（$N \le 30$），GPU 启动开销（5~15 $\mu$s）与显存同步延迟将远远超过计算本身，且高阶辛几何对舍入极其敏感。
2. **禁止将切比雪夫-皮卡迭代 (CPI) 用作通用主积分器**：CPI 是非辛配点法，强引力散射时发散，无法替代辛积分器。
3. **禁止使用全局前缀和 (Prefix Sum) 计算解缠角度均值**：必须使用独立单线程局部高斯求积，规避灾难性浮点截断。
4. **禁止对加载泡内活跃飞船做 6-DOF 刚体姿态 GPU 派发**：每帧仅 1~3 艘飞船，CPU 标量椭圆函数求值耗时 $< 1\ \mu\text{s}$，移至 GPU 会造成严重的驱动开销倒挂。

---

## 9. 分阶段落地实施排期

### 阶段一：视口渲染彻底解耦与无拷贝几何 (P0)
- [ ] **Task 1.1**: 重构 `shaders/TrajectoryTessellator.compute`：彻底移除切比雪夫 Clenshaw 误实现，换为单项式基 Horner/Estrin 展开与 Hermite3 展开。
- [ ] **Task 1.2**: 在 `GpuTrajectoryRenderer.cs` 打通 `Graphics.DrawProceduralIndirect`，输出顶点直接走 GPU 拓扑绘制。
- [ ] **Task 1.3**: 在 `ksp_plugin/planetarium_body.hpp` 旁路 CPU `PlotMethod4` 的自适应高斯求积，改为下发轨迹节点数据流。

### 阶段二：核心势场与分析器算力下放 (P1)
- [ ] **Task 2.1**: 废除分支粗糙的 3D 原型，编写 `shaders/RotatingPulsatingPotentialGrid.compute`，实现精确 2D 脉动拉格朗日有效势场。
- [ ] **Task 2.2**: 编写 2D Marching Squares 闭合折线提取 Compute Shader，替换原版 DOPRI5 沿线追踪与 Winding Number 验证。
- [ ] **Task 2.3**: 编写 `shaders/OrbitAnalyserQuadrature.compute`：实现 1024 点并行局部高斯求积（平根数）与 Cardano 三次解析交点提取。
- [ ] **Task 2.4**: 编写 `shaders/HighDegreeGeopotentialRecurrence.compute`：实现 150 阶 Clenshaw/Pines 勒让德无奇异递推。

### 阶段三：大规模深空动力学与环境扩展 (P2)
- [ ] **Task 3.1**: 完善机动优化器：将 16K 打靶初筛与 BFGS 并发扰动段接入 `burn_editor.cs`。
- [ ] **Task 3.2**: 完善万级碎片批传播 `shaders/BatchOrbitIntegrator.compute` 与 Double-Single 双单精度物理校验。
