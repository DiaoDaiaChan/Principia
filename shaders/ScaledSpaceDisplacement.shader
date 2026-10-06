Shader "Principia/ScaledSpaceDisplacement" {
  Properties {
    _MainTex ("Albedo", 2D) = "white" {}
    _Color ("Color Tint", Color) = (1,1,1,1)
    _EmissionColor ("Emission Color", Color) = (0,0,0,0)
    _WorldOffset ("World Offset", Vector) = (0,0,0,0)
    _VirtualDisplacement ("Virtual Displacement", Vector) = (0,0,0,0)
    _ScaleFactor ("Scale Factor", Float) = 1.0
  }
  SubShader {
    Tags { "RenderType"="Opaque" "Queue"="Geometry" }
    LOD 150

    Pass {
      Name "ForwardBase"
      Tags { "LightMode"="ForwardBase" }

      CGPROGRAM
      #pragma target 3.0
      #pragma vertex vert
      #pragma fragment frag
      #include "UnityCG.cginc"

      struct appdata {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float2 uv : TEXCOORD0;
      };

      struct v2f {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        float3 worldNormal : TEXCOORD1;
      };

      sampler2D _MainTex;
      float4 _MainTex_ST;
      fixed4 _Color;
      fixed4 _EmissionColor;
      float4 _WorldOffset;
      float4 _VirtualDisplacement;
      float _ScaleFactor;

      v2f vert(appdata v) {
        v2f o;
        // Transform local object space vertex into world coordinates.
        float4 worldPos = mul(unity_ObjectToWorld, v.vertex);

        // Apply GPU virtual projection: sub-millisecond precision displacement and
        // floating-origin / Krakensbane drift offsets directly in the vertex stage.
        worldPos.xyz += (_WorldOffset.xyz + _VirtualDisplacement.xyz) * _ScaleFactor;

        o.pos = mul(UNITY_MATRIX_VP, worldPos);
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        o.worldNormal = UnityObjectToWorldNormal(v.normal);
        return o;
      }

      fixed4 frag(v2f i) : SV_Target {
        fixed4 tex = tex2D(_MainTex, i.uv);
        fixed4 col = tex * _Color + _EmissionColor;
        return col;
      }
      ENDCG
    }
  }
  Fallback "Diffuse"
}
