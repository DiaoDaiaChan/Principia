# 天体轨迹缓存失效根因分析（Celestial 61ms / Vessel 8ms 每帧全量重绘）

> 现象：勾选"未固定天体"时 `天体轨迹 (Celestial)` 平滑值高达 **61.32 ms（峰值 83.5 ms）**，
> 取消勾选后帧率恢复，但 `飞船轨迹 (Vessel)` 仍有 **8.14 ms（峰值 8.9 ms）**——
> 平滑值 ≈ 峰值，说明这些路径在**几乎每一帧**都做全量 P/Invoke 重算 + 网格重建，
> 多级缓存形同虚设。本文给出经过代码逐行核实的失效原因。

---

## 结论速览

| 编号 | 根因 | 位置 | 直接后果 |
| :-- | :-- | :-- | :-- |
| R1 | **时间推进时用 realtime 节流，加速档窗口 0.05s < 帧周期 → 判定逻辑每帧必失效（正反馈死循环）** | `plotter.cs:155,417` | 加速时缓存命中率 ≈ 0%，Celestial 每帧 61ms |
| R2 | **所有天体条目刷新相位同步，无每帧预算** | `plotter.cs:425-493` | 每 2s 一帧吃下全部 ~34 次 PlotMethod4 → 83.5ms 峰值 |
| R3 | **ScaledSpace 原点漂移仍触发全局 InvalidateAll（代码与自己的注释矛盾）** | `plotter.cs:94-99` | 轨道上每 ~3s 全缓存清零一次（83.5ms 峰值来源之一） |
| R4 | **单次天体重绘本身极重，放大 R1/R2/R3** | `interface_planetarium.cpp:539` 等 | 每次重绘含 RequestReanimation + 多年弧长细分 + 万级顶点网格重建 |
| R5 | **`show_celestial_trajectory` 每天体每帧重新分配 FrameParameters** | `ksp_plugin_adapter.cs:421-425` | 取消勾选后 Celestial 残留 1.19ms（纯分配/GC 开销） |

---

## R1（主因）：realtime 节流窗口在加速档小于帧周期

三处复用判据全部使用 **realtime 年龄** 而非 UT/帧数：

```csharp
// plotter.cs:155  飞船历史轨迹
double psychohistory_interval = is_warping ? 0.05 : 0.20;
// plotter.cs:417  天体轨迹
double celestial_interval = is_warping ? 0.05 : 2.0;
// plotter.cs:58   等势线
double eq_interval = is_warping ? 0.10 : 0.50;
```

复用条件（以天体过去轨迹为例，plotter.cs:425-427）：

```csharp
bool can_reuse_past = past_entry.is_valid &&
    (!time_is_advancing ||
     current_realtime - past_entry.last_update_realtime < celestial_interval);
```

致命交互：一旦帧周期 ≥ 节流窗口，**每一帧**都满足
`current_realtime - last_update_realtime ≥ interval`，于是每帧都对全部天体执行
`PlanetariumPlotCelestialPast/FutureTrajectory` + 全量网格重建。这构成正反馈：

```
全量重绘(61ms+) → 帧周期拉长到 60~90ms → 必然超过 0.05s 窗口
→ 下一帧再次全量重绘 → 帧率锁死在低位，缓存命中率 0%
```

截图 2 中 `Celestial 平滑 61.32 ≈ 峰值 83.5`、截图 1 中 `Vessel 平滑 8.14 ≈ 峰值 8.9`
（平滑值是 α=0.08 的 EMA，若缓存按设计命中，平滑值应远低于峰值），
数学上只有"几乎每帧都 miss"才能产生这种曲线。注意 `is_time_advancing`
在正常飞行时（plugin 时间落后于 KSP 时间）**每帧都为 true**（`ksp_plugin_adapter.cs:1106-1119`），
只有暂停才走 100% 复用快路径——所以只要在推进时间且处于加速档，R1 必然生效。

HUD 验证方法：加速时观察"缓存吞吐： L1暂停/同UT复用 vs C++实时直出"，
若 `C++实时直出`（MissCount）每帧 +~2N（N 为可见天体数）即坐实。

## R2：刷新相位同步，无每帧预算

`InvalidateAll`（初始填充、R3 触发、参考系切换等）把所有条目一起置为 invalid，
它们在**同一帧**全部重算，之后又在同一时刻集体到期：
1x 下每 ~2s 出现一帧承担全部 ~34 次 PlotMethod4（83.5ms 峰值）。
缓存层没有"每帧最多刷新 K 个条目、超龄条目先画旧网格"的预算机制，
重绘代价完全不受控地集中爆发。

## R3：ScaledSpace 原点检查仍触发全局失效（代码与注释矛盾）

```csharp
// plotter.cs:88-99
// In KSP ScaledSpace, meshes ... are completely invariant to Krakensbane/FloatingOrigin shifts.
// Updating last_scaled_space_origin_ without calling InvalidateAll() eliminates
// cyclic frame spikes that previously occurred every ~3 seconds in orbit!
Vector3d current_scaled_space_origin = ScaledSpace.ScaledToLocalSpace(Vector3d.zero);
if ((current_scaled_space_origin - last_scaled_space_origin_).sqrMagnitude > 1.0) {
  InvalidateAll();                       // ← 注释声称已消除，代码却仍然全量失效
  last_scaled_space_origin_ = current_scaled_space_origin;
}
```

注释明确论证了"网格在 ScaledSpace 中、对 FloatingOrigin 漂移不变"，
但代码保留了 `InvalidateAll()`。轨道飞行中 Krakensbane 周期性平移原点，
该分支每 ~3s 命中一次，一次性清空：天体 L1 缓存、psychohistory、prediction、
飞行计划分段、等势线，并强制 `frame_key_dirty_` 重建——下一帧全量重算。
HUD"3D 网格多级缓存 … 失效： N"若以 ~0.3/s 速度爬升即坐实。

## R4：单次重绘的开销被多个改动放大

- C++ 侧每次天体过去轨迹重绘都会 `plugin->RequestReanimation(CurrentTime − history_length)`
  （`interface_planetarium.cpp:539`），对每个天体、每次刷新都发起历史重构请求；
- 未来轨迹终点延伸到 prediction/飞行计划末端，而后台 RollingEphemerisDriver 把星历
  向前推展了**数年**（HUD"前瞻余量 +N.YY"），`PlotMethod4` 要按
  `resolution_multiplier = 0.75`（`ksp_plugin_adapter.cs:3572`，比原版更细）自适应细分
  多年弧长，单个天体可产出数万顶点；
- 托管侧每次刷新（`Plotter.CachedMeshEntry.UpdateMesh`，plotter.cs:602-699）要做
  颜色逐顶点填充 + O(N) AABB 扫描 + `mesh.Clear/vertices/colors/SetIndices` 全量上传，
  且顶点缓冲已从 1 万扩到 65536（`NativeZeroCopyTrajectoryPipeline.DefaultCapacity`），
  单条轨迹的重建代价随之放大 ~6 倍。

## R5：`show_celestial_trajectory` 每天体每帧分配

```csharp
// ksp_plugin_adapter.cs:421-425
public bool show_celestial_trajectory(CelestialBody celestial) {
  return plotting_frame_selector_.pinned[celestial] ||
         !main_window_.frames_that_hide_unpinned_celestials.Contains(
             plotting_frame_selector_.FrameParameters());   // ← 每天体每帧 new + 两个 int[]
}
```

`PlotCelestialTrajectories`（plotter.cs:363-369）特意缓存了 FrameKey 以避免
"每帧分配新对象和两个 int[]"，但同一个判定又经由 `show_celestial_trajectory`
对**每个天体、每帧**重新执行 `FrameParameters()` 分配 + HashSet 值探测。
17+ 天体时是每帧数十次分配——这就是取消勾选后 Celestial 仍残留 1.19ms 的主要成分。

---

## 修复建议（按收益排序）

1. **把节流从 realtime 改为 UT 量化 + 每帧刷新预算**（解决 R1/R2）：
   - 复用判据改为 `current_time_ut - entry.last_update_ut > ut_interval`，
     `ut_interval` 随加速档缩放（如 `max(2.0, k × 每帧ΔUT)`），加速时不再因帧周期
     变长而失效；
   - 在 `PlotTrajectories` 帧首建立预算计数器（如每帧最多刷新 2 个天体条目），
     超龄但无预算的条目**继续用旧网格 Draw()**（锚点偏移机制本就补偿了天体位移）。
     最坏帧代价从 ~34 次 PlotMethod4 降为 ~1-2 次。
2. **删除 plotter.cs:96-99 的 `InvalidateAll()`**：只更新 `last_scaled_space_origin_`
   （注释本身的结论），消除每 ~3s 的周期性全清零（解决 R3 / 83.5ms 峰值）。
3. **应急止血**：把加速档 realtime 间隔从 0.05s 提到 ≥0.5s
   （`celestial_interval`、`psychohistory_interval`），可立即脱离"帧周期>窗口"
   的死循环，但治本仍需第 1 条。
4. **缓存 `show_celestial_trajectory` 的判定**：在 `frame_key_dirty_` 门控下
   每帧算一次 `bool frame_hides_unpinned`，随参数下传递归（解决 R5）。
5. （可选）对天体轨迹单条顶点数设上限（8-16K），把 0.75 细分系数只留给飞船自身轨迹。

验证清单：加速状态下 `MissCount` 不再每帧增长、`失效： N` 停止爬升、
`天体轨迹 (Celestial)` 平滑值降到 <2ms 且峰值 <10ms。

---

## 修复实施记录（2026-10-07，已编译验证）

修复全部落在 C# 侧（`plotter.cs`、`ksp_plugin_adapter.cs`），C++ 无需改动
（详见报告 03 的复核修正节）。`dotnet build -c Release` 通过：0 错误、
改动文件 0 警告（仅余 1 条与本次无关的既有 CS0414）。

| 根因 | 修复 | 位置 |
| :-- | :-- | :-- |
| R1 realtime 节流死循环 | warp 档间隔提高：天体 0.05→**0.50s**、psychohistory 0.05→**0.25s**、等势线 0.10→**0.25s**（1x 档不变），间隔重新高于帧周期，退出"每帧必 miss"正反馈 | `plotter.cs` |
| R2 相位同步爆发 | 新增**每帧刷新预算**：`MaxCelestialRefreshesPerFrame = 2`，每帧最多重绘 2 个天体条目；超预算条目继续用旧网格 `Draw()`（锚点偏移保证贴合）；`age ≥ 4×interval` 的条目强制刷新以防饥饿；未构建条目也计入预算，地图打开时逐帧填充而非一次性 80ms+ 尖峰 | `plotter.cs` `TryConsumeCelestialRefresh()` + `PlotTrajectories` 帧首重置 |
| R3 原点漂移全清零 | **删除**原点检查中的 `InvalidateAll()` 及 `last_scaled_space_origin_` 字段；网格在 ScaledSpace 中对 FloatingOrigin 不变、Planetarium 每帧重建，无需任何失效 | `plotter.cs` `PlotTrajectories` |
| R4 单次重绘过重 | 由 R1/R2/R3 的频次控制间接解决（每帧重绘次数有硬上限）；未降低 0.75 细分系数（保留视觉质量） | — |
| R5 每天体每帧分配 | `show_celestial_trajectory(body, frame_hides_unpinned)` 重载 + `FrameHidesUnpinnedCelestials()` 每帧只算一次并随递归下传 | `ksp_plugin_adapter.cs:421` 附近、`plotter.cs` |
| 报告 03 M4 载体模式清理 | **未实施**：它是原生 EphemerisPacer 不可用时的真实回退路径，删除风险大于收益 | — |

预期效果：加速状态下 Celestial 平滑值应降到 <2ms、峰值 <10ms（单帧最多 2 次
P/Invoke 重绘）；轨道上不再出现每 ~3s 的周期性尖峰；`失效: N` 计数应基本停涨。

---

## 第二轮修复（2026-10-08）：失效泛洪的韧性治理

第一轮修复后实测遥测（`PrincipiaTelemetry_latest.json`）显示：
- **Celestial 已治愈**（0.2-0.7ms，94 个 L1 条目按 2s 设计速率错峰刷新）；
- 但 `plot_invalidations` 仍以 **~16-18 次/秒** 爬升（约每 2 帧一次全量 InvalidateAll），
  Vessel 路径（psychohistory/prediction/飞行计划/目标船/等势线，约 10-18 个条目）
  **无预算保护**，每次失效后下一帧全部重建 → Vessel 平滑 5.92ms ≈ 峰值 7.6ms。

**根因定位**：`Plotter.InvalidateAll()` 被某个调用方以 ~16Hz 频率调用。
调用点仅有 4 处（guid 变化 / SetPlottingFrame 命令 / TriggerPrediction 命令 /
参考系切换），均已加装原因计数器（见下）。

**本轮修复（"失效后画旧网格 + 重建下限"语义，spam-immunity）**：

1. **`CachedMeshEntry` 新增 `has_content`**：`Invalidate()` 不再清空锚点与内容，
   仅置 `is_valid=false`；`Draw()` 改由 `has_content` 门控——失效条目继续
   以旧网格 + 锚点偏移绘制，直到重建间隔到期。
2. **Vessel 全路径接入重建下限**：新增 `NeedsRebuild()`（psychohistory 主/目标），
   prediction / 目标 prediction / 飞行计划分段 / 等势线分别接入 0.20s 重建下限；
   用户编辑（`fp_dirty`）仍即时重建保证响应性。
3. **`InvalidateAll` 不再重置等势线全局时间戳**（原重置使等势线绕过了下限）。
4. **psychohistory 间隔自适应**：EMA 实测重建耗时（7 天默认历史约 5-8ms），
   间隔在 [0.20s, 1.00s] 内按 `0.08 × 重建耗时` 自伸缩，摊销成本 <8% 帧时间。
5. **guid 空翻守卫**：`main_vessel_guid == null` 的瞬态帧不再翻转
   `last_main_vessel_guid_`，消除 null↔guid 空翻导致的反复全量失效。
6. **失效来源诊断**：`InvalidateAll(reason)` 带调用方标签，
   `PerformanceProbe` 按原因计数，HUD"失效来源"行实时显示
   （vessel-change / command:SetPlottingFrame / command:TriggerPrediction /
   plotting-frame-change），用于下一轮的源头根治。

构建验证：`dotnet build -c Release` 0 错误（2026-10-08）。

**实测遥测附带发现**：游戏内 principia.dll 为旧版（`HasNativeEphemerisPacer()`=false，
星历推演处于**遗留载体模式**，载体 `Ast.ONK-719`）；原生 EphemerisPacer 需重编 C++ 后生效。

**已知余项（已于第三轮解决）**：
- ~16Hz 失效泛洪的**源头调用方**已根治（消除 UpdatePlottingFrame 与 ProcessCommandPacket 双重失效）；
- 原生 EphemerisPacer C++ 与 C# 适配器已全量重编并就绪。

---

## 第三轮根治（2026-10-09）：C++ 原生星历演算落地与遗留模式彻底清除

### 1. 彻底移除遗留"载体模式"（Carrier Vessel Deprecation）
- **C# 载体逻辑全量删除**：[`ksp_plugin_adapter/rolling_ephemeris_driver.cs`](file:///c:/Users/43701/Documents/github/Principia/ksp_plugin_adapter/rolling_ephemeris_driver.cs) 中彻底移除载体飞船（carrier vessel）搜索、打分筛选以及通过飞行计划强行延展星历（flight plan prolongation）的全部遗留逻辑。
- **纯粹的 C++ Pacer 门面**：`RollingEphemerisDriver` 转型为轻量门面（Façade），直接对接 C++ 原生 `EphemerisPacer` 独立后台守护线程（`plugin_.EphemerisGetHorizonSeconds()`、`EphemerisAdjustHorizon()`、`EphemerisTogglePause()` 等）。
- **清理各处载体抑制钩子**：[`ksp_plugin_adapter/flight_planner.cs`](file:///c:/Users/43701/Documents/github/Principia/ksp_plugin_adapter/flight_planner.cs) 与 [`ksp_plugin_adapter/ksp_plugin_adapter.cs`](file:///c:/Users/43701/Documents/github/Principia/ksp_plugin_adapter/ksp_plugin_adapter.cs) 移除针对载体飞船的绘图抑制与特殊分支。

### 2. 根除 ~16Hz 失效泛洪（Duplicate Invalidation Flood Elimination）
- **消除 SPSC 命令总线重复失效**：`UpdatePlottingFrame` 本身在切换参考系时已同步调用 `plotter_?.InvalidateAll("plotting-frame-change")`。原逻辑在同一处又向 `SpscCommandBus` 推送了 `SetPlottingFrame` 命令，下一帧消费时再次触发 `plotter_?.InvalidateAll("command:SetPlottingFrame")`，造成双重全量失效。现已移除多余的命令推送，并在 `ProcessCommandPacket` 中将冗余失效摘除。
- **GUID 空翻守卫强化**：`PredictedVessel()` 瞬态为 null 时不覆盖 `last_main_vessel_guid_`，彻底隔绝地图视图与追踪站中由选定目标切换引发的缓存击穿。

### 3. C++ 原生工具链适配与编译修复
- **Unicode 上标数学符号编译支持**：Principia C++ 代码中广泛使用 Unicode 上标字符（如 `²`、`⁻¹`）。通过部署 LLVM 23.1.3 `clang-cl.exe` 驱动，配置 `-Wno-mathematical-notation-identifier-extension`，完美规避 MSVC cl.exe 对 P1949 的严格限制（C3872 报错）。
- **防御 Windows `<d3d11.h>` 宏污染**：修复 `<d3d11.h>` / `<combaseapi.h>` 中 `#define interface struct` 破坏 `namespace interface` 导致的符号灾难，增加 `#undef interface` 阻断污染。
- **修复 `interface_ephemeris.cpp` 命名空间**：规范引入 `quantities::_si` 与 `Second` 导出定义。
- **自动化部署管道更新**：[`scripts/deploy_to_ksp.ps1`](file:///c:/Users/43701/Documents/github/Principia/scripts/deploy_to_ksp.ps1) 升级为同步部署 `ksp_plugin_adapter.dll` 和原生 `principia.dll` / `principia.pdb` 至 KSP `GameData/Principia/Windows/x64/`。

