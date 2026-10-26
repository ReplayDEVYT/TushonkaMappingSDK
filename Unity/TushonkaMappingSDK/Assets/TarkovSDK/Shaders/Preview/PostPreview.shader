// Editor only: the raid's camera post-processing (Prism Effects' final pass, decompiled from
// Hidden/PrismEffects, Tools/ShaderStandins/DECOMPILE.md) over the Scene view: auto exposure to a middle
// grey, the vignette and the filmic tonemap, with the values of the map's PrismPreset or the retail camera.
// Never ships with a map.
Shader "Hidden/Tarkov SDK/Post Preview"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            ZTest Always
            Cull Off
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _TarkovLum;          // the frame with mips; the top mip is the average
            float4 _TarkovExposure;        // middle grey, lower limit, upper limit, on
            float4 _TarkovVignette;        // start, end, intensity, on
            float4 _TarkovVignetteColor;
            float4 _TarkovToneParams;      // Prism _ToneParams: toneValues.xyzz
            float4 _TarkovSecondaryTone;   // Prism _SecondaryToneParams
            float _TarkovTonemap;          // 0 off, 1 filmic
            float _TarkovGamma;            // 0 off

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_TarkovExposure.w > 0)
                {
                    float3 avg = tex2Dlod(_TarkovLum, float4(0.5, 0.5, 0, 12)).rgb;
                    float lum = max(dot(avg, float3(0.22, 0.707, 0.071)), 0.0001);
                    c.rgb *= clamp(_TarkovExposure.x / lum, _TarkovExposure.y, _TarkovExposure.z);
                }
                if (_TarkovVignette.w > 0)
                {
                    float d = length(i.uv - 0.5);
                    float t = saturate((d * _TarkovVignette.z - _TarkovVignette.x) / (_TarkovVignette.y - _TarkovVignette.x));
                    t = t * t * (3 - 2 * t);
                    c.rgb = lerp(c.rgb * _TarkovVignetteColor.rgb, c.rgb, t);
                }
                if (_TarkovTonemap > 0)
                {
                    float3 x = max(c.rgb - _TarkovSecondaryTone.x, 0);
                    float3 num = x * (x * _TarkovToneParams.x + _TarkovToneParams.y);
                    float3 den = x * (x * _TarkovToneParams.x + _TarkovToneParams.z) + _TarkovSecondaryTone.y;
                    float3 m = num / den;
                    c.rgb = m * m;
                }
                if (_TarkovGamma > 0)
                    c = pow(c, _TarkovGamma);
                return c;
            }
            ENDCG
        }
    }
    FallBack Off
}
