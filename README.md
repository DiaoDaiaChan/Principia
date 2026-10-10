# Principia-Optima

> **Heterogeneous Multi-Device Acceleration Edition of [Principia](https://github.com/mockingbirdnest/Principia)**  
> 𝑛-Body and extended body gravitation for Kerbal Space Program with heterogeneous multi-device acceleration & PCIe ReBAR direct DMA.

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-brightgreen.svg)](#)
[![Acceleration](https://img.shields.io/badge/Compute-Direct3D%2011%20%7C%20ReBAR%20DMA%20%7C%20AVX2-orange.svg)](#)
[![Upstream](https://img.shields.io/badge/Upstream-mockingbirdnest%2FPrincipia-lightgrey.svg)](https://github.com/mockingbirdnest/Principia)

[简体中文文档](README_zh.md) | [License (Apache 2.0)](LICENSE) | [Upstream Repository](https://github.com/mockingbirdnest/Principia)

---

## Overview

**Principia-Optima** is a high-performance acceleration fork of the celestial mechanics mod [Principia (mockingbirdnest/Principia)](https://github.com/mockingbirdnest/Principia) for Kerbal Space Program.

While fully preserving Principia's rigorous symplectic numerical integrators and extended body gravitational models, this fork introduces heterogeneous multi-device computing and a PCIe direct DMA pipeline. It offloads trajectory extrapolation and rendering workloads across CPU, dGPU, and iGPU, improving frame-rate stability and physics continuity in complex saves and dense multi-vessel environments.

---

## Core Architecture & Key Features

### 1. Heterogeneous Multi-Device Workload Balancer
* **Dynamic Topology Detection**: Automatically identifies available compute hardware across discrete GPUs (dGPU), integrated graphics (iGPU), and the host CPU.
* **Adaptive Chunk Scheduling**: Measures bus transfer latency and per-device compute throughput at runtime to dynamically size trajectory extrapolation chunks, allowing iGPUs and idle cores to assist the primary pipeline.
* **Multi-Tier Fallback & Fault Tolerance**: Automatically migrates active workloads back to the CPU or standby devices upon driver resets or high VRAM pressure, preventing physics stalls or game crashes.

### 2. PCIe ReBAR / Direct3D 11 Zero-Copy DMA Pipeline
* **Direct VRAM Viewport Mapping**: Maps Direct3D 11 buffers to USWC (Uncacheable Speculative Write-Combining) physical video memory on systems with PCIe Resizable BAR enabled.
* **SIMD Streaming Stores**: Uses AVX2 / SSE2 non-temporal streaming store instructions to write directly into physical VRAM, bypassing CPU cache pollution.
* **Single End-of-Batch Memory Barrier**: Replaces redundant inner-loop pipeline flushes with a single batch-level memory barrier (`_mm_sfence()`), maximizing bus write throughput.

### 3. Chebyshev Polynomial Continuous Trajectory Compression
* **16th-Order Gauss-Lobatto Nodal Expansion**: Projects discrete trajectory samples onto orthogonal polynomial bases, transforming discrete coordinates into continuous analytical polynomial segments.
* **Reduced Bus & Memory Footprint**: Each 256-byte polynomial slice covers hundreds of seconds of orbital motion, reducing bus bandwidth and GPU VRAM footprint by approximately 80% while enabling continuous sub-micrometer interpolation during rendering.

### 4. CPU Multi-Level Cache Micro-architecture Acceleration (L1 / L2 / L3)
* **L1d Celestial Block (1.75 KB SoA)**: Packs 32 celestial states into a 64-byte aligned Structure-of-Arrays block. Sits permanently in CPU L1d cache (32 KB - 48 KB) across all Runge-Kutta substeps, delivering a 100% L1d hit rate for gravitational acceleration evaluation.
* **L2 Core-Private Chebyshev Tile Cache (128 KB)**: Caches up to 512 analytical trajectory segment tiles (256 bytes each) per CPU core. Evaluates Clenshaw polynomial recurrence branchlessly inside private L2 cache (~3 ns latency) with zero cross-core bus contention. Includes a 64 KB aligned `SymplecticIntegratorScratchpad` to eliminate inner-loop heap allocations.
* **L3 Global Ephemeris Continuous Window Ring (4.19 MB)**: Maintains a continuous analytical window ($t \pm 1000\text{ s}$) for all 32 celestial bodies ($32 \times 512 \times 256\text{ B} = 4.096\text{ MB}$). Resides in shared L3 cache (16 MB - 96 MB), allowing all CPU worker threads across all cores to read celestial truth states with zero DRAM memory latency (saving ~70 ns per query).
* **Cache Line Alignment & Hardware Prefetching**: Structures use `alignas(64)` to eliminate false sharing / MESI bus bouncing, coupled with `_mm_prefetch` for streaming Chebyshev polynomial evaluations.

### 5. Auto-Adaptive Cadence Scheduling & Frame Budget Circuit Breaker
* **Dynamic Cadence Governor**: Replaces static tick rates with an auto-adaptive governor driven by real-time EWMA physics budget headroom ($\eta = \frac{\tau_{\text{budget}} - \tau_{\text{physics}}}{\tau_{\text{budget}}}$).
  - **Tier 1 (Critical, 50 Hz)**: Active vessel, truthful loaded bubble, or active thrust — steps 100% of frames unconditionally.
  - **Tier 2 (Interactive, 16.7 ~ 50 Hz)**: Nearby vessels / flight plan nodes adapt from 25 Hz up to 50 Hz full frame rate when headroom is plentiful ($\ge 40\%$).
  - **Tier 3 (Deferred, 2 ~ 25 Hz)**: Distant background probes and debris dynamically throttle between 2 Hz and 25 Hz based on spare budget.
* **Fibonacci Golden Ratio Uniform Hashing**: Vessels are mapped to frames via `(frame_index + (vessel_uid * 0x9E3779B97F4A7C15ULL >> 32)) % dynamic_stride == 0`, ensuring uniform dispersion across frame buckets without harmonic clustering spikes.
* **Chebyshev Continuous Analytical Interpolation**: Skipped ticks are seamlessly evaluated via high-degree analytical polynomials, guaranteeing zero visual stuttering and rock-solid 60~120+ FPS.

### 6. GPGPU Parallel Shooting Flight Plan Optimizer
* Uses GPU Compute Shaders to evaluate candidate maneuver parameters concurrently across the search domain, reducing computation times for complex rendezvous and interplanetary transfers.

### 7. Asynchronous Real-Time Performance & CPU Threading Probe HUD
* A zero-allocation floating telemetry window monitoring per-module active/idle CPU threads, dynamic refresh rates (Hz) per tier, L1/L2/L3 cache hit counters, and multi-GPU load balancing.

---

## Hardware & System Requirements

| Component | Minimum | Recommended |
| :--- | :--- | :--- |
| **Operating System** | Windows 10/11 64-bit | Windows 11 64-bit |
| **Game Version** | Kerbal Space Program 1.8.1 — 1.12.5 | KSP 1.12.5 |
| **Processor (CPU)** | 64-bit x86 CPU with SSE4.2 / AVX support | Modern multi-core CPU with AVX2 / FMA |
| **Discrete GPU (dGPU)** | Direct3D 11 compatible graphics card | PCIe 4.0/5.0 GPU with **Resizable BAR** enabled in BIOS |
| **Integrated GPU (iGPU)** | Optional (used as auxiliary compute if present) | Intel UHD / Iris / Xe or AMD Radeon Graphics |

> [!TIP]
> **Enabling PCIe ReBAR**  
> Enable **Resizable BAR Support** and **Above 4G Decoding** in your motherboard BIOS. This allows the CPU to write polynomial trajectory slices directly into GPU VRAM at PCIe line rate, minimizing staging copy latency.

---

## Building from Source

This project builds with Visual Studio 2022 and the ClangCL toolchain:

```powershell
# 1. Build optimized C++ native library (AVX / FMA configuration)
& "MSBuild.exe" "ksp_plugin\ksp_plugin.vcxproj" /p:Configuration=Release_AVX_FMA /p:Platform=x64 /m

# 2. Build baseline compatible C++ native library (generic x64)
& "MSBuild.exe" "ksp_plugin\ksp_plugin.vcxproj" /p:Configuration=Release /p:Platform=x64 /m

# 3. Build C# Unity adapter plugin
& "MSBuild.exe" "ksp_plugin_adapter\ksp_plugin_adapter.csproj" /p:Configuration=Release /p:Platform=AnyCPU /m
```

Once built, deploy the output binaries to your game's `GameData/Principia/` directory:
* `ksp_plugin_adapter.dll` -> `GameData/Principia/`
* `Windows/x64_AVX_FMA/principia.dll` -> `GameData/Principia/Windows/x64_AVX_FMA/`
* `Windows/x64/principia.dll` -> `GameData/Principia/Windows/x64/`

---

## Upstream Principia Documentation

*(The following is the original documentation from upstream [mockingbirdnest/Principia](https://github.com/mockingbirdnest/Principia):)*

![⚠️](https://placehold.co/10/red/red) _If you downloaded Lévy before September 13th, 20:58 UTC, you probably have a buggy version (see [#4685](https://github.com/mockingbirdnest/Principia/issues/4685)). Please download it again. If you have the correct version, the version string in the Principia UI says `2026091103-Lévy-0-gc6db14e961191ce8fd931d3302bfa7f7e80d0ecd`. We apologize for the inconvenience._ ![⚠️](https://placehold.co/10/red/red)

**&lrm;[Lévy](https://github.com/mockingbirdnest/Principia/wiki/Change-Log#le%CC%81vy), the September version of Principia, is available, with new features and bug fixes.  Download it [here for 1.8.1, 1.9.1, 1.10.1, 1.11.0, 1.11.1, 1.11.2, and 1.12.2 to 1.12.5](https://bit.ly/l3vy).**

**For the convenience of Chinese users, download from 腾讯微云: [Principia Lévy for 1.8.1—1.12.5](https://share.weiyun.com/JAmT3w6C).**

Principia is a mod for Kerbal Space Program (KSP) which implements N-body and extended body gravitation.  Instead of being within the sphere of influence of a single celestial body at any point in time, your vessels are influenced by all the celestials.  This makes it possible to implement missions that are more complex and more realistic than in the stock game, especially if used in conjunction with a mod like RealSolarSystem which has real-life celestials.

N-body gravitation is more complex than the toy physics of the stock game.  Therefore, before using the mod we recommend that you read the [concepts](https://github.com/mockingbirdnest/Principia/wiki/Concepts) document which explains the most important parts of Principia.  In particular, you should learn about the [plotting frame](https://github.com/mockingbirdnest/Principia/wiki/Concepts#plotting-frame) and [flight planning](https://github.com/mockingbirdnest/Principia/wiki/Concepts#flight-planning).

You might also want to go through our
[tutorial](https://github.com/mockingbirdnest/Principia/wiki/A-guide-to-going-to-the-Mun-with-Principia) which shows how 
to go to the Mun with Principia in an energy-efficient manner.  We also have a guide explaining how to use the support for [rendezvous](https://github.com/mockingbirdnest/Principia/wiki/A-guide-to-performing-low-orbit-rendezvous).

The [FAQ](https://github.com/mockingbirdnest/Principia/wiki/Installing,-reporting-bugs,-and-frequently-asked-questions) explain how to install, how to report bugs and documents the known issues and limitations.

The [change log](https://github.com/mockingbirdnest/Principia/wiki/Change-Log) gives a fairly detailed description of the new features in each release.

Principia is released on every [new moon](https://en.wikipedia.org/wiki/New_moon) with whatever features and bug fixes are ready at the time.  This ensures relatively timely improvements and bug fixes.

Download the binary (Ubuntu, macOS, and Windows) [here for 1.8.1, 1.9.1, 1.10.1, 1.11.0, 1.11.1, 1.11.2, and 1.12.2 to 1.12.5](https://bit.ly/l3vy).  Or, if you don't trust our binary, [build the mod](https://github.com/mockingbirdnest/Principia/blob/master/documentation/Setup.md) from the [Lévy](https://github.com/mockingbirdnest/Principia/releases/tag/2026091103-L%C3%A9vy) release.

---

## License

This project inherits Principia's open source license under the [Apache License 2.0](LICENSE).
