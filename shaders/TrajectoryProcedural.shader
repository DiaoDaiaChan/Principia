Shader "Principia/TrajectoryProcedural" {
  Properties {
    _LineWidth ("Line Width", Float) = 1.0
  }
  SubShader {
    Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
    Blend SrcAlpha OneMinusSrcAlpha
    ZWrite Off
    Cull Off

    Pass {
      CGPROGRAM
      #pragma target 5.0
      #pragma vertex vert
      #pragma fragment frag
      #include "UnityCG.cginc"

      struct OutputVertex {
        float3 position;
        float4 color;
      };

      StructuredBuffer<OutputVertex> _VertexBuffer;

      struct v2f {
        float4 pos : SV_POSITION;
        float4 color : COLOR;
      };

      v2f vert(uint vertexID : SV_VertexID) {
        v2f o;
        OutputVertex v = _VertexBuffer[vertexID];
        float4 worldPos = float4(v.position, 1.0);
        o.pos = mul(UNITY_MATRIX_VP, worldPos);
        o.color = v.color;
        return o;
      }

      float4 frag(v2f i) : SV_Target {
        if (i.color.a <= 0.001) {
          discard;
        }
        return i.color;
      }
      ENDCG
    }
  }
  Fallback Off
}
