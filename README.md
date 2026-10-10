# Principia (Heterogeneous Multi-Device Acceleration Edition)

> **𝑛-Body and Extended Body Gravitation for Kerbal Space Program with Heterogeneous Multi-GPU Acceleration & ReBAR Direct DMA**

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-brightgreen.svg)](#)
[![Acceleration](https://img.shields.io/badge/Compute-Direct3D%2011%20%7C%20ReBAR%20DMA%20%7C%20AVX2-orange.svg)](#)

本项目是基于经典天体力学模组 [Principia (mockingbirdnest/Principia)](https://github.com/mockingbirdnest/Principia) 的深度优化与异构算力加速演进版本。在完整保持 Principia 严谨的辛几何积分器与 $n$-体引力摄动物理精度的前提下，构建了**异构多设备动态调度（CPU + dGPU + iGPU）**、**PCIe ReBAR 显存直通 Zero-Copy DMA**、**切比雪夫连续多项式高阶压缩**与**全异步硬件性能探针**等新一代高性能架构。

---

## ⚡ 核心架构与加速特性 (Key Features)

### 1. 异构多硬件动态负载均衡 (Heterogeneous Workload Balancer)
* **动态拓扑感知**：自动探测系统中所有的可用算力硬件，包括独显（Discrete GPU）、核显（Integrated GPU）、主机处理器（CPU）及辅助显卡（Auxiliary GPU）。
* **微基准自适应配比**：基于实际运行时的物理计算延迟与跨总线传输带宽，自适应动态平衡任务分块（Chunking），让核显与闲置计算单元协同参与轨道外推与星历求解，最大化硬件利用率。
* **分级降级容灾**：当主显卡或驱动遭遇设备重置或高显存压力时，毫秒级无缝自动迁移回 CPU/备用设备，保证游戏不崩溃、物理不中断。

### 2. PCIe ReBAR / Direct3D 11 Zero-Copy DMA 显存直通
* **物理显存视口直通**：利用 PCIe Resizable BAR (ReBAR / SAM) 机制，在 Direct3D 11 下将缓冲区映射为 USWC (Uncacheable Speculative Write-Combining) 空间。
* **SIMD 非暂态流式存储**：针对 x86-64 微架构量身定制的 AVX2 / SSE2 流式写入（Streaming Stores），绕过 CPU 缓存污染，直写物理显存。
* **单批次屏障优化 (Single End-of-Batch SFence)**：彻底消除循环内冗余的硬件流水线停顿，释放 PCIe 4.0/5.0 理论带宽极限。

### 3. 切比雪夫 (Chebyshev) 连续多项式高阶轨迹压缩
* **Gauss-Lobatto 16 阶节点变换**：通过正交多项式基函数展开，将传统海量离散离散轨迹采样点转换为高阶连续解析多项式段。
* **传输带宽暴降 80%+**：每个 256 字节的切片即可覆盖数百秒的高精度轨道演化，大幅降低跨总线搬运量与 GPU 显存占用，同时在渲染端实现亚微米级平滑插值。

### 4. 全异步无损硬件性能探针 (Performance Probe GUI)
* **自动扩展多设备拓扑卡片**：界面自适应识别系统的 GPU 数量，动态扩展设备运行信息与健康状态。
* **微秒级独立延迟分析**：精确呈现 CPU 耗时、dGPU 传输/计算延迟、iGPU 吞吐量及负载分担比例条。
* **主线程零负载**：采样与微基准测量全部运行在独立异步工作线程中，零垃圾回收（Zero-GC），绝不拖累游戏渲染帧率。

### 5. C++ 算力全量下沉与 C# 托管层极致瘦身
* **架构解耦**：彻底清除多余的 C# 托管胶水层、中间调度类与冗余缓冲区，严格遵守零堆分配原则。
* **原生下沉**：所有的硬件交互、状态同步、内存屏障与数学插值全量在原生 C++ 层面闭环，消除托管/非托管边界上下文切换开销。

---

## 🖥️ 硬件与系统要求 (System Requirements)

| 项目 | 最低配置 | 推荐配置 |
| :--- | :--- | :--- |
| **操作系统** | Windows 10/11 64-bit | Windows 11 64-bit (最新版本) |
| **游戏版本** | Kerbal Space Program 1.8.1 — 1.12.5 | KSP 1.12.5 |
| **处理器 (CPU)** | 具备 SSE4.2 / AVX 的 64 位 x86 处理器 | 具备 AVX2 / FMA 的现代多核处理器 |
| **独立显卡 (dGPU)** | 支持 Direct3D 11 的独立显卡 | PCIe 4.0/5.0 独显，且主板开启 **Resizable BAR** |
| **集成显卡 (iGPU)** | 可选（系统检测到将自动作为辅算力协同） | Intel UHD/Iris/Xe 或 AMD Radeon Graphics |

> [!TIP]
> **如何最大化直通性能？**
> 建议在电脑主板 BIOS 中开启 **Resizable BAR Support** 与 **Above 4G Decoding**，这将使 CPU 能够直接将数以千计的连续轨道多项式段以总线线速写入 GPU 显存。

---

## 🛠️ 编译与构建说明 (Building from Source)

本项目采用 Visual Studio 2022 与 ClangCL 原生编译工具链构建。

1. **环境依赖**：
   - Visual Studio 2022 (Community / Professional / Enterprise)
   - 安装 C++ 工作负载、Clang-CL 工具链、.NET Framework 4.7.2+ 开发套件
   - Windows 10/11 SDK (10.0.26100.0 或兼容版本)

2. **编译核心动态库与 C# 插件**：
   ```powershell
   # 编译优化版 C++ 动态库 (AVX/FMA 优化配置)
   & "MSBuild.exe" "ksp_plugin\ksp_plugin.vcxproj" /p:Configuration=Release_AVX_FMA /p:Platform=x64 /m

   # 编译基础兼容版 C++ 动态库 (通用 x64 配置)
   & "MSBuild.exe" "ksp_plugin\ksp_plugin.vcxproj" /p:Configuration=Release /p:Platform=x64 /m

   # 编译 C# 游戏适配层
   & "MSBuild.exe" "ksp_plugin_adapter\ksp_plugin_adapter.csproj" /p:Configuration=Release /p:Platform=AnyCPU /m
   ```

3. **部署至 KSP**：
   将生成的产物复制到游戏目录的 `GameData/Principia/` 对应路径下：
   - `ksp_plugin_adapter.dll` -> `GameData/Principia/`
   - `Windows/x64_AVX_FMA/principia.dll` -> `GameData/Principia/Windows/x64_AVX_FMA/`
   - `Windows/x64/principia.dll` -> `GameData/Principia/Windows/x64/`

---

## 📖 原版 Principia 资源与概念参考 (Upstream Resources)

Principia 为《坎巴拉太空计划》带来了真正的 $n$-体引力物理与扩展体引力模型。建议玩家查阅官方维基了解核心概念：
* **概念手册 (Concepts)**: [Concepts Wiki](https://github.com/mockingbirdnest/Principia/wiki/Concepts)（包括绘制坐标系与飞行计划）
* **奔月教程 (A guide to going to the Mun)**: [Mun Guide](https://github.com/mockingbirdnest/Principia/wiki/A-guide-to-going-to-the-Mun-with-Principia)
* **交会对接指南 (Rendezvous Guide)**: [Low Orbit Rendezvous](https://github.com/mockingbirdnest/Principia/wiki/A-guide-to-performing-low-orbit-rendezvous)
* **上游官方仓库**: [mockingbirdnest/Principia](https://github.com/mockingbirdnest/Principia)

---

## 📜 许可证 (License)

本项目继承 Principia 采用 [Apache License 2.0](LICENSE) 开源协议。
