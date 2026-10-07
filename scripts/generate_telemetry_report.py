#!/usr/bin/env python3
"""
Principia 实时性能遥测日志解析与图表生成器
Principia Real-Time Telemetry Analyzer & Interactive HTML Dashboard Generator

- 零第三方依赖：纯标准库 (csv, json, os, sys, math, datetime)
- 支持读取 PrincipiaTelemetry.csv / PrincipiaTelemetry.jsonl / PrincipiaTelemetry_latest.json
- 支持在控制台输出 Agent 友好的 ASCII/Unicode 性能分析摘要
- 支持生成单文件离线交互式 HTML 性能图表 (Canvas / SVG 可视化)
"""

import csv
import json
import os
import sys
from datetime import datetime

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

DEFAULT_SEARCH_PATHS = [
    r"c:\Users\43701\Documents\github\Principia\PrincipiaTelemetry.csv",
    r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program_newmod\PrincipiaTelemetry.csv",
    r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\PrincipiaTelemetry.csv",
    r"c:\Users\43701\Documents\github\Principia\PrincipiaTelemetry.jsonl",
    r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program_newmod\PrincipiaTelemetry.jsonl",
    r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\PrincipiaTelemetry.jsonl",
    r"c:\Users\43701\Documents\github\Principia\PrincipiaTelemetry_latest.json",
    r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program_newmod\PrincipiaTelemetry_latest.json",
    r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\PrincipiaTelemetry_latest.json",
]


def find_telemetry_file(custom_path=None):
    if custom_path and os.path.exists(custom_path):
        return custom_path
    for p in DEFAULT_SEARCH_PATHS:
        if os.path.exists(p) and os.path.getsize(p) > 0:
            return p
    return None


def load_telemetry_data(file_path):
    records = []
    if not file_path or not os.path.exists(file_path):
        return records

    ext = os.path.splitext(file_path)[1].lower()
    if ext == ".csv":
        with open(file_path, "r", encoding="utf-8", errors="replace") as f:
            reader = csv.DictReader(f)
            for row in reader:
                records.append(row)
    elif ext == ".jsonl":
        with open(file_path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        obj = json.loads(line)
                        records.append(flatten_json_record(obj))
                    except Exception:
                        pass
    elif ext == ".json":
        with open(file_path, "r", encoding="utf-8", errors="replace") as f:
            try:
                obj = json.load(f)
                if isinstance(obj, list):
                    for item in obj:
                        records.append(flatten_json_record(item))
                elif isinstance(obj, dict):
                    records.append(flatten_json_record(obj))
            except Exception:
                pass
    return records


def flatten_json_record(obj):
    flat = {}
    for k, v in obj.items():
        if isinstance(v, dict):
            for sub_k, sub_v in v.items():
                flat[f"{sub_k}"] = sub_v
        else:
            flat[k] = v
    return flat


def safe_float(val, default=0.0):
    try:
        return float(val)
    except (ValueError, TypeError):
        return default


def print_agent_summary(records, source_file):
    if not records:
        print(f"[!] 未找到遥测数据记录 (文件: {source_file})")
        return

    latest = records[-1]
    count = len(records)

    print("=" * 80)
    print(f"[+] PRINCIPIA 实时性能遥测诊断报告 (共采集 {count} 帧样本)")
    print(f"[*] 数据源: {source_file}")
    print(f"[*] 时间戳: {latest.get('timestamp', 'N/A')} (UT: {safe_float(latest.get('universal_time', 0)):.1f}s)")
    print("=" * 80)

    fps = safe_float(latest.get("fps", 0))
    frametime = safe_float(latest.get("frametime_ms", 0))
    gc_rate = safe_float(latest.get("gc_alloc_kb_f", 0))
    p_total = safe_float(latest.get("principia_total_ms", 0))
    u_other = safe_float(latest.get("unity_other_ms", 0))
    ratio = (p_total / frametime * 100.0) if frametime > 0 else 0.0

    print(f"【1. 帧率与总体耗时】")
    print(f"  ● FPS:             {fps:.1f} 帧/秒")
    print(f"  ● 帧耗时:          {frametime:.2f} ms")
    print(f"  ● Principia 耗时:  {p_total:.2f} ms ({ratio:.1f}%)")
    print(f"  ● Unity 引擎耗时:  {u_other:.2f} ms ({100.0 - ratio:.1f}%)")
    print(f"  ● 堆内存分配率:    {gc_rate:.1f} KB/帧")
    print("-" * 80)

    print(f"【2. Unity 端生命周期耗时分析 (平滑均值 / 峰值)】")
    lifecycles = [
        ("• Precalc()        [天体/历表推进]", "precalc_ms", "precalc_peak_ms"),
        ("• FixedUpdate()    [物理帧同步]",   "fixed_update_ms", "fixed_update_peak_ms"),
        ("• WaitedForFixed() [部件动力学]",   "waited_for_fixed_ms", "waited_for_fixed_peak_ms"),
        ("• BetterLate()     [分阶段收割]",   "better_late_ms", "better_late_peak_ms"),
        ("• LateUpdate()     [轨迹视口交互]", "late_update_ms", "late_update_peak_ms"),
    ]
    for label, avg_k, peak_k in lifecycles:
        avg_v = safe_float(latest.get(avg_k, 0))
        peak_v = safe_float(latest.get(peak_k, 0))
        flag = " ⚠️ 尖峰" if peak_v > 2.0 else ""
        print(f"  {label:<30} {avg_v:6.2f} ms (峰值: {peak_v:5.1f} ms){flag}")
    print("-" * 80)

    print(f"【3. C++ 核心子系统剖析 (平滑均值 / 峰值)】")
    subsystems = [
        ("物理推进 (PhysicsAdvance)",   "physics_advance_ms", "physics_advance_peak_ms"),
        ("载体收割 (VesselHarvest)",     "vessel_harvest_ms", "vessel_harvest_peak_ms"),
        ("天体框架 (BodyFrames)",        "body_frames_ms", "body_frames_peak_ms"),
        ("轨迹绘制 (Plotting)",          "plotting_ms", "plotting_peak_ms"),
        ("网格细分 (MeshGen)",           "mesh_gen_ms", "mesh_gen_peak_ms"),
        ("飞行计划寻优 (FlightPlanOpt)",  "flight_plan_eval_ms", "flight_plan_eval_peak_ms"),
        ("GPU绘制直出 (GpuCompute)",     "gpu_compute_ms", "gpu_compute_peak_ms"),
        ("天体轨迹重构 (CelestialPlot)", "celestial_plot_ms", "celestial_plot_peak_ms"),
        ("飞船轨迹重构 (VesselPlot)",    "vessel_plot_ms", "vessel_plot_peak_ms"),
    ]
    for label, avg_k, peak_k in subsystems:
        avg_v = safe_float(latest.get(avg_k, 0))
        peak_v = safe_float(latest.get(peak_k, 0))
        flag = " ⚠️ 瓶颈" if avg_v > 1.0 else ""
        print(f"  • {label:<28} {avg_v:6.2f} ms (峰值: {peak_v:5.1f} ms){flag}")
    print("-" * 80)

    print(f"【4. 后台星历推演控制台 (Rolling Ephemeris)】")
    ephem_active = latest.get("ephem_active", "false")
    ephem_carrier = latest.get("ephem_carrier", "None")
    horizon_s = safe_float(latest.get("ephem_horizon_s", 0))
    horizon_y = safe_float(latest.get("ephem_horizon_y", 0))
    target_y = safe_float(latest.get("ephem_lookahead_y", 0))
    progress = safe_float(latest.get("ephem_progress_ratio", 0)) * 100.0
    is_paused = latest.get("ephem_paused", "false")
    is_prolonging = latest.get("ephem_prolonging", "false")

    status = "⏸ 已暂停" if is_paused == "true" else ("⏳ 正在推演" if is_prolonging == "true" else "● 自动运行中")
    print(f"  • 运行状态:     {status}")
    print(f"  • 驱动核心:     {ephem_carrier}")
    print(f"  • 前瞻视界余量: +{horizon_y:.2f} 年 ({horizon_s:,.0f} 秒 / 目标 +{target_y:.1f} 年)")
    print(f"  • 视界进度:     {progress:5.1f}%")
    print("-" * 80)

    print(f"【5. 物理时钟解耦与异构调度总督 (Decoupler & Governor)】")
    decouple = latest.get("decouple_active", "false")
    snapshots = latest.get("snapshots_consumed", "0")
    staged = latest.get("staged_harvest_count", "0")
    gpu_name = latest.get("gpu_name", "Unknown")
    affinity = latest.get("governor_affinity", "AutoAdaptive")
    gpu_d = latest.get("gpu_dispatch_count", "0")
    cpu_d = latest.get("cpu_dispatch_count", "0")
    vac_bypassed = latest.get("bypassed_precalc_count", "0")
    cb_trips = latest.get("circuit_breaker_trips", "0")
    mesh_hit = safe_float(latest.get("cache_hit_ratio", 0))
    cel_hit = safe_float(latest.get("celestial_hit_ratio", 0))

    print(f"  • 时钟解耦模式: [{'自治无锁流转' if decouple == 'true' else '同步锁步'}] (快照消费: {snapshots} 次, 让步收获: {staged} 次)")
    print(f"  • 加速运算设备: {gpu_name}")
    print(f"  • 异构调度策略: {affinity} (GPU分流: {gpu_d} 次 | CPU分流: {cpu_d} 次)")
    print(f"  • 真空旁路节约: {vac_bypassed} 步物理预计算")
    print(f"  • 循环熔断次数: {cb_trips} 次")
    print(f"  • 轨迹缓存命中: 3D网格命中率 {mesh_hit:.1f}% | 天体缓存命中率 {cel_hit:.1f}%")
    print("=" * 80)


def generate_interactive_html(records, output_html_path):
    # Downsample or limit to last 300 samples for swift DOM rendering if dataset is massive
    sampled = records[-300:] if len(records) > 300 else records

    timestamps = [r.get("realtime_s", i) for i, r in enumerate(sampled)]
    fps_series = [safe_float(r.get("fps", 0)) for r in sampled]
    frametime_series = [safe_float(r.get("frametime_ms", 0)) for r in sampled]
    principia_series = [safe_float(r.get("principia_total_ms", 0)) for r in sampled]

    precalc_series = [safe_float(r.get("precalc_ms", 0)) for r in sampled]
    fixed_series = [safe_float(r.get("fixed_update_ms", 0)) for r in sampled]
    waited_series = [safe_float(r.get("waited_for_fixed_ms", 0)) for r in sampled]
    better_late_series = [safe_float(r.get("better_late_ms", 0)) for r in sampled]
    late_update_series = [safe_float(r.get("late_update_ms", 0)) for r in sampled]

    physics_adv_series = [safe_float(r.get("physics_advance_ms", 0)) for r in sampled]
    harvest_series = [safe_float(r.get("vessel_harvest_ms", 0)) for r in sampled]
    body_frames_series = [safe_float(r.get("body_frames_ms", 0)) for r in sampled]
    plotting_series = [safe_float(r.get("plotting_ms", 0)) for r in sampled]

    horizon_series = [safe_float(r.get("ephem_horizon_y", 0)) for r in sampled]
    cache_hit_series = [safe_float(r.get("cache_hit_ratio", 0)) for r in sampled]

    data_payload = {
        "labels": timestamps,
        "fps": fps_series,
        "frametime": frametime_series,
        "principia_total": principia_series,
        "precalc": precalc_series,
        "fixed": fixed_series,
        "waited": waited_series,
        "better_late": better_late_series,
        "late_update": late_update_series,
        "physics_adv": physics_adv_series,
        "harvest": harvest_series,
        "body_frames": body_frames_series,
        "plotting": plotting_series,
        "horizon_years": horizon_series,
        "cache_hit_ratio": cache_hit_series,
    }

    latest = records[-1] if records else {}

    html_content = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <title>Principia 实时性能遥测图表仪表盘</title>
  <style>
    :root {{
      --bg: #0d1117;
      --card-bg: #161b22;
      --border: #30363d;
      --text: #c9d1d9;
      --accent: #58a6ff;
      --green: #3fb950;
      --yellow: #d29922;
      --red: #f85149;
      --purple: #bc8cff;
    }}
    body {{
      margin: 0;
      padding: 24px;
      background: var(--bg);
      color: var(--text);
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
    }}
    h1, h2, h3 {{ color: #ffffff; margin-top: 0; }}
    .header {{
      display: flex;
      justify-content: space-between;
      align-items: center;
      border-bottom: 1px solid var(--border);
      padding-bottom: 16px;
      margin-bottom: 24px;
    }}
    .badge {{
      display: inline-block;
      padding: 4px 10px;
      border-radius: 12px;
      font-size: 12px;
      font-weight: 600;
      background: #238636;
      color: #fff;
    }}
    .grid {{
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: 16px;
      margin-bottom: 24px;
    }}
    .card {{
      background: var(--card-bg);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 16px;
      box-shadow: 0 4px 12px rgba(0,0,0,0.15);
    }}
    .stat-val {{
      font-size: 28px;
      font-weight: 700;
      color: #ffffff;
      margin: 8px 0;
    }}
    .stat-sub {{ font-size: 13px; color: #8b949e; }}
    .chart-container {{
      background: var(--card-bg);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 20px;
      margin-bottom: 24px;
    }}
    canvas {{
      width: 100% !important;
      height: 280px !important;
    }}
  </style>
  <script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
</head>
<body>
  <div class="header">
    <div>
      <h1>🚀 Principia 实时性能遥测仪表盘</h1>
      <div style="color: #8b949e; font-size: 14px;">数据点数: {len(records)} | 最新采集时间: {latest.get("timestamp", "N/A")}</div>
    </div>
    <div>
      <span class="badge">● 原生守护线程运行中</span>
    </div>
  </div>

  <div class="grid">
    <div class="card">
      <div class="stat-sub">帧率 / 总体耗时</div>
      <div class="stat-val" style="color: var(--green);">{safe_float(latest.get('fps', 0)):.1f} <span style="font-size: 16px; color: #8b949e;">FPS</span></div>
      <div class="stat-sub">帧耗时: <b>{safe_float(latest.get('frametime_ms', 0)):.2f} ms</b> (Principia: {safe_float(latest.get('principia_total_ms', 0)):.2f} ms)</div>
    </div>
    <div class="card">
      <div class="stat-sub">轨迹绘制 (Plotting)</div>
      <div class="stat-val" style="color: var(--accent);">{safe_float(latest.get('plotting_ms', 0)):.2f} <span style="font-size: 16px; color: #8b949e;">ms</span></div>
      <div class="stat-sub">峰值: {safe_float(latest.get('plotting_peak_ms', 0)):.1f} ms | 网格缓存命中率: <b>{safe_float(latest.get('cache_hit_ratio', 100)):.1f}%</b></div>
    </div>
    <div class="card">
      <div class="stat-sub">LateUpdate() 视口交互</div>
      <div class="stat-val" style="color: var(--yellow);">{safe_float(latest.get('late_update_ms', 0)):.2f} <span style="font-size: 16px; color: #8b949e;">ms</span></div>
      <div class="stat-sub">峰值限额: <b>{safe_float(latest.get('late_update_peak_ms', 0)):.1f} ms</b> (0.6ms 预算门禁保护)</div>
    </div>
    <div class="card">
      <div class="stat-sub">星历前瞻视界 (Ephemeris Horizon)</div>
      <div class="stat-val" style="color: var(--purple);">+{safe_float(latest.get('ephem_horizon_y', 0)):.2f} <span style="font-size: 16px; color: #8b949e;">年</span></div>
      <div class="stat-sub">独立守护线程 | 进度: {safe_float(latest.get('ephem_progress_ratio', 0))*100:.1f}%</div>
    </div>
  </div>

  <!-- Chart 1: Frame Timing & FPS -->
  <div class="chart-container">
    <h3>📈 1. 实时帧耗时与 FPS 趋势</h3>
    <canvas id="chartFrame"></canvas>
  </div>

  <!-- Chart 2: Lifecycle Breakdown -->
  <div class="chart-container">
    <h3>⏱️ 2. Unity 生命周期耗时剖析 (Precalc / Waited / BetterLate / LateUpdate)</h3>
    <canvas id="chartLifecycle"></canvas>
  </div>

  <!-- Chart 3: Core Subsystems -->
  <div class="chart-container">
    <h3>⚙️ 3. C++ 物理与渲染核心子系统耗时 (Physics / Harvest / Plotting)</h3>
    <canvas id="chartSubsystems"></canvas>
  </div>

  <!-- Chart 4: Horizon & Cache -->
  <div class="chart-container">
    <h3>🛡️ 4. 滚动星历视界与多级缓存命中率</h3>
    <canvas id="chartHorizon"></canvas>
  </div>

  <script>
    const data = {json.dumps(data_payload)};

    // Chart 1: Frame & FPS
    new Chart(document.getElementById('chartFrame'), {{
      type: 'line',
      data: {{
        labels: data.labels.map(t => typeof t === 'number' ? t.toFixed(1) + 's' : t),
        datasets: [
          {{ label: '总帧耗时 (ms)', data: data.frametime, borderColor: '#f85149', tension: 0.2, yAxisID: 'y' }},
          {{ label: 'Principia 耗时 (ms)', data: data.principia_total, borderColor: '#58a6ff', tension: 0.2, yAxisID: 'y' }},
          {{ label: '实时 FPS', data: data.fps, borderColor: '#3fb950', borderDash: [4, 4], yAxisID: 'y1' }}
        ]
      }},
      options: {{
        responsive: true,
        scales: {{
          y: {{ position: 'left', title: {{ display: true, text: '毫秒 (ms)' }} }},
          y1: {{ position: 'right', grid: {{ drawOnChartArea: false }}, title: {{ display: true, text: 'FPS' }} }}
        }}
      }}
    }});

    // Chart 2: Lifecycle
    new Chart(document.getElementById('chartLifecycle'), {{
      type: 'line',
      data: {{
        labels: data.labels.map(t => typeof t === 'number' ? t.toFixed(1) + 's' : t),
        datasets: [
          {{ label: 'Precalc (天体历表)', data: data.precalc, borderColor: '#bc8cff', tension: 0.2 }},
          {{ label: 'WaitedForFixed (动力学)', data: data.waited, borderColor: '#d29922', tension: 0.2 }},
          {{ label: 'BetterLate (收割)', data: data.better_late, borderColor: '#3fb950', tension: 0.2 }},
          {{ label: 'LateUpdate (视口交互)', data: data.late_update, borderColor: '#f85149', tension: 0.2 }}
        ]
      }},
      options: {{ responsive: true, scales: {{ y: {{ title: {{ display: true, text: '毫秒 (ms)' }} }} }} }}
    }});

    // Chart 3: Subsystems
    new Chart(document.getElementById('chartSubsystems'), {{
      type: 'line',
      data: {{
        labels: data.labels.map(t => typeof t === 'number' ? t.toFixed(1) + 's' : t),
        datasets: [
          {{ label: '轨迹绘制 (Plotting)', data: data.plotting, borderColor: '#58a6ff', tension: 0.2 }},
          {{ label: '载体收割 (Harvest)', data: data.harvest, borderColor: '#3fb950', tension: 0.2 }},
          {{ label: '天体框架 (BodyFrames)', data: data.body_frames, borderColor: '#bc8cff', tension: 0.2 }},
          {{ label: '物理推进 (AdvanceTime)', data: data.physics_adv, borderColor: '#d29922', tension: 0.2 }}
        ]
      }},
      options: {{ responsive: true, scales: {{ y: {{ title: {{ display: true, text: '毫秒 (ms)' }} }} }} }}
    }});

    // Chart 4: Horizon & Cache
    new Chart(document.getElementById('chartHorizon'), {{
      type: 'line',
      data: {{
        labels: data.labels.map(t => typeof t === 'number' ? t.toFixed(1) + 's' : t),
        datasets: [
          {{ label: '星历前瞻视界 (年)', data: data.horizon_years, borderColor: '#bc8cff', yAxisID: 'y' }},
          {{ label: '网格缓存命中率 (%)', data: data.cache_hit_ratio, borderColor: '#3fb950', yAxisID: 'y1' }}
        ]
      }},
      options: {{
        responsive: true,
        scales: {{
          y: {{ position: 'left', title: {{ display: true, text: '前瞻年数 (Years)' }} }},
          y1: {{ position: 'right', min: 0, max: 100, grid: {{ drawOnChartArea: false }}, title: {{ display: true, text: '命中率 (%)' }} }}
        }}
      }}
    }});
  </script>
</body>
</html>
"""

    with open(output_html_path, "w", encoding="utf-8") as f:
        f.write(html_content)
    print(f"✅ 已成功生成交互式性能可视化图表仪表盘: {output_html_path}")


def main():
    custom_path = sys.argv[1] if len(sys.argv) > 1 else None
    telemetry_file = find_telemetry_file(custom_path)

    if not telemetry_file:
        print("[!] 未检测到运行中的 PrincipiaTelemetry 文件。")
        print("    可在启动 KSP 后等待生成，或指定文件路径: python generate_telemetry_report.py <path>")
        return 1

    records = load_telemetry_data(telemetry_file)
    print_agent_summary(records, telemetry_file)

    html_out = os.path.join(os.path.dirname(os.path.abspath(telemetry_file)), "PrincipiaTelemetry_dashboard.html")
    generate_interactive_html(records, html_out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
