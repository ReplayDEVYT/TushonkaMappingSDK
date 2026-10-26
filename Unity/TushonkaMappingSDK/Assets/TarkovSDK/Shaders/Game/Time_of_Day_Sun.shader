// Editor stand-in for the game shader "Time of Day/Sun" (the sun disc billboard), decompiled
// (Tools/ShaderStandins/DECOMPILE.md). Drawn around the camera on the far plane.
Shader "Tarkov/Time of Day/Sun"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background+120" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "TarkovGame.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 uvHeight : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float4 clip = mul(UNITY_MATRIX_VP, float4(worldPos + _WorldSpaceCameraPos, 1));
                o.pos = float4(clip.xy, 0, clip.w);
                o.uvHeight.xy = v.texcoord.xy * 2 - 1;
                o.uvHeight.z = mul(TOD_World2Sky, float4(worldPos, 1)).y * 25;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float d = length(i.uvHeight.xy);
                float t = saturate(2 * (d - 0.5));
                float disc = 1 - t * t * (3 - 2 * t);
                float a = pow(disc, TOD_SunMeshContrast) * saturate(i.uvHeight.z) * TOD_SunMeshBrightness;
                return float4(a * TOD_SunMeshColor, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
