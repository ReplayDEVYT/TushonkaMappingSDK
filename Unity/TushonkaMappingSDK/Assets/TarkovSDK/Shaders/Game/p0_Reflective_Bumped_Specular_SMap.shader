// Editor stand-in for the game shader "p0/Reflective/Bumped Specular SMap": the properties are the
// game's, the shading is the game's decompiled GBuffer pass lit as the raid lights it
// (Tools/ShaderStandins/DECOMPILE.md). The client plugin swaps this shader for the game's at raid start.
Shader "Tarkov/p0/Reflective/Bumped Specular SMap"
{
    Properties
    {
        [MaterialEnum(Static, 0, Characters, 1, Hands, 2)] _StencilType ("_StencilType", Float) = 0
        _Color ("Main Color", Color) = (1, 1, 1, 1)
        _BaseTintColor ("Tint Color", Color) = (1, 1, 1, 1)
        _SpecMap ("GlossMap", 2D) = "white" {}
        _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _Glossness ("Specularness", Range(0.01, 10)) = 1
        _Specularness ("Glossness", Range(0.01, 10)) = 0.078125
        _ReflectColor ("Reflection Color", Color) = (1, 1, 1, 0.5)
        _MainTex ("Base (RGB) Specular (A)", 2D) = "white" {}
        [Toggle(TINTMASK)] _HasTint ("Has tint", Float) = 0
        _TintMask ("Tint mask", 2D) = "black" {}
        _Cube ("Reflection Cubemap", Cube) = "" {}
        _BumpMap ("Normalmap", 2D) = "bump" {}
        _SpecVals ("Specular Vals", Vector) = (1.1, 2, 0, 0)
        _DefVals ("Defuse Vals", Vector) = (0.5, 0.7, 0, 0)
        _BumpTiling ("_BumpTiling", Float) = 1
        _NormalIntensity ("Normal intensity", Float) = 1
        _NormalUVMultiplier ("Normal UV tiling", Float) = 1
        _Factor ("Z Offset Angle", Float) = 0
        _Units ("Z Offset Forward", Float) = 0
        _DropsSpec ("Drops spec", Float) = 128
        _Temperature ("_Temperature", Vector) = (0.1, 0.2, 0.28, 0)
        [Space(30)] [Header(Wetting)] _RippleTexScale ("_RippleTexScale", Float) = 4
        _RippleFakeLightIntensityOffset ("Ripple fake light offset", Float) = 0.7
        _NightRippleFakeLightOffset ("Night fake light offset", Float) = 0.2
        _NdotLOffset ("Normal dot light offset", Float) = 0.4
        [Toggle(USERAIN)] _USERAIN ("Is material affected by rain", Float) = 0
        [HideInInspector] _SkinnedMeshMaterial ("Skinned Mesh Material", Float) = 0
        [Toggle(USEHEAT)] USEHEAT ("Use metal heat glow", Float) = 0
        _HeatVisible ("_HeatVisible([0-1] for thermalVision only)", Float) = 1
        [HDR] _HeatColor1 ("_HeatColor1", Color) = (1, 0, 0, 1)
        [HDR] _HeatColor2 ("_HeatColor2", Color) = (1, 0.34, 0, 1)
        _HeatCenter ("_HeatCenter", Vector) = (0, 0, 0, 1)
        _HeatSize ("_HeatSize", Vector) = (0.02, 0.04, 0.02, 1)
        _HeatTemp ("_HeatTemp", Float) = 0
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
            #pragma shader_feature TINTMASK
            #pragma shader_feature USEHEAT
            #pragma shader_feature USERAIN
            #define P0_SMAP 1
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
            #pragma shader_feature TINTMASK
            #pragma shader_feature USEHEAT
            #define P0_SMAP 1
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
            #define P0_SMAP 1
            #include "TarkovP0.cginc"
            ENDCG
        }
    }
    FallBack Off
}
