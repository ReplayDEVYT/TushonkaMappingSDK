// Editor stand-in for the game shader "Custom/ShitOnScreen", written by hand (the game keeps it in
// globalgamemanagers, not in the shader bundle Tools/ShaderStandins reads). It declares the game
// material's properties so their values are kept; it draws nothing. CustomMapLoader puts the
// game's shader in its place in the raid.
Shader "Tarkov/Custom/ShitOnScreen"
{
    Properties
    {
        _MainTex ("Flare Atlas", 2D) = "white" {}
        _ShitTex ("Dirt Noise", 2D) = "white" {}
        _Color ("Color", Color) = (1, 1, 1, 1)
        _NoiseScale ("Noise Scale", Vector) = (2, 2, 1, 0)
        _NoiseIntensity ("Noise Intensity", Float) = 5
        _Intensity ("Intensity", Float) = 1
        _DepthOffset ("Depth Offset", Float) = 1
        _FadeDepthOffset ("Fade Depth Offset", Float) = 0.4
        _FallofShift ("Falloff Shift", Float) = 1
        _FallofStrength ("Falloff Strength", Float) = 16
        _VisibilityCheckerSize ("Visibility Checker Size", Float) = 0.01
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass { ColorMask 0 ZWrite Off }
    }
}
