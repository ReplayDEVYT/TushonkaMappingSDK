// Editor stand-in for the game shader "Nature/SpeedTreeEFT", the trees' shader on every retail map (Woods' pines:
// Branches_0 with GEOM_TYPE_BRANCH_DETAIL, Fronds_1 with GEOM_TYPE_LEAF, Fronds_2 with GEOM_TYPE_FROND, all with
// EFFECT_BUMP and EFFECT_HUE_VARIATION). It's Unity's SpeedTree 7 shader with the thermal _Temperature values added,
// so this is Unity's own SpeedTree surface shader on its SpeedTreeCommon.cginc: the same properties, keywords and
// vertex data, lit by Unity's lighting rather than the raid's. Wind is the game's global sway, decompiled (the
// whole tree bending by height; see TarkovSpeedTreeVert): its _ST_Wind* values come from EFT's TreeWind property
// block, gated by _WindQuality * _WindEnabled, and EFT blends them by the wind's strength (_ST_WindVector.w) between
// the calm set and the _A set, plus the _C / _B rates times the time. Unity's branch and leaf wind aren't ported,
// so ripped retail trees only sway as a whole in the editor. The game's shader is in globalgamemanagers.assets, so it's always
// loaded, and the client swaps it in at raid start. LOD_FADE_PERCENTAGE is always on, as in Unity's own SpeedTree shader.
Shader "Tarkov/Nature/SpeedTreeEFT"
{
    Properties
    {
        _Color ("Main Color", Color) = (1, 1, 1, 1)
        _HueVariation ("Hue Variation", Color) = (1, 0.5, 0, 0.1)
        _MainTex ("Base (RGB) Trans (A)", 2D) = "white" {}
        _DetailTex ("Detail", 2D) = "black" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.333
        [MaterialEnum(Off, 0, Front, 1, Back, 2)] _Cull ("Cull", Int) = 2
        [MaterialEnum(None, 0, Fastest, 1, Fast, 2, Better, 3, Best, 4, Palm, 5)] _WindQuality ("Wind Quality", Range(0, 5)) = 0
        _Temperature ("_Temperature(min, max, factor)", Vector) = (0, 0.1, 0.1, 0)
        _Temperature1 ("_Temperature1(min, max, factor)", Vector) = (0, 0.1, 0.1, 0)
        _Temperature2 ("_Temperature2(min, max, factor)", Vector) = (0.04, 0.17, 0.3, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Geometry" "IgnoreProjector" = "True" "RenderType" = "Opaque" "DisableBatching" = "LODFading" }
        LOD 400
        Cull [_Cull]

        CGPROGRAM
        #pragma surface surf Lambert vertex:TarkovSpeedTreeVert nodirlightmap nodynlightmap dithercrossfade addshadow
        #pragma target 3.0
        #pragma multi_compile_vertex LOD_FADE_PERCENTAGE
        #pragma instancing_options assumeuniformscaling lodfade maxcount:50
        #pragma shader_feature_local GEOM_TYPE_BRANCH GEOM_TYPE_BRANCH_DETAIL GEOM_TYPE_FROND GEOM_TYPE_LEAF GEOM_TYPE_MESH
        #pragma shader_feature_local EFFECT_BUMP
        #pragma shader_feature_local EFFECT_HUE_VARIATION
        #include "SpeedTreeCommon.cginc"

        half _WindQuality;
        half _WindEnabled;
        float4 _ST_WindVector;
        float4 _ST_WindGlobal, _ST_WindGlobal_A, _ST_WindGlobal_B, _ST_WindGlobal_C;
        float4 _ST_WindBranchAdherences, _ST_WindBranchAdherences_A;

        // SpeedTree's TrigApproximate (a smoothed triangle wave)
        float4 TarkovTrig(float4 x)
        {
            x = abs(frac(x + 0.5) * 2.0 - 1.0);
            return (x * x * (3.0 - 2.0 * x) - 0.5) * 2.0;
        }

        // Unity's vertex, then the game's global wind (Nature/SpeedTreeEFT's vertex program)
        void TarkovSpeedTreeVert(inout SpeedTreeVB IN, out Input OUT)
        {
            SpeedTreeVert(IN, OUT);
            if (_WindQuality * _WindEnabled <= 0)
                return;
            float strength = clamp(_ST_WindVector.w, 0.1, 1.0);
            float3 wind = normalize(mul(_ST_WindVector.xyz, (float3x3)unity_ObjectToWorld));
            float4 g = lerp(_ST_WindGlobal, _ST_WindGlobal_A, strength) + lerp(_ST_WindGlobal_C, _ST_WindGlobal_B, strength) * _Time.y;
            float3 p = IN.vertex.xyz;
            float len = length(p);
            // how much the height contributes (the game raises it to twice the exponent)
            float adjust = pow(max(p.y - 0.25 / g.z, 0.0) * g.z, 2.0 * g.w);
            float2 osc = TarkovTrig(float4(unity_ObjectToWorld[0].w + g.x, unity_ObjectToWorld[1].w + g.x * 0.8, 0, 0)).xy;
            float adherence = lerp(_ST_WindBranchAdherences.x, _ST_WindBranchAdherences_A.x, strength) / g.z;
            p.xz += wind.xz * ((g.y * (osc.x + osc.y * osc.y) + adherence) * adjust);
            IN.vertex.xyz = normalize(p) * len;
        }

        void surf(Input IN, inout SurfaceOutput OUT)
        {
            SpeedTreeFragOut o;
            SpeedTreeFrag(IN, o);
            SPEEDTREE_COPY_FRAG(OUT, o)
        }
        ENDCG
    }

    FallBack "Transparent/Cutout/VertexLit"
}
