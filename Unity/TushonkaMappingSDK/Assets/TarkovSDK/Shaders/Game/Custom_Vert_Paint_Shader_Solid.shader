// Editor stand-in for the game shader "Custom/Vert Paint Shader Solid" (vertex-painted ground), lit as the
// raid lights its decompiled GBuffer pass (Tools/ShaderStandins/DECOMPILE.md). Three layers weighted by
// vertex colour and height maps. Note the game treats a layer's "Smoothness" as roughness (GBuffer1.a =
// 1 - smoothness). Swapped for the game's at raid start.
Shader "Tarkov/Custom/Vert Paint Shader Solid"
{
    Properties
    {
        _BlendStrength ("Blend Strength", Range(0.1, 30)) = 1
        [Space(30)] [Header(________________________________________________________________________________________________________________)] _Color0 ("Main Color 0", Color) = (1, 1, 1, 1)
        _MainTex0 ("Base (RGB) Smoothness (A) 0", 2D) = "white" {}
        [NoScaleOffset] [Normal] _BumpMap0 ("Normalmap 0", 2D) = "bump" {}
        _Smoothness0 ("Smoothness 0", Range(0, 1)) = 0
        _Specular0 ("Specular 0", Range(0, 1)) = 0
        _FresnelDiffuse0 ("Fresnel Diffuse 0", Range(0, 1)) = 0
        _FresnelSpecular0 ("Fresnel Specular 0", Range(0, 1)) = 0
        [Space(30)] [Header(________________________________________________________________________________________________________________)] _Color1 ("Main Color 1", Color) = (1, 1, 1, 1)
        _MainTex1 ("Base (RGB) Smoothness (A) 1", 2D) = "white" {}
        [NoScaleOffset] [Normal] _BumpMap1 ("Normalmap 1", 2D) = "bump" {}
        _Smoothness1 ("Smoothness 1", Range(0, 1)) = 0
        _Specular1 ("Specular 1", Range(0, 1)) = 0
        _FresnelDiffuse1 ("Fresnel Diffuse 1", Range(0, 1)) = 0
        _FresnelSpecular1 ("Fresnel Specular 1", Range(0, 1)) = 0
        [Space(30)] [Header(________________________________________________________________________________________________________________)] _Color2 ("Main Color 2", Color) = (1, 1, 1, 1)
        _MainTex2 ("Base (RGB) Smoothness (A) 2", 2D) = "white" {}
        [NoScaleOffset] [Normal] _BumpMap2 ("Normalmap 2", 2D) = "bump" {}
        _Smoothness2 ("Smoothness 2", Range(0, 1)) = 0
        _Specular2 ("Specular 2", Range(0, 1)) = 0
        _FresnelDiffuse2 ("Fresnel Diffuse 2", Range(0, 1)) = 0
        _FresnelSpecular2 ("Fresnel Specular 2", Range(0, 1)) = 0
        [Space(30)] [Header(________________________________________________________________________________________________________________)] _Heights ("Heights", 2D) = "white" {}
        [Space(30)] [Header(________________________________________________________________________________________________________________)] [Toggle(USE_MERGED_TEXTURES)] _UseMergedTextures ("Use Merged Textures", Float) = 0
        _ValsTex ("Vals factors", 2D) = "black" {}
        [Space(30)] [Header(________________________________________________________________________________________________________________)] _ReflectionStrength ("GI And Reflection Strength", Float) = 1
        _Cutoff ("_Cutoff", Float) = 0.85
        _Factor ("Z Offset Angle", Float) = 0
        _Units ("Z Offset Forward", Float) = 0
        _Temperature2 ("_Temperature2(min, max, factor)", Vector) = (0.1, 0.3, 0.35, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        CGINCLUDE
        #include "TarkovGame.cginc"
        #include "AutoLight.cginc"

        sampler2D _Heights, _MainTex0, _MainTex1, _MainTex2, _BumpMap0, _BumpMap1, _BumpMap2, _ValsTex;
        float4 _Heights_ST, _MainTex0_ST, _MainTex1_ST, _MainTex2_ST;
        float4 _Color0, _Color1, _Color2;
        float _BlendStrength, _ReflectionStrength;
        float _Smoothness0, _Smoothness1, _Smoothness2, _Specular0, _Specular1, _Specular2;
        float _FresnelDiffuse0, _FresnelDiffuse1, _FresnelDiffuse2, _FresnelSpecular0, _FresnelSpecular1, _FresnelSpecular2;
        float4 _Temperature2;

        struct v2f
        {
            float4 pos : SV_POSITION;
            float4 tbn0 : TEXCOORD0;
            float4 tbn1 : TEXCOORD1;
            float4 tbn2 : TEXCOORD2;
            float4 uv01 : TEXCOORD3;
            float4 uv2h : TEXCOORD4;
            float4 color : COLOR;
            UNITY_LIGHTING_COORDS(5, 6)
        };

        v2f vert(appdata_full v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 t = normalize(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz));
            float3 n = normalize(mul(v.normal, (float3x3)unity_WorldToObject));
            float3 b = cross(n, t) * v.tangent.w;
            o.tbn0 = float4(t.x, b.x, n.x, worldPos.x);
            o.tbn1 = float4(t.y, b.y, n.y, worldPos.y);
            o.tbn2 = float4(t.z, b.z, n.z, worldPos.z);
            o.uv01 = float4(TRANSFORM_TEX(v.texcoord, _MainTex0), TRANSFORM_TEX(v.texcoord, _MainTex1));
            o.uv2h = float4(TRANSFORM_TEX(v.texcoord, _MainTex2), TRANSFORM_TEX(v.texcoord, _Heights));
            o.color = v.color;
            UNITY_TRANSFER_LIGHTING(o, v.texcoord1.xy);
            return o;
        }

        // the GBuffer pass; alsoFills the Unity reflection term the game writes into GBuffer3
        TarkovSurface surface(v2f i, float3 worldPos)
        {
            float3 w;
            float4 base;
            float3 bump;
        #if defined(USE_MERGED_TEXTURES)
            w = tex2D(_ValsTex, i.uv01.xy).rgb;
            base = tex2D(_MainTex0, i.uv01.xy);
            bump = tex2D(_BumpMap0, i.uv01.xy).xyw;
        #else
            w = float3(tex2D(_Heights, i.uv01.xy).r, tex2D(_Heights, i.uv01.zw).r, tex2D(_Heights, i.uv2h.xy).r) * i.color.rgb;
            w = pow(w, _BlendStrength);
            w /= w.x + w.y + w.z;
            base = tex2D(_MainTex0, i.uv01.xy) * _Color0 * w.x + tex2D(_MainTex1, i.uv01.zw) * _Color1 * w.y + tex2D(_MainTex2, i.uv2h.xy) * _Color2 * w.z;
            bump = tex2D(_BumpMap0, i.uv01.xy).xyw * w.x + tex2D(_BumpMap1, i.uv01.zw).xyw * w.y + tex2D(_BumpMap2, i.uv2h.xy).xyw * w.z;
        #endif
            float3 nt;
            nt.xy = float2(bump.z * bump.x, bump.y) * 2 - 1;
            nt.z = sqrt(1 - min(dot(nt.xy, nt.xy), 1));
            float3 n = float3(dot(i.tbn0.xyz, nt), dot(i.tbn1.xyz, nt), dot(i.tbn2.xyz, nt));
            float3 viewDir = normalize(_WorldSpaceCameraPos - worldPos);
            float nv = dot(viewDir, n);
            float fresnelSpec = dot(w, float3(_FresnelSpecular0, _FresnelSpecular1, _FresnelSpecular2));
            float fresnelDiff = dot(w, float3(_FresnelDiffuse0, _FresnelDiffuse1, _FresnelDiffuse2));
            float fs = 1 - nv * fresnelSpec; fs = fs * fs; fs = fs * fs;
            float fd = 1 - nv * fresnelDiff; fd = fd * fd; fd = fd * fd;
            float3 albedo = base.rgb * fd;
            float smoothness = dot(w, float3(_Smoothness0, _Smoothness1, _Smoothness2));
            float spec = dot(w, float3(_Specular0, _Specular1, _Specular2)) * base.a;
            float3 specColor = fs * spec;
            // the reflection the game writes into GBuffer3: Unity's probes at a mip from "smoothness" used as roughness
            float3 r = reflect(-viewDir, n);
            float3 probe = TarkovUnityProbes(worldPos, r, smoothness);
            float3 emission = fs * (fs * spec * _ReflectionStrength) * probe;
            float3 thermal = min(max(albedo * _Temperature2.z, _Temperature2.x), _Temperature2.y) + _Temperature2.w;
            TarkovSurface s;
            s.albedo = (_ThermalVisionOn > 0) ? thermal : albedo;
            s.occlusion = 1;
            s.specColor = specColor;
            s.smoothness = 1 - smoothness;
            s.normal = normalize(n);
            s.emission = emission;
            return s;
        }

        float4 fragBase(v2f i) : SV_Target
        {
            float3 worldPos = float3(i.tbn0.w, i.tbn1.w, i.tbn2.w);
            TarkovSurface s = surface(i, worldPos);
            float3 toPixel = normalize(worldPos - _WorldSpaceCameraPos);
            UNITY_LIGHT_ATTENUATION(atten, i, worldPos)
            float3 color = s.emission + TarkovScreenAmbient(s, toPixel);
            color += TarkovDeferredLight(s, -toPixel, _WorldSpaceLightPos0.xyz, _LightColor0.rgb * atten);
            return float4(TarkovApplyFog(color, worldPos), 1);
        }

        float4 fragAdd(v2f i) : SV_Target
        {
            float3 worldPos = float3(i.tbn0.w, i.tbn1.w, i.tbn2.w);
            TarkovSurface s = surface(i, worldPos);
            float3 viewDir = normalize(_WorldSpaceCameraPos - worldPos);
            float3 lightDir = normalize(UnityWorldSpaceLightDir(worldPos));
            UNITY_LIGHT_ATTENUATION(atten, i, worldPos)
            float3 color = TarkovDeferredLight(s, viewDir, lightDir, _LightColor0.rgb * atten);
            return float4(color * TarkovFogFade(worldPos), 1);
        }
        ENDCG

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Offset [_Factor], [_Units]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBase
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma shader_feature USE_MERGED_TEXTURES
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            ZWrite Off
            Offset [_Factor], [_Units]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAdd
            #pragma target 3.0
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #pragma shader_feature USE_MERGED_TEXTURES
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ShadowCaster" }
            CGPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma target 3.0
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            struct v2fs { V2F_SHADOW_CASTER; };
            v2fs vertShadow(appdata_base v) { v2fs o; TRANSFER_SHADOW_CASTER_NORMALOFFSET(o) return o; }
            float4 fragShadow(v2fs i) : SV_Target { SHADOW_CASTER_FRAGMENT(i) }
            ENDCG
        }
    }
    FallBack Off
}
