// Editor stand-in for the game shader "p0/Bumped Specular": the game's properties, lit as the raid lights
// its decompiled GBuffer pass (Tools/ShaderStandins/DECOMPILE.md). Swapped for the game's at raid start.
Shader "Tarkov/p0/Bumped Specular"
{
    Properties
    {
        [MaterialEnum(Static, 0, Characters, 1, Hands, 2)] _StencilType ("_StencilType", Float) = 0
        _Color ("Main Color", Color) = (1, 1, 1, 1)
        _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _SpecPower ("Specular Power", Range(0.01, 10)) = 1
        _Shininess ("Shininess", Range(0.03, 1)) = 0.078125
        _MainTex ("Base (RGB) Gloss (A)", 2D) = "white" {}
        _BumpMap ("Normalmap", 2D) = "bump" {}
        _SpecVals ("Specular Vals", Vector) = (1.1, 2, 0, 0)
        _DefVals ("Defuse Vals", Vector) = (0.5, 0.7, 0, 0)
        _Temperature ("_Temperature(min, max, factor)", Vector) = (0.1, 0.2, 0.3, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 400

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex TarkovP0Vert
            #pragma fragment TarkovP0FragBase
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #define P0_BUMPED 1
            #include "TarkovP0.cginc"
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            ZWrite Off
            CGPROGRAM
            #pragma vertex TarkovP0Vert
            #pragma fragment TarkovP0FragAdd
            #pragma target 3.0
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #define P0_BUMPED 1
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
            #define P0_BUMPED 1
            #include "TarkovP0.cginc"
            ENDCG
        }
    }
    FallBack Off
}
