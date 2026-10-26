// Editor stand-in for the game shader "Global Fog/Particle Alpha Blended Lighted" (fog volumes),
// decompiled (Tools/ShaderStandins/DECOMPILE.md): vertex lit by Unity's four vertex lights and the ambient,
// fogged per vertex towards the atmospheric scattering colour. Swapped for the game's at raid start.
Shader "Tarkov/Global Fog/Particle Alpha Blended Lighted"
{
    Properties
    {
        _TintColor ("Tint Color", Color) = (0.5, 0.5, 0.5, 0.5)
        _MainTex ("Particle Texture", 2D) = "white" {}
        _InvFade ("Soft Particles Factor", Range(0.01, 3)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" "LightMode" = "Vertex" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            ColorMask RGB
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "TarkovGame.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _TintColor;
            float _InvFade;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float4 fog : TEXCOORD2;   // rgb the fog colour, a the amount
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                float3 viewPos = UnityObjectToViewPos(v.vertex);
                o.pos = mul(UNITY_MATRIX_P, float4(viewPos, 1));
                // Unity's vertex lights, no normal: 1 / (1 + d^2 * atten)
                float3 light = 2 * glstate_lightmodel_ambient.rgb;
                for (int l = 0; l < 4; l++)
                {
                    float3 toLight = unity_LightPosition[l].xyz - viewPos * unity_LightPosition[l].w;
                    float atten = 1 / (1 + dot(toLight, toLight) * unity_LightAtten[l].z);
                    light += unity_LightColor[l].rgb * atten;
                }
                float4 tint = v.color * _TintColor;
                o.color = tint * float4(light, 1);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 ray = worldPos - _WorldSpaceCameraPos;
                float dist = length(ray);
                float heightTerm = 1 / (1 + max(0, (ray.y + _Density.y) * _Density.x));
                float amount = 1 - exp(-min(max(_Density.z, 0.001) * heightTerm * dist, 10));
                float3 dir = normalize(mul((float3x3)TOD_World2Sky, ray));
                float3 rayleigh, mie;
                TarkovScatterIntegral(dir, rayleigh, mie);
                o.fog = float4(sqrt(2 * TarkovSkyColorLinear(dir, rayleigh, mie)), amount * _TarkovScattering);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv) * i.color;
                c.rgb *= 1 + _EFT_Ambient;
                c.rgb = lerp(c.rgb, i.fog.rgb, i.fog.a);
                return c;
            }
            ENDCG
        }
    }
    FallBack Off
}
