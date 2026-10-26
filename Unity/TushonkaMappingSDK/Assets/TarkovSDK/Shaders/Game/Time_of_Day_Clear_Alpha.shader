// Editor stand-in for the game shader "Time of Day/Clear Alpha", decompiled (Tools/ShaderStandins/DECOMPILE.md):
// writes alpha 0 over the dome for the post stack; colour is masked off.
Shader "Tarkov/Time of Day/Clear Alpha"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Geometry+510" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite Off
            Cull Front
            ColorMask A
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                float3 viewPos = mul((float3x3)UNITY_MATRIX_V, vertex.xyz);
                float4 clip = mul(UNITY_MATRIX_P, float4(viewPos, 1));
                return float4(clip.xy, 0, clip.w);
            }

            float4 frag() : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }
    FallBack Off
}
