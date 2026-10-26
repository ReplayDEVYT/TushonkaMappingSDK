// Editor stand-in for the game shader "Time of Day/Skybox", decompiled (Tools/ShaderStandins/DECOMPILE.md):
// the sky's scattering per vertex from the TOD_* globals TarkovShaderGlobals sets. The editor draws it as
// the skybox of maps with the full weather; in the raid the TOD dome draws over it.
Shader "Tarkov/Time of Day/Skybox"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Pass
        {
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "TarkovGame.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                float3 dir = normalize(vertex.xyz);
                float3 rayleigh, mie;
                TarkovScatterIntegral(dir, rayleigh, mie);
                float cosSun = dot(TOD_LocalSunDirection, dir);
                float3 color = rayleigh * (0.75 + 0.75 * cosSun * cosSun);
                color += TOD_MoonSkyColor * (1 - dir.y);
                float lum = (color.r + color.g + color.b) * 0.333;
                color = lerp(color, lum, TOD_Fogginess);
                float t = saturate(-0.8 * dir.y);
                t = t * t * (3 - 2 * t);
                color = lerp(color, TOD_GroundColor, t);
                color = pow(color * TOD_ScatteringBrightness, TOD_Contrast);
                float3 below = pow(TOD_AmbientColor, TOD_Contrast);
                o.color = float4(dir.y < 0 ? below : color, 1);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return sqrt(i.color);
            }
            ENDCG
        }
    }
    FallBack Off
}
