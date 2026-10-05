using System;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

public static class GpuTrajectoryRenderer {
  private static ComputeBuffer indirect_args_buffer_;
  private static ComputeBuffer vertex_output_buffer_;

  public const int MaxVertices = 65536;

  public static bool IsGpuRenderingEnabled =>
      PrincipiaGPGPUContext.SupportsCompute &&
      PrincipiaGPGPUContext.trajectory_tessellator != null &&
      !HeterogeneousDynamicGovernor.IsBackpressureActive;

  public static void EnsureBuffersAllocated() {
    if (indirect_args_buffer_ == null || !indirect_args_buffer_.IsValid()) {
      // 4 uints: [vertexCountPerInstance, instanceCount, startVertexLocation, startInstanceLocation]
      indirect_args_buffer_ = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
      indirect_args_buffer_.SetData(new uint[] { 0, 1, 0, 0 });
    }
    if (vertex_output_buffer_ == null || !vertex_output_buffer_.IsValid()) {
      // struct OutputVertex { float3 position; float4 color; } => 28 bytes
      vertex_output_buffer_ = new ComputeBuffer(MaxVertices, sizeof(float) * 7);
    }
  }

  public static void ReleaseBuffers() {
    indirect_args_buffer_?.Release();
    indirect_args_buffer_ = null;
    vertex_output_buffer_?.Release();
    vertex_output_buffer_ = null;
    NativeZeroCopyTrajectoryPipeline.Dispose();
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
