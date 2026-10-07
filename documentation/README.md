# documentation/ 文档索引 (Documentation Index)

> 本目录是本分支（`feature/multi-level-caching-pipeline`）新增文档的**唯一存放地**。
> 最后更新：2026-10-07。新文档请先在本索引登记，再动手写。

## 单一事实来源 (Single Sources of Truth)

阅读顺序建议：**先看这两份，其余按需查阅。**

| 角色 | 文档 | 说明 |
| :--- | :--- | :--- |
| **实施追踪（是什么状态）** | [`GPGPU_Implementation_TODO.md`](GPGPU_Implementation_TODO.md) | 唯一的落地进度事实来源：在役模块、已移除模块档案、里程碑日志、崩溃修复记录、已知问题 |
| **架构规格（该怎么做）** | [`Principia_GPGPU_Native_Acceleration_Specification.md`](Principia_GPGPU_Native_Acceleration_Specification.md) | GPGPU 加速的工程规格：6 模块实施矩阵（含落地状态）、内核与缓冲区规范、反模式清单 |
| **长期路线图（往哪去）** | [`Principia_Long_Term_Performance_Architecture_Roadmap.md`](Principia_Long_Term_Performance_Architecture_Roadmap.md) | 四大支柱架构与 Phase 0~4 宏观排期；开头含 Roadmap 编号 ↔ 实施编号映射表 |

## 支撑文档 (Supporting Docs)

| 文档 | 类型 | 状态 |
| :--- | :--- | :--- |
| [`audit_reports/01_csharp_rendering_cache_audit.md`](audit_reports/01_csharp_rendering_cache_audit.md) | C# 渲染管线审计（L1/L2 缓存设计依据） | 现行 |
| [`audit_reports/02_cpp_physics_ephemeris_audit.md`](audit_reports/02_cpp_physics_ephemeris_audit.md) | C++ 物理内核/星历/序列化审计（L2/L3/Sidecar 设计依据） | 现行 |
| [`Principia_Unity_Decoupling_Architecture.md`](Principia_Unity_Decoupling_Architecture.md) | Unity 解耦架构总纲 | 现行 |
| [`Principia_Decoupling_Phase1_Implementation_Audit.md`](Principia_Decoupling_Phase1_Implementation_Audit.md) | 解耦第一阶段实施审计 | 现行 |
| [`Precalc_Performance_Optimization_Report.md`](Precalc_Performance_Optimization_Report.md) | Precalc 性能优化报告 | 现行 |

## 历史归档 (Historical / Superseded)

> 这些文档描述的是**被 `e4163820f` 清理之前的旧管线**，其中引用的模块文件（Porkchop、DEM、GeopotentialGrid、SRP、Conjunction 等）已不存在于代码树。仅供考古，**不得作为实施依据**。

| 文档 | 说明 |
| :--- | :--- |
| [`Principia_Low_Precision_GPGPU_Architecture_Audit.md`](Principia_Low_Precision_GPGPU_Architecture_Audit.md) | 旧 12 模块 GPGPU 架构审计，其结论已沉淀进新规格说明书 |
| [`Principia_GPU_Acceleration_Architecture.md`](Principia_GPU_Acceleration_Architecture.md) | 旧 GPU 加速架构设计 |
| [`Principia_GPU_Implementation_Code_Audit.md`](Principia_GPU_Implementation_Code_Audit.md) | 旧管线代码审计，多处引用已删除文件 |

## 相关目录 (Related Directories Outside documentation/)

| 目录 | 说明 |
| :--- | :--- |
| `plan/` | 实施编号 Phase 1~6 的分阶段落地计划书（提交级粒度） |
| `fix/` | **已移除**（2026-10-07）：曾存放与 documentation/ 完全重复的 6 份文档副本 |

## 维护守则

1. **进度只写 TODO 表**：任何"XX 已完成 / 已移除 / 崩溃修复"必须同步进 `GPGPU_Implementation_TODO.md`，其余文档不得各自维护进度状态。
2. **阶段计划进 `plan/`**：每批次实施前先落一份 `phaseN_*.md` 计划书，完成后在 TODO 表登记 commit。
3. **删除代码须同步文档**：删模块/文件时，把引用它的文档移入"历史归档"或加注，防止幽灵引用。
4. **提及 "Phase N" 必须注明体系**：Roadmap 编号（0~4）或实施编号（1~6），映射表见 Roadmap 文档开头。
5. **禁止在 documentation/ 之外复制本目录文档**（2026-10-07 已清理过一次 `fix/` 副本）。
