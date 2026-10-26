// Editor stand-in for the game shader "p0/Cutout/Bumped Diffuse": the game's properties, lit as the raid
// lights its decompiled GBuffer pass (Tools/ShaderStandins/DECOMPILE.md). Swapped for the game's at raid start.
Shader "Tarkov/p0/Cutout/Bumped Diffuse"
{
    Properties
    {
        [MaterialEnum(Static, 0, Characters, 1, Hands, 2)] _StencilType ("_StencilType", Float) = 0
        _Color ("Main Color", Color) = (1, 1, 1, 1)
        _MainTex ("Base (RGB) Trans (A)", 2D) = "white" {}
        _BumpMap ("Normalmap", 2D) = "bump" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _SpecVals ("Specular Vals", Vector) = (0.35, 2, 0, 0)
        _DefVals ("Defuse Vals", Vector) = (0.1, 2.5, 0, 0)
        _Temperature ("_Temperature(min, max, factor)", Vector) = (0.1, 0.38, 0.3, 0)
        _Factor ("Z Offset Angle", Float) = 0
        _Units ("Z Offset Forward", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
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
            #define P0_CUTOUT 1
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
            #define P0_CUTOUT 1
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
            #define P0_CUTOUT 1
            #include "TarkovP0.cginc"
            ENDCG
        }
    }
    FallBack Off
}
