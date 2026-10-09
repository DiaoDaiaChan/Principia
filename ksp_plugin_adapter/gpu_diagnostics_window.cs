using System;
using System.Collections.Generic;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

internal class GpuDiagnosticsWindow : SupervisedWindowRenderer {
  private readonly PrincipiaPluginAdapter adapter_;
  private Vector2 scroll_position_ = Vector2.zero;
  private Vector2 log_scroll_position_ = Vector2.zero;
  private bool show_benchmark_logs_ = true;
  private bool show_module_descriptions_ = false;

  private const int ColumnWidthModule = 210;
  private const int ColumnWidthCategory = 70;
  private const int ColumnWidthStatus = 85;
  private const int ColumnWidthCount = 65;
  private const int ColumnWidthLastMs = 70;
  private const int ColumnWidthAvgMs = 70;
  private const int ColumnWidthResource = 130;
  private const int ColumnWidthAction = 75;

  public GpuDiagnosticsWindow(PrincipiaPluginAdapter adapter) : base(
      adapter,
      GUILayout.MinWidth(780),
      GUILayout.MinHeight(560)) {
    adapter_ = adapter;
  }

  protected override string Title =>
      "Principia GPGPU 异构加速诊断中心 (Heterogeneous Acceleration Diagnostics)";

  public void RenderButton() {
    string btn_text = Shown() ? "▲ GPU 诊断" : "▼ GPU 诊断";
    if (GUILayout.Button(btn_text, GUILayoutWidth(3))) {
      Toggle();
    }
  }

  protected override void RenderWindowContents(int window_id) {
    PrincipiaGPGPUContext.EnsureInitialized();

    using (new GUILayout.VerticalScope()) {
      RenderHardwareBanner();
      RenderDmaAndGpuSelectionSection();
      RenderPrecisionAndGovernanceSection();
      RenderTelemetryTable();
      RenderGlobalActionBar();
      if (show_benchmark_logs_) {
        RenderBenchmarkLogsSection();
      }
    }
    GUI.DragWindow();
  }

  private void RenderDmaAndGpuSelectionSection() {
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      GUILayout.Label("<b>硬件 DMA、异构多 GPU 路由与 A/B 基准测速 (Hardware DMA & GPU Router):</b>");

      // 1. Adapter Selection
      int adapter_count = Interface.GpuGetAdapterCount();
      int selected_adapter = Interface.GpuGetSelectedAdapter();

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label("<b>目标运算显卡:</b>", GUILayout.Width(110));
        bool is_auto = selected_adapter < 0;
        if (GUILayout.Toggle(is_auto, "自动 (Auto / 默认系统GPU)", GUI.skin.toggle, GUILayout.Width(190)) && !is_auto) {
          Interface.GpuSelectComputeAdapter(-1);
          GpuModuleTelemetry.LogBenchmarkResult("GPU 路由已切换至 [自动选择]");
        }

        for (int i = 0; i < adapter_count; ++i) {
          if (Interface.GpuGetAdapterInfo(i, out var info)) {
            bool is_this = selected_adapter == i;
            string gpu_type = info.is_integrated ? "<color=#55ffff>[核显 iGPU]</color>" : "<color=#ffaa55>[独显 dGPU]</color>";
            ulong vram_mb = info.dedicated_video_memory / (1024UL * 1024UL);
            string btn_title = $"{gpu_type} {info.description} ({vram_mb} MB)";
            if (GUILayout.Toggle(is_this, btn_title, GUI.skin.toggle) && !is_this) {
              Interface.GpuSelectComputeAdapter(i);
              GpuModuleTelemetry.LogBenchmarkResult($"GPU 路由已切换至: [{i}] {info.description}");
            }
          }
        }
      }

      GUILayout.Label(
          "<size=10><color=#aaffaa>💡 异构调度策略: 若机器具备双显卡，强烈推荐指定至 <b>核显 (Intel UHD/Iris / AMD RDNA APU)</b> 承载 GPGPU 轨道打靶与推演，使独立显卡 100% 算力全权留给游戏 3D 渲染与着色！</color></size>");

      // 2. DMA Transfer Modes Hot-Switching
      DmaTransferMode current_dma_mode = Interface.GpuGetDmaMode();
      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label("<b>DMA 传输模式:</b>", GUILayout.Width(110));

        bool is_dma = current_dma_mode == DmaTransferMode.DirectDma;
        bool is_staging = current_dma_mode == DmaTransferMode.StagingFallback;
        bool is_pure_cpu = current_dma_mode == DmaTransferMode.PureCpu;

        if (GUILayout.Toggle(is_dma, "⚡ Mode 1: 硬件直连 DMA (PCIe ReBAR / 零拷贝 / 零停顿)", GUI.skin.toggle)) {
          if (!is_dma) {
            Interface.GpuSetDmaMode(DmaTransferMode.DirectDma);
            GpuModuleTelemetry.LogBenchmarkResult("已切换至 [Mode 1: 硬件直连 DMA (ReBAR)] 零拷贝高速直达。");
          }
        }

        if (GUILayout.Toggle(is_staging, "📦 Mode 2: 传统暂存内存拷贝 (Staging Fallback)", GUI.skin.toggle)) {
          if (!is_staging) {
            Interface.GpuSetDmaMode(DmaTransferMode.StagingFallback);
            GpuModuleTelemetry.LogBenchmarkResult("已切换至 [Mode 2: 暂存拷贝降级模式] 兼容无 ReBAR 设备。");
          }
        }

        if (GUILayout.Toggle(is_pure_cpu, "🛑 Mode 3: 纯 CPU 模式 (Pure CPU Native)", GUI.skin.toggle)) {
          if (!is_pure_cpu) {
            Interface.GpuSetDmaMode(DmaTransferMode.PureCpu);
            GpuModuleTelemetry.LogBenchmarkResult("已切换至 [Mode 3: 纯 CPU 原生模式] 硬件级旁路全部 GPU 派发。");
          }
        }
      }

      // 3. A/B Telemetry & Performance Probes
      double last_dma_write_s = Interface.GpuGetLastDmaWriteSeconds();
      ulong total_dma_bytes = Interface.GpuGetTotalDmaBytes();
      double total_dma_mb = total_dma_bytes / (1024.0 * 1024.0);
      float dma_upload_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.GpuDmaUpload);
      float gpu_stall_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.GpuPipelineStall);
      float jitter_ms = PerformanceProbe.P99FrameJitterMs;

      using (new GUILayout.HorizontalScope()) {
        string stall_color = gpu_stall_ms < 0.1f ? "#55ff55" : (gpu_stall_ms < 1.0f ? "#ffff55" : "#ff5555");
        string dma_color = dma_upload_ms < 0.2f ? "#55ff55" : "#ffff55";
        string jitter_color = jitter_ms < 3.0f ? "#55ff55" : (jitter_ms < 8.0f ? "#ffff55" : "#ff5555");

        GUILayout.Label(
            $"<b>实时 A/B 探针:</b> DMA 上传: <color={dma_color}>{dma_upload_ms:F3} ms</color> | GPU 管线停顿: <color={stall_color}>{gpu_stall_ms:F3} ms</color> | P99 帧抖动: <color={jitter_color}>{jitter_ms:F2} ms</color> | 最近 DMA 写: <b>{last_dma_write_s * 1000.0:F3} ms</b> | 累计直通吞吐: <b>{total_dma_mb:F2} MB</b>",
            GUILayout.ExpandWidth(true));
      }
    }
  }

  private void RenderHardwareBanner() {
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      float fps = 1.0f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);
      float frame_time_ms = Time.unscaledDeltaTime * 1000.0f;

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label(
            $"<b>实时帧率:</b> <color=#55ff55>{fps:F1} FPS</color> ({frame_time_ms:F1} ms) | <b>GPU 硬件:</b> <color=#55ffff>{PrincipiaGPGPUContext.GpuDeviceName}</color> | <b>显存:</b> {PrincipiaGPGPUContext.GpuMemoryMB} MB | <b>API:</b> {SystemInfo.graphicsDeviceType}",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        string compute_badge = PrincipiaGPGPUContext.SupportsCompute
            ? "<color=#55ff55>✓ Compute Shader (SM 5.0+)</color>"
            : "<color=#ff5555>✗ Compute Shader 不支持</color>";

        string readback_badge = PrincipiaGPGPUContext.SupportsAsyncReadback
            ? "<color=#55ff55>✓ AsyncGPUReadback (非阻塞)</color>"
            : "<color=#ffff55>○ 同步回读降级</color>";

        string bundle_badge = PrincipiaGPGPUContext.HasAssetBundleLoaded
            ? "<color=#55ff55>✓ principia_gpu.bundle 已装载</color>"
            : "<color=#ffff55>○ 源码级管线已就绪 (混合异构测速)</color>";

        int vessels_count = FlightGlobals.Vessels?.Count ?? 0;
        int bodies_count = FlightGlobals.Bodies?.Count ?? 0;

        GUILayout.Label($"<b>特性支持:</b> {compute_badge}  |  {readback_badge}  |  {bundle_badge} | <b>活动实体:</b> {vessels_count} 载具 / {bodies_count} 天体");
      }
    }
  }

  private void RenderPrecisionAndGovernanceSection() {
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label("<b>计算管线与物理精度治理:</b>", GUILayout.Width(170));

        bool is_native = HeterogeneousDynamicGovernor.IsNativeDoublePrecision;
        bool set_native = GUILayout.Toggle(
            is_native,
            "● 原生双精度 (CPU Native FP64 经典实现)",
            GUI.skin.toggle);
        bool set_gpu = GUILayout.Toggle(
            !is_native,
            "⚡ GPGPU 异构加速 (FP32 并发打靶/细分)",
            GUI.skin.toggle);

        if (set_native && !is_native) {
          HeterogeneousDynamicGovernor.PipelineMode =
              ComputationPipelineMode.NativeDoublePrecision;
          GpuModuleTelemetry.LogBenchmarkResult("已切换至 [原生双精度] 模式: 经典 C++ 核心 100% 原始运行，GPU 全模块硬件级旁路。");
        } else if (set_gpu && is_native) {
          HeterogeneousDynamicGovernor.PipelineMode =
              ComputationPipelineMode.GpuAccelerated;
          GpuModuleTelemetry.LogBenchmarkResult("已切换至 [GPGPU 异构加速] 模式: 并行打靶、自适应曲率细分等着色器接入。");
        }
      }

      if (HeterogeneousDynamicGovernor.IsNativeDoublePrecision) {
        using (new GUILayout.VerticalScope()) {
          GUILayout.Label(
              "<color=#55ff55><b>【物理保真安全模式】</b>当前为原生双精度 (IEEE 754 FP64)。经典 C++ 核心 100% 原始运行，旧实现未作任何修改。所有 GPU 模块处于硬件级旁路状态 (Bypassed in FP64)，零显存侵入，零性能开销。</color>",
              Style.Info(GUI.skin.label));
          GUILayout.Label(
              "<size=10><color=#ffffaa>💡 提示: 原生双精度模式下，日常常规飞行不向显卡派发物理运算（因此常规物理调度显示为旁路或 0 次，属于绝对保真设计，绝非故障）。您可随时点击下方 [⚡ 压测] 测试各模块运算吞吐，或切换至 [⚡ GPGPU 异构加速] 启用实时异构分流。</color></size>");
        }
      } else {
        using (new GUILayout.HorizontalScope()) {
          GUILayout.Label(
              "<color=cyan><b>【异构计算激活模式】</b>GPGPU 运算管线已激活，16,384 条机动打靶、自适应曲率细分等多任务并发由 GPU 调度执行。</color>",
              Style.Info(GUI.skin.label));
        }
      }

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label(
            $"<b>渲染耗时:</b> {HeterogeneousDynamicGovernor.SmoothedRenderTimeMs:F1} ms | <b>GPU 运算预算:</b> {HeterogeneousDynamicGovernor.GpuComputeBudgetMs:F1} ms | <b>状态:</b> {(HeterogeneousDynamicGovernor.IsBackpressureActive ? "<color=yellow>反压节流生效中 (Throttled)</color>" : "<color=lime>正常 (Normal)</color>")}",
            GUILayout.ExpandWidth(true));

        if (GUILayout.Button($"调度倾向: {HeterogeneousDynamicGovernor.Affinity}", GUILayoutWidth(4))) {
          HeterogeneousDynamicGovernor.Affinity = (HeterogeneousAffinity)(
              ((int)HeterogeneousDynamicGovernor.Affinity + 1) % 3);
        }
      }

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label(
            $"<b>调度计数:</b> GPU 派发 <b>{HeterogeneousDynamicGovernor.GpuDispatchCount:N0}</b> 次 | CPU 回退 <b>{HeterogeneousDynamicGovernor.CpuDispatchCount:N0}</b> 次 | 零拷贝轨迹直通: <b>{HeterogeneousDynamicGovernor.ZeroCopyDirectCount:N0}</b> 次 | 阶段收割: <b>{HeterogeneousDynamicGovernor.StagedHarvestCount:N0}</b> 次",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        string state_bus_badge = Interface.HasStateBusSupport
            ? "<color=#55ff55>C++ 原生原子三缓冲 (Lock-Free 64B Aligned)</color>"
            : "<color=#55ffff>托管无锁三缓冲 (Lock-Free Atomic Fallback)</color>";
        string cmd_bus_badge = Interface.HasCommandBusSupport
            ? "<color=#55ff55>C++ SPSC 环形缓冲</color>"
            : "<color=#55ffff>托管 SPSC 环形队列</color>";

        GUILayout.Label(
            $"<b>解耦总线 (Phase 2):</b> 状态总线: {state_bus_badge} | 命令总线: {cmd_bus_badge}",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label(
            $"<b>状态通道吞吐:</b> 载具切片: <b>{TripleBufferStateBus.ActiveVesselChannels}</b> | 天体切片: <b>{TripleBufferStateBus.ActiveBodyChannels}</b> | 派发: <b>{TripleBufferStateBus.TotalSnapshotsPublished:N0}</b> | 采样消费: <b>{TripleBufferStateBus.TotalSnapshotsSampled:N0}</b> | SPSC 命令入队: <b>{SpscCommandBus.CommandsPostedCount:N0}</b> / 出队: <b>{SpscCommandBus.CommandsExecutedCount:N0}</b>",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        string batch_sync_badge = Interface.HasBatchSyncSupport
            ? "<color=#55ff55>C++ 原生批处理同步 (Zero-Copy 64B Aligned)</color>"
            : "<color=#55ffff>托管批量缓冲 (Zero-Allocation Batch Fallback)</color>";
        GUILayout.Label(
            $"<b>批处理状态同步 (Phase 3):</b> {batch_sync_badge}",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label(
            $"<b>物理同步指标:</b> 部件批处理: <b>{BatchStateSync.BatchInsertDispatchedCount:N0}</b> (回退: {BatchStateSync.SequentialInsertFallbackCount:N0}) | 视在运动: <b>{BatchStateSync.BatchApparentDispatchedCount:N0}</b> (回退: {BatchStateSync.SequentialApparentFallbackCount:N0}) | 累计节省 P/Invoke: <b>{BatchStateSync.TotalPinvokesSaved:N0}</b> (本帧: {BatchStateSync.PinvokesSavedThisFrame:N0})",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label(
            $"<b>双时钟桥 (Dual-Clock Bridge):</b> 浮点原点纪元: <b>{DualClockBridge.CurrentEpoch}</b> (重定位: {DualClockBridge.TotalOriginShifts} 次) | 相位滞后: <b>{DualClockBridge.PhaseLagMs:F2} ms</b> | 漂移补偿: <b>{DualClockBridge.CumulativeDrift.magnitude:F2} m</b> | 插值 α: <b>{DualClockBridge.InterpolationAlpha:F3}</b>",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        string decoupler_badge = SceneGraphDecoupler.PqsSweepsAvoided > 0
            ? "<color=#55ff55>PQS 脏化拦截已激活</color>"
            : "<color=#55ffff>场景图解耦就绪</color>";
        double throttle_pct = SceneGraphDecoupler.ThrottleRatio * 100.0;
        GUILayout.Label(
            $"<b>场景图解耦 (Phase 4):</b> {decoupler_badge} | 变换节流率: <b>{throttle_pct:F1}%</b> (拦截: {SceneGraphDecoupler.UpdatesThrottled:N0} / 评估: {SceneGraphDecoupler.TotalEvaluations:N0}) | 避免 PQS 脏化重算: <b>{SceneGraphDecoupler.PqsSweepsAvoided:N0} 次</b> | 分级分布: T0:{SceneGraphDecoupler.Tier0Count} T1:{SceneGraphDecoupler.Tier1Count} T2:{SceneGraphDecoupler.Tier2Count} T3:{SceneGraphDecoupler.Tier3Count}",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        string smooth_badge = SmoothPlottingBridge.IsExternalModDetected
            ? "<color=#55ff55>检测到 PrincipiaSmoothPlotting 模组</color>"
            : "<color=#55ffff>内置自适应 Catmull-Rom 样条平滑</color>";
        GUILayout.Label(
            $"<b>GPU 虚拟投影与平滑轨迹:</b> 投影绑定: <b>{GpuVirtualProjection.TotalProjectionsApplied:N0}</b> | 最大微偏置: <b>{GpuVirtualProjection.MaxDisplacementMagnitude:F3} m</b> | 轨迹平滑: {smooth_badge} (插值细分: {SmoothPlottingBridge.SubdivisionsPerformed:N0} 次, 生成顶点: {SmoothPlottingBridge.SubdividedVertices:N0})",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        string breaker_status;
        switch (FrameBudgetCircuitBreaker.CurrentLevel) {
          case CircuitBreakerLevel.Normal:
            breaker_status = "<color=#55ff55>帧预算正常</color>";
            break;
          case CircuitBreakerLevel.SecondaryThrottled:
            breaker_status = "<color=#ffff55>次要物理节流</color>";
            break;
          case CircuitBreakerLevel.ActiveExtrapolated:
            breaker_status = "<color=#ffaa55>外推抑制</color>";
            break;
          case CircuitBreakerLevel.HardTripped:
            breaker_status = "<color=#ff5555>死循环熔断</color>";
            break;
          default:
            breaker_status = "<color=#55ffff>就绪</color>";
            break;
        }
        GUILayout.Label(
            $"<b>死亡循环熔断 (Phase 5):</b> {breaker_status} | 物理帧耗时: <b>{FrameBudgetCircuitBreaker.CurrentFramePhysicsElapsedMs:F2} ms</b> (均值: <b>{FrameBudgetCircuitBreaker.AverageFramePhysicsElapsedMs:F2} ms</b> / 预算: {FrameBudgetCircuitBreaker.BudgetMilliseconds:F1} ms) | 熔断次数: <b>{FrameBudgetCircuitBreaker.TotalCircuitTrips:N0}</b> (抑制步数: {FrameBudgetCircuitBreaker.TotalSubstepsSuppressed:N0})",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        double saved_mb = ZeroAllocationForceCache.EstimatedBytesSaved / (1024.0 * 1024.0);
        GUILayout.Label(
            $"<b>零分配力缓存与真空旁路:</b> 旁路载具: <b>{ZeroAllocationForceCache.VacuumBypasses:N0} 次</b> | 消除堆分配: <b>{ZeroAllocationForceCache.AllocationsSaved:N0} 次</b> (节省堆内存: <b>{saved_mb:F2} MB</b>) | 避免气动 P/Invoke: <b>{ZeroAllocationForceCache.PInvokesSaved:N0} 次</b>",
            GUILayout.ExpandWidth(true));
      }

      using (new GUILayout.HorizontalScope()) {
        string shooting_mode_badge = GpuFlightPlanShootingOptimizer.LastUsedGpu
            ? "<color=#55ff55>[⚡ GPU 16K 并行打靶]</color>"
            : (GpuFlightPlanShootingOptimizer.IsGpuAcceleratedSupported
                ? "<color=#55ffff>[⚡ GPU 就绪 / CPU 备用]</color>"
                : "<color=#ffaa55>[多核 CPU 16K 并行打靶]</color>");

        GUILayout.Label(
            $"<b>并行打靶优化 (Phase 6):</b> {shooting_mode_badge} | 总打靶: <b>{GpuFlightPlanShootingOptimizer.TotalOptimizations:N0} 次</b> (GPU: <b>{GpuFlightPlanShootingOptimizer.GpuDispatches:N0}</b>, CPU: <b>{GpuFlightPlanShootingOptimizer.CpuFallbackDispatches:N0}</b>) | 评估候选轨道: <b>{GpuFlightPlanShootingOptimizer.TotalCandidatesEvaluated:N0} 条</b> | 最近耗时: <b>{GpuFlightPlanShootingOptimizer.LastDurationMs:F2} ms</b> (均值: <b>{GpuFlightPlanShootingOptimizer.AverageDurationMs:F2} ms</b>) | 最优成本: <b>{GpuFlightPlanShootingOptimizer.LastBestCost:F2}</b> (近点距: <b>{GpuFlightPlanShootingOptimizer.LastPeriapsisDistance:N0} m</b>)",
            GUILayout.ExpandWidth(true));
      }
    }
  }

  private void RenderTelemetryTable() {
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label("<b>GPU 模块统筹监控列表 (12 大异构加速组件):</b>", GUILayout.ExpandWidth(true));
        show_module_descriptions_ = GUILayout.Toggle(
            show_module_descriptions_,
            "显示架构说明与算法描述",
            GUI.skin.toggle);
      }

      // Table Header
      using (new GUILayout.HorizontalScope(GUI.skin.box)) {
        GUILayout.Label("<b>模块名称 / 着色器</b>", GUILayout.Width(ColumnWidthModule));
        GUILayout.Label("<b>类别</b>", GUILayout.Width(ColumnWidthCategory));
        GUILayout.Label("<b>当前状态</b>", GUILayout.Width(ColumnWidthStatus));
        GUILayout.Label("<b>调度次数</b>", GUILayout.Width(ColumnWidthCount));
        GUILayout.Label("<b>最近耗时</b>", GUILayout.Width(ColumnWidthLastMs));
        GUILayout.Label("<b>平均耗时</b>", GUILayout.Width(ColumnWidthAvgMs));
        GUILayout.Label("<b>显存 / 资源</b>", GUILayout.Width(ColumnWidthResource));
        GUILayout.Label("<b>操作</b>", GUILayout.Width(ColumnWidthAction));
      }

      scroll_position_ = GUILayout.BeginScrollView(scroll_position_, GUILayout.Height(210));
      var modules = GpuModuleTelemetry.GetAllModules();

      for (int i = 0; i < modules.Length; ++i) {
        var m = modules[i];
        if (m == null) continue;

        GpuModuleStatus effective_status = GpuModuleTelemetry.GetEffectiveStatus(m.Id);

        using (new GUILayout.VerticalScope(GUI.skin.box)) {
          using (new GUILayout.HorizontalScope()) {
            // Column 1: Name
            GUILayout.Label(
                $"<b>{m.Name}</b>\n<size=9><color=grey>{m.ShaderName}</color></size>",
                GUILayout.Width(ColumnWidthModule));

            // Column 2: Category
            GUILayout.Label($"<color=#aaaaff>{m.Category}</color>", GUILayout.Width(ColumnWidthCategory));

            // Column 3: Status Badge
            string status_badge;
            switch (effective_status) {
              case GpuModuleStatus.BypassedInFP64:
                status_badge = "<color=#55ff55>[FP64 旁路]</color>";
                break;
              case GpuModuleStatus.Active:
                status_badge = "<color=cyan>[就绪/活跃]</color>";
                break;
              case GpuModuleStatus.BackpressureThrottled:
                status_badge = "<color=yellow>[反压节流]</color>";
                break;
              case GpuModuleStatus.FallbackCpu:
                status_badge = "<color=orange>[CPU 降级]</color>";
                break;
              case GpuModuleStatus.Error:
                status_badge = "<color=red>[异常错误]</color>";
                break;
              default:
                status_badge = "<color=grey>[待机就绪]</color>";
                break;
            }
            GUILayout.Label(status_badge, GUILayout.Width(ColumnWidthStatus));

            // Column 4: Count
            GUILayout.Label($"{m.DispatchCount:N0}", GUILayout.Width(ColumnWidthCount));

            // Column 5: Last Duration
            string last_dur_str = m.LastDurationMs > 0 ? $"{m.LastDurationMs:F2} ms" : "-";
            GUILayout.Label(last_dur_str, GUILayout.Width(ColumnWidthLastMs));

            // Column 6: Avg Duration
            string avg_dur_str = m.AvgDurationMs > 0 ? $"{m.AvgDurationMs:F2} ms" : "-";
            GUILayout.Label(avg_dur_str, GUILayout.Width(ColumnWidthAvgMs));

            // Column 7: Resource
            GUILayout.Label($"<size=10>{m.ResourceSummary}</size>", GUILayout.Width(ColumnWidthResource));

            // Column 8: Benchmark Action
            if (GUILayout.Button("⚡ 压测", GUILayout.Width(ColumnWidthAction))) {
              GpuModuleId module_id = m.Id;
              GpuModuleTelemetry.ExecuteBenchmark(module_id, (ok, info) => {});
            }
          }

          // Detailed summary & algorithm description
          if (show_module_descriptions_) {
            using (new GUILayout.HorizontalScope()) {
              GUILayout.Label($"<size=10><color=#cccccc>说明: {m.Description}</color></size>");
            }
          }
          if (m.DispatchCount > 0 && !string.IsNullOrEmpty(m.LastResultSummary)) {
            using (new GUILayout.HorizontalScope()) {
              GUILayout.Label($"<size=10><color=#88ff88>结果: {m.LastResultSummary}</color></size>");
            }
          }
        }
      }

      GUILayout.EndScrollView();
    }
  }

  private void RenderGlobalActionBar() {
    using (new GUILayout.HorizontalScope()) {
      if (GUILayout.Button("⚡ 一键全模块综合压测 (Run All Benchmarks)", GUILayout.ExpandWidth(true))) {
        GpuModuleTelemetry.ExecuteAllBenchmarks();
      }

      if (GUILayout.Button("⚡ 载入基准预设", GUILayoutWidth(4))) {
        GpuModuleTelemetry.InitializeDefaultBaselines();
      }

      if (GUILayout.Button("🔄 重置统计", GUILayoutWidth(3))) {
        GpuModuleTelemetry.ResetTelemetry();
      }

      if (GUILayout.Button("🗑 释放显存", GUILayoutWidth(3))) {
        GpuModuleTelemetry.ReleaseAllGpuBuffers();
      }

      string log_btn_text = show_benchmark_logs_ ? "▲ 隐藏日志" : "▼ 诊断日志";
      if (GUILayout.Button(log_btn_text, GUILayoutWidth(3))) {
        show_benchmark_logs_ = !show_benchmark_logs_;
      }
    }
  }

  private void RenderBenchmarkLogsSection() {
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      var logs = GpuModuleTelemetry.BenchmarkLogs;
      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label($"<b>压测与诊断日志记录 ({logs.Count} 条):</b>", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("清空", GUILayoutWidth(2))) {
          GpuModuleTelemetry.ClearBenchmarkLogs();
        }
      }

      log_scroll_position_ = GUILayout.BeginScrollView(log_scroll_position_, GUILayout.Height(100));
      if (logs.Count == 0) {
        GUILayout.Label("<color=grey><i>(暂无压测日志，点击上方 [⚡ 单项压测] 或 [⚡ 一键全模块综合压测] 开始执行)</i></color>");
      } else {
        for (int i = logs.Count - 1; i >= 0; --i) {
          GUILayout.Label($"<size=10>{logs[i]}</size>");
        }
      }
      GUILayout.EndScrollView();
    }
  }
}

}
