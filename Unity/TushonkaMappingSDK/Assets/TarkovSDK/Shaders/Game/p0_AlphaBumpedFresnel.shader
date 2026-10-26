// Editor stand-in for the game shader "p0/AlphaBumpedFresnel", decompiled from its forward passes
// (Tools/ShaderStandins/DECOMPILE.md): alpha-blended, diffuse only, with BSG's _DefVals fresnel.
// Swapped for the game's at raid start.
Shader "Tarkov/p0/AlphaBumpedFresnel"
{
    Properties
    {
        _Color ("Main Color", Color) = (1, 1, 1, 1)
        _MainTex ("Base (RGB)", 2D) = "white" {}
        _AddAlpha ("Add Alpha", Range(-1, 1)) = 0
        _BumpMap ("Normalmap", 2D) = "bump" {}
        _DefVals ("Defuse Vals", Vector) = (0.1, 2.5, 0, 0)
        _Temperature ("_Temperature(min, max, factor)", Vector) = (0.1, 0.12, 0.4, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest+2" }

        CGINCLUDE
        #include "TarkovGame.cginc"
        #include "AutoLight.cginc"

        sampler2D _MainTex, _BumpMap;
        float4 _MainTex_ST, _BumpMap_ST;
        float4 _Color, _DefVals, _Temperature;
        float _AddAlpha;

        struct v2f
        {
            float4 pos : SV_POSITION;
            float4 uv : TEXCOORD0;
            float4 tbn0 : TEXCOORD1;
            float4 tbn1 : TEXCOORD2;
            float4 tbn2 : TEXCOORD3;
            UNITY_LIGHTING_COORDS(4, 5)
        };

        v2f vert(appdata_full v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = float4(TRANSFORM_TEX(v.texcoord, _MainTex), TRANSFORM_TEX(v.texcoord, _BumpMap));
            float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 t = normalize(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz));
            float3 n = normalize(mul(v.normal, (float3x3)unity_WorldToObject));
            float3 b = cross(n, t) * (v.tangent.w * unity_WorldTransformParams.w);
            o.tbn0 = float4(t.x, b.x, n.x, worldPos.x);
            o.tbn1 = float4(t.y, b.y, n.y, worldPos.y);
            o.tbn2 = float4(t.z, b.z, n.z, worldPos.z);
            UNITY_TRANSFER_LIGHTING(o, v.texcoord1.xy);
            return o;
        }

        // albedo, alpha and the world normal
        float4 surface(v2f i, float3 worldPos, out float3 n)
        {
            float4 tex = tex2D(_MainTex, i.uv.xy);
            float4 bump = tex2D(_BumpMap, i.uv.zw);
            float3 nt;
            nt.xy = float2(bump.a * bump.r, bump.g) * 2 - 1;
            nt.z = sqrt(1 - min(dot(nt.xy, nt.xy), 1));
            float3 viewDir = normalize(_WorldSpaceCameraPos - worldPos);
            float3 viewTS = normalize(float3(dot(viewDir, float3(i.tbn0.x, i.tbn1.x, i.tbn2.x)), dot(viewDir, float3(i.tbn0.y, i.tbn1.y, i.tbn2.y)), dot(viewDir, float3(i.tbn0.z, i.tbn1.z, i.tbn2.z))));
            float f = 1 - dot(viewTS, nt);
            f = f * f * 0.5;
            float3 albedo = tex.rgb * _Color.rgb * (_DefVals.x + _DefVals.y * f);
            float3 thermal = min(max(albedo * _Temperature.z, _Temperature.x), _Temperature.y) + _Temperature.w;
            albedo = (_ThermalVisionOn > 0) ? thermal : albedo;
            n = normalize(float3(dot(i.tbn0.xyz, nt), dot(i.tbn1.xyz, nt), dot(i.tbn2.xyz, nt)));
            return float4(albedo, tex.a * _Color.a + _AddAlpha);
        }

        float4 fragBase(v2f i) : SV_Target
        {
            float3 worldPos = float3(i.tbn0.w, i.tbn1.w, i.tbn2.w);
            float3 n;
            float4 c = surface(i, worldPos, n);
            UNITY_LIGHT_ATTENUATION(atten, i, worldPos)
            float nl = max(0, dot(n, _WorldSpaceLightPos0.xyz));
            float3 color = c.rgb * _LightColor0.rgb * atten * nl + c.rgb * TarkovLinearToGamma(ShadeSH9(float4(n, 1)));
            return float4(TarkovApplyFog(color, worldPos), c.a);
        }

        float4 fragAdd(v2f i) : SV_Target
        {
            float3 worldPos = float3(i.tbn0.w, i.tbn1.w, i.tbn2.w);
            float3 n;
            float4 c = surface(i, worldPos, n);
            float3 lightDir = normalize(UnityWorldSpaceLightDir(worldPos));
            UNITY_LIGHT_ATTENUATION(atten, i, worldPos)
            float nl = max(0, dot(n, lightDir));
            return float4(c.rgb * _LightColor0.rgb * atten * nl * TarkovFogFade(worldPos), c.a);
        }
        ENDCG

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ColorMask RGB
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBase
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            Blend SrcAlpha One
            ZWrite Off
            ColorMask RGB
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAdd
            #pragma target 3.0
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            ENDCG
        }
    }
    FallBack Off
}
