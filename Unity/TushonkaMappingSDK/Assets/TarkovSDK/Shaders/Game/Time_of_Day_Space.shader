// Editor stand-in for the game shader "Time of Day/Space" (the stars), decompiled
// (Tools/ShaderStandins/DECOMPILE.md): the star texture tiled over the dome, fading towards the horizon.
Shader "Tarkov/Time of Day/Space"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background+10" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite Off
            Cull Front
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "TarkovGame.cginc"

            sampler2D _MainTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 uvBright : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 viewPos = mul((float3x3)UNITY_MATRIX_V, v.vertex.xyz);
                float4 clip = mul(UNITY_MATRIX_P, float4(viewPos, 1));
                o.pos = float4(clip.xy, 0, clip.w);
                o.normal = v.normal;
                float3 worldNormal = normalize(mul((float3x3)unity_ObjectToWorld, v.normal));
                o.uvBright.z = max(0, worldNormal.y) * abs(v.normal.y) * TOD_SpaceBrightness;
                o.uvBright.xy = v.normal.xz / (abs(v.normal.y) + 1) * TOD_SpaceTiling;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uvBright.xy * ((i.normal.y > 0) ? 1 : -1);
                return float4(tex2D(_MainTex, uv).rgb * i.uvBright.z, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
