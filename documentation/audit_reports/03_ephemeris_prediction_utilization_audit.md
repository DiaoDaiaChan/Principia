# 星历预测（Ephemeris Prolongation）利用现状审计

> 问题：当前代码有没有利用星历预测功能？
> 结论：**利用了，而且是两条并行的链路**——① C++ 原生 `EphemerisPacer` 独立守护线程；
> ② C# 遗留的"载体飞船飞行计划"推演。但两者的**覆盖窗口都只有 100 天**，
> 而轨迹绘制却要求绘制到预测/飞行计划末端（可达数年），这个**语义错配**正是
> 上篇报告（`02_…_root_cause.md`）中"单次重绘极重"的直接放大器。

---

## 一、链路 A：C++ 原生星历守护线程（当前生效路径）

| 环节 | 代码位置 | 说明 |
| :-- | :-- | :-- |
| 创建与启动 | `plugin.cpp:301-303`、`1697-1701` | `Instantiate`/反序列化时 `new EphemerisPacer(ephemeris_)` 并 `Start()` |
| 守护线程主体 | `ephemeris_pacer.cpp:31-83` | `MakeStoppableThread`，循环 50ms 醒一次，`ephemeris_->Prolong(target, 4096 steps)` 分片推进 |
| 触发条件 | `ephemeris_pacer.cpp:58-61` | `force \|\| t_max − t_now < refresh_threshold` 才推演 |
| 默认视界 | `ephemeris_pacer.hpp:36-37` | `lookahead_horizon = 100 天`，`refresh_threshold = 30 天` |
| 时长同步 | `plugin.cpp:797-798` | `NotifyCurrentTime(current_time_)`，每步游戏时间告知守护线程 |
| 控制接口 | `ephemeris_pacer.cpp:119-137` | `ForceProlong` / `AdjustHorizon` / `TogglePause` |
| 能力探测 | `Interface.HasNativeEphemerisPacer()` | `rolling_ephemeris_driver.cs:30` 等所有查询处据此切换到原生模式 |

**关键点：`Prolong` 只推进 N 体星历（天体轨道）到 `t_now + 100 天`，
它不推进、也不限制飞船的 `prediction` / 飞行计划。**

## 二、链路 B：C# 遗留载体模式（原生不可用时的回退）

`rolling_ephemeris_driver.cs`：
- `Update()`（200-228 行）按 realtime 节流（warp 0.5s / 常规 2s）；
- `EnsureHorizon()`（230-293 行）优选"载体飞船"（`FindBestCarrier`，295-353 行，按船型/情境打分），
  然后 `FlightPlanCreate` 或 `FlightPlanSetDesiredFinalTime(carrier, ut + lookahead_horizon)`
  ——**用一艘真实飞船的飞行计划去驱动星历数值积分**；
- 当原生 pacer 可用时 `Update()` 在第 205-208 行直接 `return`，
  且 `ShouldSuppressPlot`/`IsCarrier` 也直接返回 false（142-160 行）——
  **载体机制已完全旁路，只剩 UI 上的历史包袱**。

## 三、绘制侧如何"利用"星历预测

### 3.1 双向天体轨迹：过去靠 reanimation，未来靠延长到预测末端
```cpp
// interface_planetarium.cpp:539  过去轨迹
plugin->RequestReanimation(plugin->CurrentTime() - max_history_length * Second);
// interface_planetarium.cpp:599-612  未来轨迹
Instant const final_time = vessel.has_flight_plan()
    ? std::max(GetFlightPlan(*plugin, vessel_guid).actual_final_time(),
               prediction_final_time)          // ← 取预测/飞行计划较远者
    : prediction_final_time;
celestial_view.Restrict(plugin->CurrentTime(), final_time);
```
未来天体轨迹的**时间跨度由飞船预测决定**，而不是由 100 天的星历视界决定。
两者不一致时，`PlotMethod4` 会对"星历已覆盖段 + 未覆盖段"做不同处理，
这也是历表未覆盖时曲线突然变直/变折的常见成因。

### 3.2 飞船自身预测：`UpdatePrediction` → `RefreshPrediction`
```csharp
// ksp_plugin_adapter.cs:993   节流：机动中 10Hz / 巡航 2.5s / warp 5s
double interval = is_maneuvering ? 0.100 : (is_warping ? 5.000 : 2.500);
```
```cpp
// plugin.cpp:973-1001
void Plugin::UpdatePrediction(std::vector<GUID> const& vessel_guids) const {
  ...
  target_vessel->RefreshPrediction();
  for (auto const vessel : predicted_vessels)
    vessel->RefreshPrediction(target_vessel->prediction()->back().time);
  ...
}
// vessel.cpp:558-584
void Vessel::RefreshPrediction() {
  // synchronous_ 为真时同步 FlowPrognostication（阻塞主线程）；
  // 否则 put/Start/Get 走后台姑且称"异步"
  ...
}
// integrators.cpp:84-92
DefaultPredictionParameters(): max_steps = 1000, tol = 1 m / 1 m/s
// plugin.cpp:1016-1035
ExtendPredictionForFlightPlan(): 有目标船时 max_steps ×= 4（≤ 1<<24）
```
**预测的有效时长由 `max_steps × 自适应步长` 决定**：默认 1000 步，
近地轨道只够几小时~几天；只有在"存在目标飞船 + 飞行计划过长"时才会 ×4 扩步数。
注意 HUD 里"前瞻余量 +N 年"显示的是**线性外推的多年跨度**，
预测积分是**自适应步长**的，两者完全不同量级。

### 3.3 飞行计划分段绘制
`plotter.cs:213-252` → `PlotFlightPlanSegmentDirect` → `PlanetariumPlotFlightPlanSegment`
（`interface_planetarium.cpp:327` 附近），飞行计划段以预测末端为界。

## 四、错配清单（为什么"利用了"却仍然慢）

| 编号 | 错配 | 证据 | 后果 |
| :-- | :-- | :-- | :-- |
| M1 | 星历视界 100 天 vs 绘制要求数年 | `ephemeris_pacer.hpp:36` ↔ `interface_planetarium.cpp:601-604` | 未来天体轨迹大部分落在星历覆盖外，细分代价高、结果退化为折线 |
| M2 | 预测 `max_steps=1000` 与"多年"显示不一致 | `integrators.cpp:89`、`performance_probe.cs:207` | HUD"前瞻 +N.YY 年"是外推值，实际预测只到几天，未来轨迹被迫外推 |
| M3 | 每次重绘都发起 `RequestReanimation` | `interface_planetarium.cpp:539` | R1 的"每帧重绘"叠加此处 → 每帧都在请求历史重构 |
| M4 | 载体模式与原生模式功能重叠 | `rolling_ephemeris_driver.cs:205-208,142-160` | 代码面存在两套推演语义，UI/日志仍显示"载体飞船"，易误判缓存行为 |
| M5 | 预测异步/同步路径不统一 | `vessel.cpp:570-580` | `synchronous_` 为真时 `FlowPrognostication` 阻塞主线程，与"异步预测"假设冲突 |

## 五、建议

1. **统一视界语义**：绘制侧把 `final_time` 截断到 `ephemeris_->t_max()`（或
   `plugin->ephemeris_pacer()->target_extended_time()`），并把"超出星历的段"用
   显式视觉降级（浅色虚线）表达，而不是让 `PlotMethod4` 硬算到外推区。
2. **把预测步数上限与视界解耦**：`ExtendPredictionForFlightPlan` 的 ×4 逻辑改为
   按"目标时间 / 步长估计"动态设定，避免 `max_steps=1000` 与多年飞行计划打架。
3. **给 `RequestReanimation` 加节流/去重**：按 `(celestial_index, desired_first_time)`
   记录上次请求，时间推进未超阈值时不再重复发起。
4. **清理载体模式**：原生 pacer 已是默认路径，建议把 `FindBestCarrier` /
   `suppress_plot_on_active_` 等死代码删除或明确标注为 legacy，避免后续误读。
5. **HUD 修正**：`performance_probe.cs:207-208` 的"+N.YY 年"应改为显示
   `EphemerisPacer::target_extended_time()`（真实覆盖终点），而非 `current_horizon_years`
   的线性外推，否则会持续误导对缓存窗口的判断。

---

## 复核修正（2026-10-07，修复实施前的源码级验证）

对第五节建议逐条做了源码复核，三条被推翻或降级，**结论：修复不需要动 C++**：

1. ~~建议 1（final_time 截断到 `ephemeris_->t_max()`）~~：**不需要**。
   `physics/trajectory_view_body.hpp:50-51` 的 `Restrict` 是与视图自身区间**求交**，
   而初始视图覆盖整条轨迹（`TrajectoryView(trajectory)` 构造），
   所以未来轨迹端点天然被 `celestial_trajectory.t_max()` 截断——不存在越界求值。
   M1 的"覆盖外求值/幽灵几何"不成立；但"弧长被 pacer 视界拉长到 100 天（用户上调后可达 20 年）、
   单次 PlotMethod4 走弧昂贵"的**性能问题真实存在**，已由报告 02 的每帧刷新预算解决。
2. ~~建议 3（`RequestReanimation` 节流去重）~~：**不需要**。
   `Ephemeris::RequestReanimation`（`physics/ephemeris_body.hpp:265+`）只是互斥锁记账 +
   启动异步 reanimator 线程，单次调用廉价；且本 fork 的 `Ephemeris::Prolong`
   已有读锁快速路径避免与后台推演线程争锁。M3 降级为非问题。
3. ~~建议 5（HUD 显示外推值）~~：**不成立**。
   `interface_ephemeris.cpp:14-20` 的 `EphemerisGetHorizonSeconds` 返回
   `pacer->current_horizon()` = `t_max − current_time`（真实覆盖余量），
   HUD 的"+N.YY 年"在原生模式下是真实值。M2 仅保留"预测 max_steps=1000 与多年视界
   不同量级"的观察，不影响正确性。
4. 建议 2（预测步数动态化）、建议 4（载体模式清理）：**未实施**。
   前者涉及预测积分语义、风险高于收益；后者是原生 pacer 缺失时的真实回退路径。

最终修复清单见报告 02 的"修复实施记录"一节（全部在 C# 侧，编译验证通过）。
