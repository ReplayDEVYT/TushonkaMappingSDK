// Editor stand-in for the game shader "Global Fog/Transparent Reflective Specular" (glass), decompiled
// from its forward pass (Tools/ShaderStandins/DECOMPILE.md): Unity's Standard BRDF with BSG's sky
// reflection, _EFT_Ambient and per-vertex atmospheric fog. The game samples its own cascaded shadow map
// here; the editor uses Unity's shadow attenuation for the sun instead. Swapped for the game's at raid start.
Shader "Tarkov/Global Fog/Transparent Reflective Specular"
{
    Properties
    {
        _MainTex ("Base (RGB) Gloss (A)", 2D) = "white" {}
        _Color ("Main Color", Color) = (1, 1, 1, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0.5
        _AlphaOffset ("Alpha Offset", Range(-1, 1)) = 0
        _DirtStrength ("Dirt Strength", Range(0, 64)) = 3
        _GlobalReflectionStrength ("_GlobalReflectionStrength", Float) = 0.2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        LOD 300

        CGINCLUDE
        #include "TarkovGame.cginc"
        #include "AutoLight.cginc"

        sampler2D _MainTex;
        float4 _MainTex_ST;
        float4 _Color;
        float _Smoothness, _Metallic, _AlphaOffset, _DirtStrength, _GlobalReflectionStrength;

        struct v2f
        {
            float4 pos : SV_POSITION;
            float3 uvFog : TEXCOORD0;     // xy uv, z the per-vertex fog amount
            float3 normal : TEXCOORD1;
            float3 worldPos : TEXCOORD2;
            UNITY_LIGHTING_COORDS(3, 4)
        };

        v2f vert(appdata_full v)
        {
            v2f o;
            UNITY_INITIALIZE_OUTPUT(v2f, o);
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uvFog.xy = TRANSFORM_TEX(v.texcoord, _MainTex);
            o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.normal = UnityObjectToWorldNormal(v.normal);
            // the scattering fog per vertex, with a second term that fades in over the first metre
            float3 ray = o.worldPos - _WorldSpaceCameraPos;
            float dist = length(ray);
            float heightTerm = 1 / (1 + max(0, (ray.y + _Density.y) * _Density.x));
            float density = max(_Density.z, 0.001);
            float a = 1 - exp(-min(heightTerm * dist * density, 10));
            float b = 1 - exp(-1.5 * density * max(dist - 1, 0));
            o.uvFog.z = max(a * b, 0.001) * _TarkovScattering;
            UNITY_TRANSFER_LIGHTING(o, v.texcoord1.xy);
            return o;
        }

        float4 fragBase(v2f i) : SV_Target
        {
            float4 tex = tex2D(_MainTex, i.uvFog.xy);
            float3 c = tex.rgb * _Color.rgb;
            float alpha = saturate(pow(tex.a, _DirtStrength) + _AlphaOffset);
            float metallic = tex.a * _Metallic;
            float3 n = normalize(i.normal);
            float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
            float3 toPixel = -viewDir;

            // the sky reflection (Unity_GlossyEnvironment on AmbientLight's cubemap)
            float pr = 1 - _Smoothness;
            float roughness = max(pr * pr, 0.002);
            float surfaceReduction = 1 - 0.28 * roughness * pr;
            float mip = pr * (1.7 - 0.7 * pr) * 6;
            float3 r = reflect(toPixel, n);
            float3 probe = texCUBElod(_MyGlobalReflectionProbe, float4(r, mip)).rgb * saturate(r.y + _ReflectionBottomShade) * surfaceReduction;
            float nv = dot(n, viewDir);
            float fres = 1 - abs(nv);
            fres = fres * fres * fres * fres * fres;
            float grazing = saturate(metallic + _Smoothness);
            float3 refl = probe * (metallic + fres * (grazing - metallic)) * _GlobalReflectionStrength;
            float3 ambient = refl * _GlobalReflectionStrength + _Color.rgb * _EFT_Ambient;
            // the fogged part: the game blends to its scattering texture
            float fog = i.uvFog.z;
            float3 fogged = lerp(ambient, TarkovFogColor(i.worldPos), fog);

            UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos)
            float sunShadow = max(atten, 0.3) * (1 - fog);
            float3 lightColor = _LightColor0.rgb;

            // Unity Standard in gamma space
            float3 specColor = lerp(unity_ColorSpaceDielectricSpec.rgb, c, metallic);
            float oneMinusReflectivity = unity_ColorSpaceDielectricSpec.a * (1 - metallic);
            float3 diffColor = c * oneMinusReflectivity;
            TarkovSurface s;
            s.albedo = diffColor; s.occlusion = 1; s.specColor = specColor; s.smoothness = _Smoothness; s.normal = n; s.emission = 0;
            float3 direct = TarkovDeferredLight(s, viewDir, _WorldSpaceLightPos0.xyz, lightColor * sunShadow);
            float3 sh = TarkovLinearToGamma(ShadeSH9(float4(n, 1)));
            float3 unityProbe = TarkovUnityProbes(i.worldPos, r, pr) * sunShadow * surfaceReduction;
            float grazing2 = saturate(1 - oneMinusReflectivity + _Smoothness);
            float3 indirectSpec = unityProbe * FresnelLerp(specColor, grazing2, saturate(nv));
            float3 color = fogged + direct + diffColor * sh * sunShadow + indirectSpec;
            return float4(color, alpha);
        }

        float4 fragAdd(v2f i) : SV_Target
        {
            float4 tex = tex2D(_MainTex, i.uvFog.xy);
            float3 c = tex.rgb * _Color.rgb;
            float alpha = saturate(pow(tex.a, _DirtStrength) + _AlphaOffset);
            float metallic = tex.a * _Metallic;
            float3 n = normalize(i.normal);
            float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
            float3 lightDir = normalize(UnityWorldSpaceLightDir(i.worldPos));
            UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos)
            TarkovSurface s;
            s.albedo = c * (unity_ColorSpaceDielectricSpec.a * (1 - metallic)); s.occlusion = 1;
            s.specColor = lerp(unity_ColorSpaceDielectricSpec.rgb, c, metallic); s.smoothness = _Smoothness; s.normal = n; s.emission = 0;
            float3 color = TarkovDeferredLight(s, viewDir, lightDir, _LightColor0.rgb * atten) * (1 - i.uvFog.z);
            return float4(color, alpha);
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
            ENDCG
        }
    }
    FallBack Off
}
