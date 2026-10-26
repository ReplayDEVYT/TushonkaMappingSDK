// Editor stand-in for the game shader "Time of Day/Moon", decompiled (Tools/ShaderStandins/DECOMPILE.md):
// the moon texture lit by the sun's direction. Drawn around the camera on the far plane.
Shader "Tarkov/Time of Day/Moon"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background+30" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "TarkovGame.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 uvHeight : TEXCOORD0;
                float3 normal : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float4 clip = mul(UNITY_MATRIX_VP, float4(worldPos + _WorldSpaceCameraPos, 1));
                o.pos = float4(clip.xy, 0, clip.w);
                o.uvHeight.xy = TRANSFORM_TEX(v.texcoord, _MainTex);
                float h = mul(TOD_World2Sky, float4(worldPos, 1)).y * 2;
                o.uvHeight.z = h * h;
                o.normal = normalize(mul((float3x3)unity_ObjectToWorld, v.normal));
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float lit = pow(max(0, dot(i.normal, TOD_SunDirection)), TOD_MoonMeshContrast) * saturate(i.uvHeight.z) * TOD_MoonMeshBrightness;
                return float4(lit * tex2D(_MainTex, i.uvHeight.xy).rgb, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
