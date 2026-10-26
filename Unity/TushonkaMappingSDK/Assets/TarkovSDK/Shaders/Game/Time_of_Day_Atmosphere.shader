// Editor stand-in for the game shader "Time of Day/Atmosphere" (the sky dome), decompiled
// (Tools/ShaderStandins/DECOMPILE.md): the scattering integral per vertex, the phase functions per pixel,
// additive over the stars. Drawn around the camera (the view's translation is dropped).
Shader "Tarkov/Time of Day/Atmosphere"
{
    Properties
    {
        _DitheringTexture ("Dithering Lookup Texture (A)", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background+40" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite Off
            Cull Front
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "TarkovGame.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 rayleigh : TEXCOORD0;
                float3 mie : TEXCOORD1;
                float3 dir : TEXCOORD2;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                float3 viewPos = mul((float3x3)UNITY_MATRIX_V, vertex.xyz);
                float4 clip = mul(UNITY_MATRIX_P, float4(viewPos, 1));
                o.pos = float4(clip.xy, 0, clip.w);   // on the far plane
                TarkovScatterIntegral(vertex.xyz, o.rayleigh, o.mie);
                o.dir = vertex.xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float3 color = TarkovSkyColorLinear(dir, i.rayleigh, i.mie);
                return min(float4(sqrt(color), 1), 4);
            }
            ENDCG
        }
    }
    FallBack Off
}
