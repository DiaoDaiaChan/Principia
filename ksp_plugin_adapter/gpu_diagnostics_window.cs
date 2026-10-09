using System;
using System.Collections.Generic;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

internal class GpuDiagnosticsWindow : SupervisedWindowRenderer {
  private readonly PrincipiaPluginAdapter adapter_;
  private int current_tab_ = 0;
  private readonly string[] tab_names_ = new string[] {
    "⚡ 硬件路由与DMA",
    "📋 异构计算组件 (12)",
    "⚙️ 架构解耦诊断",
    "📜 压测与诊断日志"
  };

  private Vector2 tab0_scroll_position_ = Vector2.zero;
  private Vector2 modules_scroll_position_ = Vector2.zero;
  private Vector2 architecture_scroll_position_ = Vector2.zero;
  private Vector2 log_scroll_position_ = Vector2.zero;
  private bool show_module_descriptions_ = false;

  public static string LastDmaBenchmarkResult { get; private set; } = "尚未执行基准测速 (点击下方按钮立即测试)";
  public static bool IsDmaBenchmarking { get; private set; } = false;

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
      GUILayout.MinWidth(820),
      GUILayout.MinHeight(620)) {
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

  public static void RunDmaBenchmark() {
    try {
      IsDmaBenchmarking = true;
      const int segment_count = 4096; // 1 MB payload (4096 * 256 B)
      bool native_ok = Interface.GpuBenchmarkDmaTransfer(
          segment_count, out double duration_s, out double bandwidth_gbps);

      double duration_ms = duration_s * 1000.0;
      var mode = Interface.GpuGetDmaMode();
      string mode_str = mode switch {
        DmaTransferMode.DirectDma => "Mode 1 (直连 DMA / ReBAR 零拷贝)",
        DmaTransferMode.StagingFallback => "Mode 2 (暂存拷贝降级)",
        DmaTransferMode.PureCpu => "Mode 3 (纯 CPU 模式)",
        _ => mode.ToString()
      };

      if (native_ok) {
        PerformanceProbe.RecordElapsed(
            ProbeCategory.GpuDmaUpload,
            (long)(duration_ms / (1000.0 / System.Diagnostics.Stopwatch.Frequency)));
        LastDmaBenchmarkResult =
            $"实测带宽: <b><color=#55ff55>{bandwidth_gbps:F2} GB/s</color></b> | 传输耗时: <b><color=#55ffff>{duration_ms:F3} ms</color></b> | 模式: <b>{mode_str}</b> | 数据校验: <b><color=#55ff55>100% 校验通过</color></b> ({segment_count:N0} 节点 / 1.0 MB)";
        GpuModuleTelemetry.LogBenchmarkResult(
            $"[DMA 压测通过] PCIe 实测带宽: {bandwidth_gbps:F2} GB/s | 耗时: {duration_ms:F3} ms | 传输模式: {mode_str}");
      } else {
        LastDmaBenchmarkResult =
            "<color=#ff5555>DMA 传输基准测试失败或数据校验不匹配 (Native C++ 返回 false)</color>";
        GpuModuleTelemetry.LogBenchmarkResult("[DMA 压测失败] 接口调用异常或校验失败");
      }
    } catch (Exception ex) {
      LastDmaBenchmarkResult = $"<color=#ff5555>压测异常: {ex.Message}</color>";
      GpuModuleTelemetry.LogBenchmarkResult($"[DMA 压测异常] {ex.Message}");
    } finally {
      IsDmaBenchmarking = false;
    }
  }

  protected override void RenderWindowContents(int window_id) {
    PrincipiaGPGPUContext.EnsureInitialized();

    using (new GUILayout.VerticalScope()) {
      RenderHardwareBanner();

      current_tab_ = GUILayout.Toolbar(current_tab_, tab_names_, GUILayout.Height(28));

      switch (current_tab_) {
        case 0:
          RenderDmaAndHardwareTab();
          break;
        case 1:
          RenderComputeModulesTab();
          break;
        case 2:
          RenderDecouplingArchitectureTab();
          break;
        case 3:
          RenderBenchmarkLogsTab();
          break;
      }
    }
    GUI.DragWindow();
  }

  private void RenderHardwareBanner() {
    using (new GUILayout.VerticalScope(GUI.skin.box)) {
      float fps = 1.0f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);
      float frame_time_ms = Time.unscaledDeltaTime * 1000.0f;

      using (new GUILayout.HorizontalScope()) {
        string fps_color = fps > 55.0f ? "#55ff55" : (fps > 30.0f ? "#ffff55" : "#ff5555");
        GUILayout.Label(
            $"<b>实时帧率:</b> <color={fps_color}>{fps:F1} FPS</color> ({frame_time_ms:F1} ms) | <b>GPU 硬件:</b> <color=#55ffff>{PrincipiaGPGPUContext.GpuDeviceName}</color> | <b>显存:</b> {PrincipiaGPGPUContext.GpuMemoryMB} MB | <b>API:</b> {SystemInfo.graphicsDeviceType}",
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

  private void RenderDmaAndHardwareTab() {
    tab0_scroll_position_ = GUILayout.BeginScrollView(tab0_scroll_position_, GUILayout.Height(500));
    using (new GUILayout.VerticalScope()) {
      // 1. Adapter Selection Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>目标运算显卡路由 (GPU Compute Router):</b>");
        int adapter_count = Interface.GpuGetAdapterCount();
        int selected_adapter = Interface.GpuGetSelectedAdapter();

        using (new GUILayout.HorizontalScope()) {
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
            "<size=10><color=#aaffaa>💡 异构调度策略: 若机器具备双显卡，强烈推荐指定至 <b>核显 (Intel UHD/Iris / AMD APU)</b> 承载 GPGPU 轨道打靶与推演，使独立显卡 100% 算力全权留给游戏 3D 渲染与着色！</color></size>");
      }

      // 2. DMA Transfer Modes Hot-Switching Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>DMA 传输模式热切换 (DMA Transfer Modes):</b>");
        DmaTransferMode current_dma_mode = Interface.GpuGetDmaMode();
        using (new GUILayout.HorizontalScope()) {
          bool is_dma = current_dma_mode == DmaTransferMode.DirectDma;
          bool is_staging = current_dma_mode == DmaTransferMode.StagingFallback;
          bool is_pure_cpu = current_dma_mode == DmaTransferMode.PureCpu;

          if (GUILayout.Toggle(is_dma, "⚡ Mode 1: 硬件直连 DMA (PCIe ReBAR / 零拷贝)", GUI.skin.toggle)) {
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
      }

      // 3. Dedicated DMA Benchmark Card
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>⚡ PCIe DMA 传输性能基准测速 (Direct DMA Benchmark):</b>");
        using (new GUILayout.HorizontalScope()) {
          string btn_txt = IsDmaBenchmarking
              ? "⏳ 正在测试 PCIe DMA 传输带宽..."
              : "⚡ 测试 DMA 传输性能 (Benchmark PCIe DMA Transfer)";
          if (GUILayout.Button(btn_txt, GUILayout.Height(30))) {
            RunDmaBenchmark();
          }
        }

        using (new GUILayout.HorizontalScope(GUI.skin.box)) {
          GUILayout.Label($"<b>测试结果:</b> {LastDmaBenchmarkResult}", GUILayout.ExpandWidth(true));
        }

        // Live A/B Probes
        double last_dma_write_s = Interface.GpuGetLastDmaWriteSeconds();
        ulong total_dma_bytes = Interface.GpuGetTotalDmaBytes();
        double total_dma_mb = total_dma_bytes / (1024.0 * 1024.0);
        float dma_upload_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.GpuDmaUpload);
        float gpu_stall_ms = PerformanceProbe.GetSmoothedMs(ProbeCategory.GpuPipelineStall);
        float jitter_ms = PerformanceProbe.P99FrameJitterMs;

        string stall_color = gpu_stall_ms < 0.1f ? "#55ff55" : (gpu_stall_ms < 1.0f ? "#ffff55" : "#ff5555");
        string dma_color = dma_upload_ms < 0.2f ? "#55ff55" : "#ffff55";
        string jitter_color = jitter_ms < 3.0f ? "#55ff55" : (jitter_ms < 8.0f ? "#ffff55" : "#ff5555");

        using (new GUILayout.HorizontalScope()) {
          GUILayout.Label(
              $"<b>实时 A/B 探针:</b> DMA 上传: <color={dma_color}>{dma_upload_ms:F3} ms</color> | GPU 管线停顿: <color={stall_color}>{gpu_stall_ms:F3} ms</color> | P99 帧抖动: <color={jitter_color}>{jitter_ms:F2} ms</color> | 最近 DMA 写: <b>{last_dma_write_s * 1000.0:F3} ms</b> | 累计直通吞吐: <b>{total_dma_mb:F2} MB</b>",
              GUILayout.ExpandWidth(true));
        }
      }

      // 4. Computation Pipeline & Governance
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>计算管线与物理精度治理 (Pipeline Precision & Governance):</b>");
        using (new GUILayout.HorizontalScope()) {
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
          GUILayout.Label(
              "<color=#55ff55><b>【物理保真安全模式】</b>当前为原生双精度 (IEEE 754 FP64)。经典 C++ 核心 100% 原始运行，旧实现未作任何修改。所有 GPU 模块处于硬件级旁路状态 (Bypassed in FP64)，零显存侵入，零性能开销。</color>",
              Style.Info(GUI.skin.label));
        } else {
          GUILayout.Label(
              "<color=cyan><b>【异构计算激活模式】</b>GPGPU 运算管线已激活，16,384 条机动打靶、自适应曲率细分等多任务并发由 GPU 调度执行。</color>",
              Style.Info(GUI.skin.label));
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
      }
    }
    GUILayout.EndScrollView();
  }

  private void RenderComputeModulesTab() {
    using (new GUILayout.VerticalScope()) {
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

      modules_scroll_position_ = GUILayout.BeginScrollView(modules_scroll_position_, GUILayout.Height(360));
      var modules = GpuModuleTelemetry.GetAllModules();

      for (int i = 0; i < modules.Length; ++i) {
        var m = modules[i];
        if (m == null) continue;

        GpuModuleStatus effective_status = GpuModuleTelemetry.GetEffectiveStatus(m.Id);

        using (new GUILayout.VerticalScope(GUI.skin.box)) {
          using (new GUILayout.HorizontalScope()) {
            GUILayout.Label(
                $"<b>{m.Name}</b>\n<size=9><color=grey>{m.ShaderName}</color></size>",
                GUILayout.Width(ColumnWidthModule));

            GUILayout.Label($"<color=#aaaaff>{m.Category}</color>", GUILayout.Width(ColumnWidthCategory));

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
            GUILayout.Label($"{m.DispatchCount:N0}", GUILayout.Width(ColumnWidthCount));

            string last_dur_str = m.LastDurationMs > 0 ? $"{m.LastDurationMs:F2} ms" : "-";
            GUILayout.Label(last_dur_str, GUILayout.Width(ColumnWidthLastMs));

            string avg_dur_str = m.AvgDurationMs > 0 ? $"{m.AvgDurationMs:F2} ms" : "-";
            GUILayout.Label(avg_dur_str, GUILayout.Width(ColumnWidthAvgMs));

            GUILayout.Label($"<size=10>{m.ResourceSummary}</size>", GUILayout.Width(ColumnWidthResource));

            if (GUILayout.Button("⚡ 压测", GUILayout.Width(ColumnWidthAction))) {
              GpuModuleId module_id = m.Id;
              GpuModuleTelemetry.ExecuteBenchmark(module_id, (ok, info) => {});
            }
          }

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

      // Global Actions
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
      }
    }
  }

  private void RenderDecouplingArchitectureTab() {
    architecture_scroll_position_ = GUILayout.BeginScrollView(architecture_scroll_position_, GUILayout.Height(500));
    using (new GUILayout.VerticalScope()) {
      // Phase 2: Lock-Free State Bus
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        string state_bus_badge = Interface.HasStateBusSupport
            ? "<color=#55ff55>C++ 原生原子三缓冲 (Lock-Free 64B Aligned)</color>"
            : "<color=#55ffff>托管无锁三缓冲 (Lock-Free Atomic Fallback)</color>";
        string cmd_bus_badge = Interface.HasCommandBusSupport
            ? "<color=#55ff55>C++ SPSC 环形缓冲</color>"
            : "<color=#55ffff>托管 SPSC 环形队列</color>";
        GUILayout.Label($"<b>Phase 2: 自治物理与无锁三缓冲总线:</b> {state_bus_badge} | {cmd_bus_badge}");
        GUILayout.Label(
            $"状态通道吞吐: 载具切片 <b>{TripleBufferStateBus.ActiveVesselChannels}</b> | 天体切片 <b>{TripleBufferStateBus.ActiveBodyChannels}</b> | 派发 <b>{TripleBufferStateBus.TotalSnapshotsPublished:N0}</b> | 采样消费 <b>{TripleBufferStateBus.TotalSnapshotsSampled:N0}</b> | SPSC 命令入队: <b>{SpscCommandBus.CommandsPostedCount:N0}</b> / 出队: <b>{SpscCommandBus.CommandsExecutedCount:N0}</b>");
      }

      // Phase 3: Batch State Sync
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        string batch_sync_badge = Interface.HasBatchSyncSupport
            ? "<color=#55ff55>C++ 原生批处理同步 (Zero-Copy 64B Aligned)</color>"
            : "<color=#55ffff>托管批量缓冲 (Zero-Allocation Batch Fallback)</color>";
        GUILayout.Label($"<b>Phase 3: 批处理状态同步:</b> {batch_sync_badge}");
        GUILayout.Label(
            $"物理同步指标: 部件批处理 <b>{BatchStateSync.BatchInsertDispatchedCount:N0}</b> (回退: {BatchStateSync.SequentialInsertFallbackCount:N0}) | 视在运动 <b>{BatchStateSync.BatchApparentDispatchedCount:N0}</b> (回退: {BatchStateSync.SequentialApparentFallbackCount:N0}) | 累计节省 P/Invoke: <b>{BatchStateSync.TotalPinvokesSaved:N0}</b> (本帧: {BatchStateSync.PinvokesSavedThisFrame:N0})");
      }

      // Phase 3: Dual-Clock Bridge
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        GUILayout.Label("<b>Phase 3: 双时钟桥 (Dual-Clock Bridge):</b>");
        GUILayout.Label(
            $"浮点原点纪元: <b>{DualClockBridge.CurrentEpoch}</b> (重定位: <b>{DualClockBridge.TotalOriginShifts}</b> 次) | 相位滞后: <b>{DualClockBridge.PhaseLagMs:F2} ms</b> | 漂移补偿: <b>{DualClockBridge.CumulativeDrift.magnitude:F2} m</b> | 插值 α: <b>{DualClockBridge.InterpolationAlpha:F3}</b>");
      }

      // Phase 4: Scene Graph Decoupler
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        string decoupler_badge = SceneGraphDecoupler.PqsSweepsAvoided > 0
            ? "<color=#55ff55>PQS 脏化拦截已激活</color>"
            : "<color=#55ffff>场景图解耦就绪</color>";
        double throttle_pct = SceneGraphDecoupler.ThrottleRatio * 100.0;
        GUILayout.Label($"<b>Phase 4: 场景图解耦与 PQS 节流:</b> {decoupler_badge}");
        GUILayout.Label(
            $"变换节流率: <b>{throttle_pct:F1}%</b> (拦截: <b>{SceneGraphDecoupler.UpdatesThrottled:N0}</b> / 评估: {SceneGraphDecoupler.TotalEvaluations:N0}) | 避免 PQS 脏化重算: <b>{SceneGraphDecoupler.PqsSweepsAvoided:N0} 次</b> | 分级分布: T0:{SceneGraphDecoupler.Tier0Count} T1:{SceneGraphDecoupler.Tier1Count} T2:{SceneGraphDecoupler.Tier2Count} T3:{SceneGraphDecoupler.Tier3Count}");
      }

      // Phase 4: GPU Virtual Projection & Trajectory Smoothing
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        string smooth_badge = SmoothPlottingBridge.IsExternalModDetected
            ? "<color=#55ff55>PrincipiaSmoothPlotting 模组联动</color>"
            : "<color=#55ffff>内置自适应 Catmull-Rom 样条平滑</color>";
        GUILayout.Label($"<b>Phase 4: GPU 虚拟投影与轨迹平滑:</b> {smooth_badge}");
        GUILayout.Label(
            $"投影绑定: <b>{GpuVirtualProjection.TotalProjectionsApplied:N0}</b> | 最大微偏置: <b>{GpuVirtualProjection.MaxDisplacementMagnitude:F3} m</b> | 样条细分: <b>{SmoothPlottingBridge.SubdivisionsPerformed:N0}</b> 次 (生成顶点: <b>{SmoothPlottingBridge.SubdividedVertices:N0}</b>)");
      }

      // Phase 5: Frame Budget Circuit Breaker
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        string breaker_status = FrameBudgetCircuitBreaker.CurrentLevel switch {
          CircuitBreakerLevel.Normal => "<color=#55ff55>帧预算正常</color>",
          CircuitBreakerLevel.SecondaryThrottled => "<color=#ffff55>次要物理节流</color>",
          CircuitBreakerLevel.ActiveExtrapolated => "<color=#ffaa55>外推抑制</color>",
          CircuitBreakerLevel.HardTripped => "<color=#ff5555>死循环熔断</color>",
          _ => "<color=#55ffff>就绪</color>"
        };
        GUILayout.Label($"<b>Phase 5: 死亡循环熔断器 (Frame Budget Circuit Breaker):</b> {breaker_status}");
        GUILayout.Label(
            $"物理帧耗时: <b>{FrameBudgetCircuitBreaker.CurrentFramePhysicsElapsedMs:F2} ms</b> (均值: <b>{FrameBudgetCircuitBreaker.AverageFramePhysicsElapsedMs:F2} ms</b> / 预算: {FrameBudgetCircuitBreaker.BudgetMilliseconds:F1} ms) | 熔断次数: <b>{FrameBudgetCircuitBreaker.TotalCircuitTrips:N0}</b> (抑制步数: {FrameBudgetCircuitBreaker.TotalSubstepsSuppressed:N0})");
      }

      // Phase 5: Zero-Allocation Force Cache & Vacuum Bypass
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        double saved_mb = ZeroAllocationForceCache.EstimatedBytesSaved / (1024.0 * 1024.0);
        GUILayout.Label("<b>Phase 5: 零分配力缓存与真空旁路:</b>");
        GUILayout.Label(
            $"旁路载具: <b>{ZeroAllocationForceCache.VacuumBypasses:N0}</b> 次 | 消除堆分配: <b>{ZeroAllocationForceCache.AllocationsSaved:N0}</b> 次 (节省堆内存: <b>{saved_mb:F2} MB</b>) | 避免气动 P/Invoke: <b>{ZeroAllocationForceCache.PInvokesSaved:N0}</b> 次");
      }

      // Phase 6: 16K Parallel Flight Plan Shooting Optimizer
      using (new GUILayout.VerticalScope(GUI.skin.box)) {
        string shooting_mode_badge = GpuFlightPlanShootingOptimizer.LastUsedGpu
            ? "<color=#55ff55>[⚡ GPU 16K 并行打靶]</color>"
            : (GpuFlightPlanShootingOptimizer.IsGpuAcceleratedSupported
                ? "<color=#55ffff>[⚡ GPU 就绪 / CPU 备用]</color>"
                : "<color=#ffaa55>[多核 CPU 16K 并行打靶]</color>");
        GUILayout.Label($"<b>Phase 6: 16K 并行打靶寻优器:</b> {shooting_mode_badge}");
        GUILayout.Label(
            $"总打靶: <b>{GpuFlightPlanShootingOptimizer.TotalOptimizations:N0}</b> 次 (GPU: <b>{GpuFlightPlanShootingOptimizer.GpuDispatches:N0}</b>, CPU: <b>{GpuFlightPlanShootingOptimizer.CpuFallbackDispatches:N0}</b>) | 评估候选轨道: <b>{GpuFlightPlanShootingOptimizer.TotalCandidatesEvaluated:N0} 条</b> | 最近耗时: <b>{GpuFlightPlanShootingOptimizer.LastDurationMs:F2} ms</b> (均值: <b>{GpuFlightPlanShootingOptimizer.AverageDurationMs:F2} ms</b>) | 最优成本: <b>{GpuFlightPlanShootingOptimizer.LastBestCost:F2}</b> (近点距: <b>{GpuFlightPlanShootingOptimizer.LastPeriapsisDistance:N0} m</b>)");
      }
    }
    GUILayout.EndScrollView();
  }

  private void RenderBenchmarkLogsTab() {
    using (new GUILayout.VerticalScope()) {
      var logs = GpuModuleTelemetry.BenchmarkLogs;
      using (new GUILayout.HorizontalScope()) {
        GUILayout.Label($"<b>压测与诊断日志记录 ({logs.Count} 条):</b>", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("清空日志", GUILayoutWidth(2))) {
          GpuModuleTelemetry.ClearBenchmarkLogs();
        }
      }

      log_scroll_position_ = GUILayout.BeginScrollView(log_scroll_position_, GUILayout.Height(470));
      if (logs.Count == 0) {
        GUILayout.Label("<color=grey><i>(暂无压测日志，点击 [⚡ 硬件路由与DMA] 中的 [测试 DMA 传输性能] 或 [📋 异构计算组件] 中的 [⚡ 压测] 开始执行)</i></color>");
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
