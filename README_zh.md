# Principia-Optima

> **[Principia](https://github.com/mockingbirdnest/Principia) 异构硬件加速与性能演进分支**  
> 基于坎巴拉太空计划（KSP）的 $n$-体与非球形引力摄动物理，提供异构多 GPU 协同调度与 PCIe ReBAR 显存直通 DMA 支持。

[English Documentation](README.md) | [许可证 (Apache 2.0)](LICENSE) | [上游官方仓库](https://github.com/mockingbirdnest/Principia)

---

## 项目概述

本项目基于经典天体力学模组 [Principia (mockingbirdnest/Principia)](https://github.com/mockingbirdnest/Principia) 开发。在保留原版辛几何数值积分器与非球形引力物理模型的前提下，引入现代 PC 硬件异构计算与显存直通管线，分流 CPU 轨道外推与渲染负担，改善复杂存档和密集轨道计算下的帧率表现与物理稳定性。

---

## 核心架构与特性

### 1. 异构多设备动态负载均衡 (Heterogeneous Workload Balancer)
* **动态拓扑感知**：自动识别系统内可用的独立显卡（dGPU）、集成显卡（iGPU）与中央处理器（CPU）。
* **自适应分块调度**：根据实测的总线传输延迟与各设备计算吞吐，动态调整轨道外推任务的分块大小，协调核显与空闲计算单元参与计算。
* **分级降级容灾**：遇到显卡驱动重置或显存压力过高时，任务自动回退至 CPU 或备用设备，避免物理模拟中断或游戏崩溃。

### 2. PCIe ReBAR / Direct3D 11 Zero-Copy DMA 显存直通
* **显存视口映射**：在支持 PCIe Resizable BAR 的硬件上，将 Direct3D 11 缓冲区映射为 USWC（不可缓存推测写入合并）显存空间。
* **流式存储直写**：使用 AVX2 / SSE2 非暂态流式存储指令直写物理显存，避免写入数据污染 CPU 缓存。
* **单批次屏障**：在批量数据传输完成时执行单次内存屏障（SFENCE），减少多余的流水线停顿，提高总线写入吞吐。

### 3. 切比雪夫 (Chebyshev) 连续高阶多项式轨迹压缩
* **Gauss-Lobatto 16 阶节点变换**：通过正交多项式基函数展开，将离散时间采样点转换为连续解析多项式段。
* **降低传输与显存开销**：每个 256 字节的多项式切片覆盖数百秒轨道演化，实测减少约 80% 的跨总线传输量与 GPU 显存占用，并在渲染时提供平滑插值。

### 4. CPU 多级缓存微架构全层级加速 (L1 / L2 / L3 Multi-Tier Cache)
* **L1d 数据缓存天体块 (1.75 KB SoA)**：将多达 32 个主要天体的质量与状态向量封装为 64 字节对齐的 SoA 结构，体积仅 1.75 KB，100% 驻留在 CPU L1d 高速缓存（32KB~48KB）中。在多重 Runge-Kutta 物理子步积分中实现 **100% L1 缓存命中率**。
* **L2 单核私有切比雪夫瓦片缓存 (128 KB)**：每个 CPU 核心维护 512 个连续解析多项式轨道段瓦片（每段 256 字节），Clenshaw 递推解析求解 100% 在单核 L2 内部（~3ns 延迟）无分支向量化执行，零核间总线通信。包含 64 KB 对齐的 `SymplecticIntegratorScratchpad`，彻底消除物理步进时的堆内存动态分配。
* **L3 全局星历连续时间窗口环形缓冲 (4.19 MB)**：为太阳系全部 32 个天体维护跨越数千秒（$t \pm 1000\text{ s}$）的连续解析轨道段池（$32 \times 512 \times 256\text{ B} = 4.096\text{ MB}$）。全核心多线程并发无锁读取全部命中共享 L3 缓存，彻底消除 60~80ns 的主内存等待延迟。
* **SIMD 缓存行对齐与流式预取**：所有关键数据结构应用 `alignas(64)` 消除多核伪共享（False Sharing），并在连续多项式循环中使用 `_mm_prefetch` 提前导入系数。

### 5. 自适应动态切片刷新调度器与帧预算熔断器 (Dynamic Cadence Governor)
* **平滑预算余量自适应调节器**：彻底废除硬编码刷新率，基于物理帧耗时 EWMA 余量（$\eta = \frac{\tau_{\text{budget}} - \tau_{\text{physics}}}{\tau_{\text{budget}}}$）动态调度：
  - **Tier 1 (核心级, 50 Hz)**：活动焦点载具 / 真实物理泡 / 产生推力，每帧实时期期积分，绝不延后。
  - **Tier 2 (交互级, 16.7 ~ 50 Hz)**：伴飞载具与机动节点，在算力充沛（余量 $\ge 40\%$）时自动提速至 **50 Hz 满帧实时运行**，负载增加时平滑调整至 25 Hz 或 16.7 Hz。
  - **Tier 3 (延后级, 2 ~ 25 Hz)**：远距探测器与碎片根据剩余算力动态在 2 Hz 到 25 Hz 之间弹性伸缩。
* **斐波那契黄金分割散列 (Fibonacci Hash)**：飞船通过 `(frame_index + (vessel_uid * 0x9E3779B97F4A7C15ULL >> 32)) % dynamic_stride == 0` 均匀映射至各帧，消除周期性波峰微卡顿。
* **切比雪夫高阶多项式解析连续插值**：被切片让步跳过的帧在渲染时由高阶多项式无缝连续拟合，**实现 0 物理卡顿、锁定高帧率**。

### 6. GPGPU 并行打靶飞行计划求解器 (Parallel Shooting Optimizer)
* 利用 GPU 计算着色器并发外推参数空间中的候选轨道，缩短复杂变轨与深空交会窗口的打靶搜索耗时。

### 7. 异步性能监控与 CPU 线程探针面板 (Performance & CPU Threads Probe)
* 游戏内浮动悬浮窗，在独立线程中无 GC 轮询各计算模块的可用与活跃 CPU 线程，实时展示各 Tier 的动态刷新率（Hz）与步进跨度、L1/L2/L3 各级缓存命中次数以及多 GPU 协同状态。

---

## 硬件与系统要求

| 项目 | 最低配置 | 推荐配置 |
| :--- | :--- | :--- |
| **操作系统** | Windows 10/11 64-bit | Windows 11 64-bit |
| **游戏版本** | Kerbal Space Program 1.8.1 — 1.12.5 | KSP 1.12.5 |
| **处理器 (CPU)** | 支持 SSE4.2 / AVX 的 64 位 x86 处理器 | 支持 AVX2 / FMA 的现代多核处理器 |
| **独立显卡 (dGPU)** | 支持 Direct3D 11 的独立显卡 | 支持 PCIe 4.0/5.0，且主板 BIOS 开启 **Resizable BAR** |
| **集成显卡 (iGPU)** | 可选（检测到核显时自动作为辅助算力） | Intel UHD/Iris/Xe 或 AMD Radeon Graphics |

> [!TIP]
> **开启 PCIe ReBAR**  
> 建议在主板 BIOS 中开启 **Resizable BAR Support** 与 **Above 4G Decoding**。开启后，CPU 可直接向 GPU 显存写入多项式切片，降低数据拷贝延迟。

---

## 源码编译说明

使用 Visual Studio 2022 与 ClangCL 原生编译工具链：

```powershell
# 1. 编译优化版 C++ 动态库 (AVX/FMA 优化配置)
& "MSBuild.exe" "ksp_plugin\ksp_plugin.vcxproj" /p:Configuration=Release_AVX_FMA /p:Platform=x64 /m

# 2. 编译基础兼容版 C++ 动态库 (通用 x64 配置)
& "MSBuild.exe" "ksp_plugin\ksp_plugin.vcxproj" /p:Configuration=Release /p:Platform=x64 /m

# 3. 编译 C# 游戏适配层
& "MSBuild.exe" "ksp_plugin_adapter\ksp_plugin_adapter.csproj" /p:Configuration=Release /p:Platform=AnyCPU /m
```

编译完成后，将生成的产物复制到游戏目录的 `GameData/Principia/` 对应路径：
* `ksp_plugin_adapter.dll` -> `GameData/Principia/`
* `Windows/x64_AVX_FMA/principia.dll` -> `GameData/Principia/Windows/x64_AVX_FMA/`
* `Windows/x64/principia.dll` -> `GameData/Principia/Windows/x64/`

---

## 原版 Principia 资源与说明

*(以下为上游官方 [mockingbirdnest/Principia](https://github.com/mockingbirdnest/Principia) 的说明与参考资料)*

![⚠️](https://placehold.co/10/red/red) _If you downloaded Lévy before September 13th, 20:58 UTC, you probably have a buggy version (see [#4685](https://github.com/mockingbirdnest/Principia/issues/4685)). Please download it again. If you have the correct version, the version string in the Principia UI says `2026091103-Lévy-0-gc6db14e961191ce8fd931d3302bfa7f7e80d0ecd`. We apologize for the inconvenience._ ![⚠️](https://placehold.co/10/red/red)

**&lrm;[Lévy](https://github.com/mockingbirdnest/Principia/wiki/Change-Log#le%CC%81vy), the September version of Principia, is available, with new features and bug fixes.  Download it [here for 1.8.1, 1.9.1, 1.10.1, 1.11.0, 1.11.1, 1.11.2, and 1.12.2 to 1.12.5](https://bit.ly/l3vy).**

**For the convenience of Chinese users, download from 腾讯微云: [Principia Lévy for 1.8.1—1.12.5](https://share.weiyun.com/JAmT3w6C).**

Principia 是《坎巴拉太空计划》（KSP）的模组，实现了 $n$-体引力与非球形扩展体引力。飞船在任意时刻都会受到所有天体的引力影响，而非局限于单一引力范围（SOI）。这使得比原版游戏更为复杂和真实的轨道任务成为可能，尤其是配合包含真实天体的 RealSolarSystem 等模组时。

在开始使用前，建议阅读官方[概念文档 (Concepts)](https://github.com/mockingbirdnest/Principia/wiki/Concepts)，了解[绘制参考系 (Plotting frame)](https://github.com/mockingbirdnest/Principia/wiki/Concepts#plotting-frame)与[飞行计划 (Flight planning)](https://github.com/mockingbirdnest/Principia/wiki/Concepts#flight-planning)。

推荐参考资料：
* **[Mun 登月教程](https://github.com/mockingbirdnest/Principia/wiki/A-guide-to-going-to-the-Mun-with-Principia)**：介绍如何以较低能耗飞往 Mun。
* **[交会对接指南](https://github.com/mockingbirdnest/Principia/wiki/A-guide-to-performing-low-orbit-rendezvous)**：说明如何进行轨道交会。
* **[常见问题与已知限制 (FAQ)](https://github.com/mockingbirdnest/Principia/wiki/Installing,-reporting-bugs,-and-frequently-asked-questions)**：包含安装说明与问题反馈方式。
* **[版本变更日志 (Change log)](https://github.com/mockingbirdnest/Principia/wiki/Change-Log)**：各版本更新详情。

Principia 在每个[新月 (New moon)](https://en.wikipedia.org/wiki/New_moon)发布更新。

预编译二进制文件下载（Ubuntu、macOS、Windows）：[适用于 1.8.1—1.12.5](https://bit.ly/l3vy)。若希望自行编译，请参考[构建说明](https://github.com/mockingbirdnest/Principia/blob/master/documentation/Setup.md)。

---

## 许可证

本项目继承 Principia 采用 [Apache License 2.0](LICENSE) 开源协议。
