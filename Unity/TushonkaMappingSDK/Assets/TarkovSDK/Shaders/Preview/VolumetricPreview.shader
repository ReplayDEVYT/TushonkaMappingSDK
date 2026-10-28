// Editor only: the game's volumetric light (VolumetricLight + VolumetricLightRenderer, "Sandbox/VolumetricLight") in
// Scene views. Marches the view ray through the light's sphere up to the scene's depth; each step adds
// scattering * attenuation * exp(-extinction so far) * the Mie phase (Henyey-Greenstein with Mie G) of the light's
// colour, as the game's shader does without its shadow map, cookie and noise. Never ships with a map.
Shader "Hidden/Tarkov SDK/Volumetric Preview"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Front
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _LightPos;          // position, range
            float4 _SpotDir;           // forward, cos of the half angle (-2 for a point light)
            float4 _LightColor;        // colour * intensity
            float4 _VolumetricLight;   // scattering, extinction, Mie G, samples

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.world = mul(unity_ObjectToWorld, vertex).xyz;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float Mie(float cosAngle, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * UNITY_PI * pow(max(1.0 + g2 - 2.0 * g * cosAngle, 1e-4), 1.5));
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 origin = _WorldSpaceCameraPos;
                float3 dir = normalize(i.world - origin);
                float range = _LightPos.w;

                // the ray's stretch inside the light's sphere
                float3 oc = origin - _LightPos.xyz;
                float b = dot(oc, dir);
                float h = b * b - (dot(oc, oc) - range * range);
                if (h <= 0.0)
                    return 0;
                h = sqrt(h);
                float t0 = max(-b - h, 0.0);
                float t1 = -b + h;

                // up to the first surface
                float depth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen)));
                float3 forward = -UNITY_MATRIX_V[2].xyz;
                t1 = min(t1, depth / max(dot(dir, forward), 1e-3));
                if (t1 <= t0)
                    return 0;

                int samples = (int)_VolumetricLight.w;
                float step = (t1 - t0) / samples;
                // the game jitters its steps with a dither texture; the same with screen noise
                float2 pixel = i.screen.xy / i.screen.w * _ScreenParams.xy;
                float jitter = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
                float extinction = 0;
                float light = 0;
                [loop]
                for (int s = 0; s < samples; s++)
                {
                    float3 p = origin + dir * (t0 + (s + jitter) * step);
                    float3 toPoint = p - _LightPos.xyz;
                    float d = length(toPoint);
                    float3 toPointDir = toPoint / max(d, 1e-4);
                    // Unity's point light falloff
                    float r2 = saturate(d * d / (range * range));
                    float atten = saturate(1.0 - r2) / (1.0 + 25.0 * r2);
                    // the default spot cookie: full in the middle, soft towards the edge
                    if (_SpotDir.w > -1.5)
                    {
                        float c = dot(toPointDir, _SpotDir.xyz);
                        atten *= smoothstep(_SpotDir.w, lerp(_SpotDir.w, 1.0, 0.35), c);
                    }
                    extinction += _VolumetricLight.y * step;
                    light += atten * _VolumetricLight.x * step * exp(-extinction) * Mie(dot(toPointDir, -dir), _VolumetricLight.z);
                }
                return float4(_LightColor.rgb * light, 0);
            }
            ENDCG
        }
    }
}
