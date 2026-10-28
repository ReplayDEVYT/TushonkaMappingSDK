// Editor stand-in for the game shader "p0/Bumped Specular Wires Wind" (globalgamemanagers.assets, always loaded):
// p0/Bumped Specular with the game's wire sway in the vertex program (TarkovP0.cginc TarkovWireWind; the
// WindController's WiresController sets its _Wire* globals from the weather's wind, the editor's wind preview sets
// them in the Scene view). The mesh's TEXCOORD1 holds each vertex's sway weight (x) and speed (y). Its shadow pass is
// Unity's, so shadows don't sway, as in the game. Swapped for the game's at raid start.
Shader "Tarkov/p0/Bumped Specular Wires Wind"
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
        _Freq ("_Freq", Float) = 0
        _DetailWindFreq ("_DetailWindFreq", Float) = 0
        _DetailWindScale ("_DetailWindScale", Float) = 0
        _DetailWindAmp ("_DetailWindAmp", Float) = 0
        _Temperature ("_Temperature(min, max, factor)", Vector) = (0.1, 0.2, 0.4, 0)
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
            #define P0_WIRES_WIND 1
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
            #define P0_WIRES_WIND 1
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
