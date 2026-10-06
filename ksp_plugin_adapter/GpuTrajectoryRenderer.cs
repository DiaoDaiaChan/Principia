using System;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

internal static class GpuTrajectoryRenderer {
  public const int MaxVertices = 65536;

  private static ComputeBuffer indirect_args_buffer_;
  private static ComputeBuffer vertex_output_buffer_;
  private static ComputeBuffer input_nodes_buffer_;

  public struct TrajectoryNode {
    public Vector3 position_scaled;
    public float time;
  }

  public struct OutputVertex {
    public Vector3 position;
    public Color color;
  }

  private static TrajectoryNode[] managed_nodes_ = new TrajectoryNode[MaxVertices];
  private static readonly uint[] indirect_args_init_ = new uint[] { 0, 1, 0, 0 };

  public static bool IsGpuRenderingEnabled =>
      HeterogeneousDynamicGovernor.IsGpuAccelerated &&
      PrincipiaGPGPUContext.SupportsCompute &&
      PrincipiaGPGPUContext.trajectory_tessellator != null &&
      !HeterogeneousDynamicGovernor.IsBackpressureActive;

  public static void EnsureBuffersAllocated() {
    if (indirect_args_buffer_ == null || !indirect_args_buffer_.IsValid()) {
      // 4 uints: [vertexCountPerInstance, instanceCount, startVertexLocation, startInstanceLocation]
      indirect_args_buffer_ = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
      indirect_args_buffer_.SetData(indirect_args_init_);
    }
    if (vertex_output_buffer_ == null || !vertex_output_buffer_.IsValid()) {
      // struct OutputVertex { float3 position; float4 color; } => 28 bytes
      vertex_output_buffer_ = new ComputeBuffer(MaxVertices, sizeof(float) * 7);
    }
    if (input_nodes_buffer_ == null || !input_nodes_buffer_.IsValid()) {
      // struct TrajectoryNode { float3 position_scaled; float time; } => 16 bytes
      input_nodes_buffer_ = new ComputeBuffer(MaxVertices, sizeof(float) * 4);
    }
  }

  public static void ReleaseBuffers() {
    indirect_args_buffer_?.Release();
    indirect_args_buffer_ = null;
    vertex_output_buffer_?.Release();
    vertex_output_buffer_ = null;
    input_nodes_buffer_?.Release();
    input_nodes_buffer_ = null;
  }

  public static bool RenderTrajectory(
      Vector3[] vertices,
      int count,
      Color colour,
      GLLines.Style style,
      Camera camera,
      Vector3 draw_position) {
    if (!IsGpuRenderingEnabled || vertices == null || count < 2 || camera == null) {
      return false;
    }

    EnsureBuffersAllocated();
    ComputeShader cs = PrincipiaGPGPUContext.trajectory_tessellator;
    if (cs == null) {
      return false;
    }

    int safe_count = Math.Min(count, MaxVertices);
    if (managed_nodes_.Length < safe_count) {
      managed_nodes_ = new TrajectoryNode[safe_count];
    }

    // Zero-GC transfer from vertices into managed_nodes_ buffer
    for (int i = 0; i < safe_count; ++i) {
      managed_nodes_[i].position_scaled = vertices[i] + draw_position;
      managed_nodes_[i].time = (float)i;
    }
    input_nodes_buffer_.SetData(managed_nodes_, 0, 0, safe_count);

    indirect_args_init_[0] = 0;
    indirect_args_init_[1] = 1;
    indirect_args_init_[2] = 0;
    indirect_args_init_[3] = 0;
    indirect_args_buffer_.SetData(indirect_args_init_);

    int kernel = cs.FindKernel("TessellateTrajectoryScreenAdaptive");
    cs.SetMatrix("_WorldToCameraMatrix", camera.worldToCameraMatrix);
    cs.SetMatrix("_CameraToScreenMatrix", camera.projectionMatrix);
    cs.SetVector("_BaseColor", colour);

    Color fade_color = (style == GLLines.Style.Faded)
        ? new Color(colour.r, colour.g, colour.b, colour.a * 0.2f)
        : colour;
    cs.SetVector("_FadeColor", fade_color);

    cs.SetInt("_NodeCount", safe_count);
    cs.SetFloat("_CurvatureTolerancePixels", 2.0f);
    cs.SetFloat("_CameraDistanceFactor", 1.0f);
    cs.SetInt("_MaxOutputVertices", MaxVertices);

    cs.SetBuffer(kernel, "_InputNodes", input_nodes_buffer_);
    cs.SetBuffer(kernel, "_OutputVertices", vertex_output_buffer_);
    cs.SetBuffer(kernel, "_IndirectArgs", indirect_args_buffer_);

    int groups = Mathf.CeilToInt((safe_count - 1) / 128.0f);
    cs.Dispatch(kernel, groups, 1, 1);

    Material mat = GLLines.line_material;
    if (mat != null) {
      mat.SetBuffer("_VertexBuffer", vertex_output_buffer_);
      mat.SetPass(0);
      Graphics.DrawProceduralIndirectNow(
          MeshTopology.Lines,
          indirect_args_buffer_,
          0);
      GpuModuleTelemetry.RecordDispatch(
          GpuModuleId.TrajectoryTessellator,
          0.05f,
          $"{safe_count} 节点切比雪夫自适应细分绘制");
      return true;
    }
    return false;
  }

  public static void RenderProcedural(Material material, Camera camera) {
    if (!IsGpuRenderingEnabled || material == null || camera == null) return;

    EnsureBuffersAllocated();
    material.SetBuffer("_VertexBuffer", vertex_output_buffer_);
    material.SetPass(0);

    Graphics.DrawProceduralIndirectNow(
        MeshTopology.Lines,
        indirect_args_buffer_,
        0);
  }
}

}
