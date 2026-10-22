// Editor stand-in for the game shader "Custom/multiFlare", written by hand (the game keeps it in
// globalgamemanagers, not in the shader bundle Tools/ShaderStandins reads). It declares the game
// material's properties so their values are kept; it draws nothing. CustomMapLoader puts the
// game's shader in its place in the raid.
Shader "Tarkov/Custom/multiFlare"
{
    Properties
    {
        _MainTex ("Flare Atlas", 2D) = "white" {}
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Intensity ("Intensity", Float) = 0.6
        _Size ("Size", Float) = 0.2
        _DepthOffset ("Depth Offset", Float) = 0.1
        _FadeDepthOffset ("Fade Depth Offset", Float) = 0.3
        _FallofShift ("Falloff Shift", Float) = 1
        _FallofStrength ("Falloff Strength", Float) = 18
        _VisibilityCheckerSize ("Visibility Checker Size", Float) = 0.003
        _Volume ("Volume", Float) = 1
        _VolumeStep ("Volume Step", Float) = 3
        _Val1 ("Val1", Float) = 0
        _Val2 ("Val2", Float) = -2.62
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass { ColorMask 0 ZWrite Off }
    }
}
