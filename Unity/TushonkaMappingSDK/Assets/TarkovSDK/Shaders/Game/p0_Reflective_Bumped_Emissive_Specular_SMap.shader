// Editor stand-in for the game shader "p0/Reflective/Bumped Emissive Specular SMap": the game's properties,
// lit as the raid lights its decompiled GBuffer pass (Tools/ShaderStandins/DECOMPILE.md). Swapped for the
// game's at raid start. The lamp code fades _EmissionVisibility.
Shader "Tarkov/p0/Reflective/Bumped Emissive Specular SMap"
{
    Properties
    {
        _Color ("Main Color", Color) = (1, 1, 1, 1)
        _SpecMap ("GlossMap", 2D) = "white" {}
        _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _Glossness ("Specularness", Range(0.01, 10)) = 1
        _Specularness ("Glosness", Range(0.01, 10)) = 0.078125
        _ReflectColor ("Reflection Color", Color) = (1, 1, 1, 0.5)
        _MainTex ("Base (RGB) Specular (A)", 2D) = "white" {}
        _Cube ("Reflection Cubemap", Cube) = "" {}
        _BumpMap ("Normalmap", 2D) = "bump" {}
        _EmissionMap ("EmissionMap", 2D) = "white" {}
        _EmissiveColor ("EmissiveColor", Color) = (1, 1, 1, 1)
        _EmissionVisibility ("EmissionVisibility", Range(0, 1)) = 1
        _EmissionPower ("EmissionPower", Range(0, 10)) = 1
        _SpecVals ("Specular Vals", Vector) = (1.1, 2, 0, 0)
        _DefVals ("Defuse Vals", Vector) = (0.5, 0.7, 0, 0)
        _BumpTiling ("_BumpTiling", Float) = 1
        _Factor ("Z Offset Angle", Float) = 0
        _Units ("Z Offset Forward", Float) = 0
        _DropsSpec ("Drops spec", Float) = 128
        [Space(30)] [Header(Wetting)] _RippleTexScale ("_RippleTexScale", Float) = 4
        _RippleFakeLightIntensityOffset ("Ripple fake light offset", Float) = 0.7
        _NightRippleFakeLightOffset ("Night fake light offset", Float) = 0.2
        _NdotLOffset ("Normal dot light offset", Float) = 0.4
        [Toggle(USERAIN)] _USERAIN ("Is material affected by rain", Float) = 0
        [HideInInspector] _SkinnedMeshMaterial ("Skinned Mesh Material", Float) = 0
        _Temperature ("_Temperature(min, max, factor)", Vector) = (0.1, 1, 2, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 400

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Offset [_Factor], [_Units]
            CGPROGRAM
            #pragma vertex TarkovP0Vert
            #pragma fragment TarkovP0FragBase
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma shader_feature USERAIN
            #define P0_EMISSIVE 1
            #include "TarkovP0.cginc"
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            ZWrite Off
            Offset [_Factor], [_Units]
            CGPROGRAM
            #pragma vertex TarkovP0Vert
            #pragma fragment TarkovP0FragAdd
            #pragma target 3.0
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #define P0_EMISSIVE 1
            #include "TarkovP0.cginc"
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ShadowCaster" }
            CGPROGRAM
            #pragma vertex TarkovP0VertShadow
            #pragma fragment TarkovP0FragShadow
            #pragma target 3.0
            #pragma multi_compile_shadowcaster
            #define P0_EMISSIVE 1
            #include "TarkovP0.cginc"
            ENDCG
        }
    }
    FallBack Off
}
