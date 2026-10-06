# Principia 阶段三第二阶段：无锁三缓冲状态总线与 SPSC 异步命令队列实施规划
*(Phase 2: Lock-Free Triple-Buffering State Bus & SPSC Command Ring Buffer Implementation Plan)*

## 一、阶段二目标与定位 (Mission & Objectives)

在 Phase 1 中，我们成功实现了**轨迹显存直通零拷贝管线与非阻塞异步收割回退机制**，斩断了轨迹绘制的 4 层内存中转并消除了 `Precalc -> Early` 阶段的阻塞停顿风险。

**Phase 2（阶段二）的核心使命**：
> **构建原生与托管端之间的高性能“无锁三缓冲状态总线 (Lock-Free Triple-Buffering State Bus)”与“SPSC 异步命令队列 (Single-Producer Single-Consumer Command Ring Buffer)”，并引入“连续多项式切片 (Dense Polynomial Slice)”瞬时微秒级代数采样机制。**
> 
> 彻底切断渲染帧与物理帧的逐帧数据交换强依赖，使 Unity 渲染端在任何帧率下（30/60/144 FPS）均能通过本地极速代数求值（Horner 算法，< 10ns）获取天体与飞船的平滑物理位姿，消除 Mono GC 分配与跨语言调用开销。

---

## 二、架构设计与核心组件 (Architecture & Technical Blueprint)

### 2.1 无锁三缓冲状态通道 (`TripleBufferChannel`)
- **三缓冲原理**：
  - Buffer 0 (Writing)：物理线程独立写入中。
  - Buffer 1 (Ready)：最新生成的就绪切片。
  - Buffer 2 (Reading)：渲染主线程瞬时读取中。
- **原子状态交换 (Packed 32-bit Atomic Exchange)**：
  - 打包编码：`[Byte 2: reader] [Byte 1: ready] [Byte 0: writer]`。
  - 写入端提交：`CommitWrite()` 无锁 CAS 交换 writer 与 ready。
  - 读取端获取：`AcquireRead()` 无锁 CAS 交换 reader 与 ready，耗时 $< 5\text{ ns}$。

### 2.2 稠密输出多项式切片 (`DensePolynomialSlice`)
- 5 阶 Hermite 插值多项式向量：$\mathbf{q}(t) = \sum_{k=0}^{5} \mathbf{c}_k \tau^k$，其中 $\tau = (t - t_0) / (t_1 - t_0)$。
- 四元数球面插值切片参数：$q_{\text{start}}, q_{\text{end}}$。
- C# 瞬时代数求值：Horner 方案 5 次 FMA，耗时 $< 10\text{ ns}$，完全消除每帧 P/Invoke。

### 2.3 SPSC 无锁控制命令环形队列 (`SpscCommandRingBuffer`)
- 环形拓扑：单生产者（Unity 主线程）写入指令，单消费者（后台工作线程/物理引擎）消费指令。
- 采用 64 字节缓存行对齐 (`alignas(64)`) 与顺序一致性/获取-释放内存屏障，避免伪共享 (False Sharing)。
- 命令协议支持：
  - 载具姿态与推力变轨 (`ApplyManoeuvre`)
  - 变轨删除与修改 (`ModifyFlightPlan`)
  - 目标天体切换与参考系变更 (`SetFrame`)
  - 传送与时间跃迁锚定 (`TeleportVessel`)

### 2.4 三级韧性防御与平滑回退 (Resilient Fallback Matrix)
- 保持对官方发布原生二进制 `principia.dll`（173 导出函数）的 100% 动态兼容：
  - 检测三缓冲与命令队列原生接口是否存在。
  - 若原生库导出，则接入纳秒级无锁三缓冲状态总线；
  - 若原生库缺失，安全降级至 C# 侧托管仿真缓冲与标准 P/Invoke 路径，系统平稳运行且 0 崩溃。

---

## 三、文件修改与新建清单 (File Manifest)

### 3.1 C++ 原生层 (Native C++)
1. `ksp_plugin/triple_buffer_channel.hpp` (新建)
   - 无锁原子三缓冲模板与状态控制器。
2. `ksp_plugin/spsc_command_buffer.hpp` (新建)
   - 环形 SPSC 无锁命令队列与紧凑命令结构。
3. `ksp_plugin/dense_polynomial_slice.hpp` (新建)
   - 稠密多项式切片数据结构与 Horner 采样算法。
4. `ksp_plugin/interface_snapshot.cpp` (新建)
   - 导出三缓冲与命令队列 C 风格 ABI 接口。

### 3.2 C# 托管层 (C# Adapter)
1. `ksp_plugin_adapter/DensePolynomialSlice.cs` (新建)
   - 强类型内存对齐多项式切片与 Horner 极速代数求值。
2. `ksp_plugin_adapter/TripleBufferStateBus.cs` (新建)
   - 托管端三缓冲状态总线管理器与无锁读取适配。
3. `ksp_plugin_adapter/SpscCommandBus.cs` (新建)
   - 托管端 SPSC 命令提交器，管理控制指令流。
4. `ksp_plugin_adapter/interface.cs` (更新)
   - 增加 `HasStateBusSupport` 与 `HasCommandBusSupport` 运行时能力感知特性。
5. `ksp_plugin_adapter/interface.generated.cs` (更新)
   - 注册阶段二可选导出函数绑定与安全回退代理。
6. `ksp_plugin_adapter/ksp_plugin_adapter.cs` (更新)
   - 在 `Precalc`、`LateUpdate` 阶段接入三缓冲状态总线，消除主线程卡顿。

---

## 四、验证基准与交付标准 (Verification & Acceptance Criteria)
1. **编译构建**：C# `ksp_plugin_adapter.csproj` 编译通过，0 错误、0 警告。
2. **符号韧性**：在纯原生环境（无新增 C++ 导出）与增强环境（有新增 C++ 导出）下均能安全稳定运行。
3. **部署同步**：运行 `scripts/deploy_to_ksp.ps1`，自动部署至 KSP GameData，哈希比对一致。
4. **性能指标**：状态采样耗时 $< 0.05\text{ ms}$，GC 内存产生量 0 字节/帧。
