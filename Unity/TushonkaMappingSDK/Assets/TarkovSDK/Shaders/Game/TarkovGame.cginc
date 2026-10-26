// The game's lighting, as decompiled from its shaders (Tools/ShaderStandins/DECOMPILE.md). Everything
// here mirrors a listing; keep the formulas as they are, quirks included.
//
// In the raid an opaque surface is lit in three steps that this file lets a forward pass do at once:
//   GBuffer pass of the material        -> TarkovSurface (what the material writes)
//   Hidden/Internal-DeferredShading     -> TarkovDeferredLight (per light)
//   Hidden/WriteScreenAmbient           -> TarkovScreenAmbient (time-of-day harmonics + sky reflection)
//   Hidden/Time of Day/Scattering       -> TarkovScattering (atmospheric fog over the frame)
// The editor sets the game's globals (TarkovShaderGlobals): the SDK project is in gamma colour space like
// the game, so LinearToGamma here is what the compiled code does, not a colour-space fix.
#ifndef TARKOV_GAME_INCLUDED
#define TARKOV_GAME_INCLUDED

#include "UnityCG.cginc"
#include "UnityLightingCommon.cginc"

// ------------------------------------------------------------ globals the game sets

// AmbientLight.SetSH: the time-of-day system's harmonics, packed like Unity's unity_SHAr..
float4 _SHAr, _SHAg, _SHAb, _SHBr, _SHBg, _SHBb, _SHC;
float3 _EFT_Ambient;                 // the harmonics straight up (AmbientLight.GetColorTop)
samplerCUBE _MyGlobalReflectionProbe; // AmbientLight's cubemap of the sky
float _ReflectionIntensity;          // AmbientLight.ReflectionIntensity
float _ReflectionBottomShade;        // 2 - AmbientLight.ReflectionBottomShade
float _AmbientBlur;
float4 _Density;                     // TOD_Scattering: (HeightFalloff, cameraY - ZeroLevel, GlobalDensity, 0)
float3 _SunDirection;
float TOD_Fogginess;
float _NightVisionOn, _ThermalVisionOn;
float _HeatThermalFactor, _HeatVisibleFactor;

// TOD_Sky.UpdateShaderProperties (the sky's scattering constants)
float3 TOD_LocalSunDirection, TOD_LocalMoonDirection, TOD_SunDirection, TOD_MoonDirection;
float3 TOD_SunSkyColor, TOD_MoonSkyColor, TOD_GroundColor, TOD_MoonHaloColor, TOD_AmbientColor, TOD_SunMeshColor, TOD_MoonMeshColor;
float TOD_Contrast, TOD_Brightness, TOD_ScatteringBrightness, TOD_MoonHaloPower;
float TOD_SunMeshContrast, TOD_SunMeshBrightness, TOD_MoonMeshContrast, TOD_MoonMeshBrightness, TOD_SpaceTiling, TOD_SpaceBrightness;
float4 TOD_kSun, TOD_k4PI, TOD_kRadius, TOD_kScale;
float3 TOD_kBetaMie;
float4x4 TOD_World2Sky;

// ------------------------------------------------------------ helpers

// pow(c, 1/2.4) * 1.055 - 0.055, clamped: Unity's LinearToGammaSpace as the compiled code inlines it
inline float3 TarkovLinearToGamma(float3 c)
{
    c = max(c, 0);
    return max(1.055 * pow(c, 0.416667) - 0.055, 0);
}

// ShadeSH9 on the game's harmonics (_SHAr..), L0+L1+L2
inline float3 TarkovSH9(float3 n)
{
    float4 n4 = float4(n, 1);
    float3 x1 = float3(dot(_SHAr, n4), dot(_SHAg, n4), dot(_SHAb, n4));
    float4 vB = n.xyzz * n.yzzx;
    float3 x2 = float3(dot(_SHBr, vB), dot(_SHBg, vB), dot(_SHBb, vB));
    x2 += _SHC.rgb * (n.x * n.x - n.y * n.y);
    return x1 + x2;
}

// the harmonics evaluated straight up (what the ambient blends to in stencil shadow)
inline float3 TarkovSHTop()
{
    return float3(_SHAr.y + _SHAr.w, _SHAg.y + _SHAg.w, _SHAb.y + _SHAb.w) - _SHC.rgb;
}

// what a material's GBuffer pass writes
struct TarkovSurface
{
    float3 albedo;      // GBuffer0.rgb
    float occlusion;    // GBuffer0.a (the p0 shaders write 1)
    float3 specColor;   // GBuffer1.rgb
    float smoothness;   // GBuffer1.a
    float3 normal;      // GBuffer2, world space
    float3 emission;    // GBuffer3: per-material cube reflection + the Unity ambient (RenderSettings) + emissive
};

// ------------------------------------------------------------ Hidden/Internal-DeferredShading

// Unity's BRDF1 in gamma colour space, fed from the GBuffer. viewDir: surface -> camera, lightDir: surface -> light.
float3 TarkovDeferredLight(TarkovSurface s, float3 viewDir, float3 lightDir, float3 lightColor)
{
    float3 n = s.normal;
    float3 h = (lightDir + viewDir) * rsqrt(max(dot(lightDir + viewDir, lightDir + viewDir), 0.001));
    float nh = saturate(dot(n, h));
    float nl = saturate(dot(n, lightDir));
    float lh = saturate(dot(lightDir, h));
    float nv = abs(dot(n, viewDir));

    float pr = 1 - s.smoothness;
    float roughness = max(pr * pr, 0.002);
    float a2 = roughness * roughness;
    float d = nh * (nh * a2 - nh) + 1;
    float D = (a2 * UNITY_INV_PI) / (d * d);
    float lambdaV = nl * (nv * (1 - roughness) + roughness);
    float lambdaL = nv * (nl * (1 - roughness) + roughness);
    float vis = 0.5 / (lambdaV + lambdaL + 1e-5);
    float specTerm = sqrt(max(1e-4, vis * D * UNITY_PI)) * nl;
    specTerm *= any(s.specColor) ? 1 : 0;

    float f = 1 - lh;
    f = f * f * f * f * f;
    float3 F = s.specColor + (1 - s.specColor) * f;

    float fd90m1 = 2 * lh * lh * pr - 0.5;
    float nl5 = 1 - nl; nl5 = nl5 * nl5 * nl5 * nl5 * nl5;
    float nv5 = 1 - nv; nv5 = nv5 * nv5 * nv5 * nv5 * nv5;
    float disney = (1 + fd90m1 * nv5) * (1 + fd90m1 * nl5) * nl;

    return s.albedo * lightColor * disney + lightColor * specTerm * F;
}

// ------------------------------------------------------------ Hidden/WriteScreenAmbient

// The ambient pass over the GBuffer. toPixel: camera -> surface, normalised. SDK maps have no stencil
// shadow volumes and the motion-vector factor is 1, so those terms are fixed.
float3 TarkovScreenAmbient(TarkovSurface s, float3 toPixel)
{
    float3 n = s.normal;
    float fres = 1 - abs(dot(n, -toPixel));
    fres = fres * fres * fres * fres * fres;
    float grazing = saturate(s.smoothness + max(s.specColor.r, max(s.specColor.g, s.specColor.b)));
    float3 F = s.specColor + fres * (grazing - s.specColor);

    float pr = 1 - s.smoothness;
    float3 r = toPixel - 2 * dot(toPixel, n) * n;
    float mip = pr * (1.7 - 0.7 * pr) * 6;
    float3 probe = texCUBElod(_MyGlobalReflectionProbe, float4(r, mip)).rgb * saturate(r.y + _ReflectionBottomShade);
    probe *= s.occlusion;
    float roughness = max(pr * pr, 0.002);
    float3 refl = probe * (1 - 0.28 * roughness * pr) * F * _ReflectionIntensity;
    refl *= (_ReflectionIntensity == 1 && s.occlusion < 0.5) ? 2 * s.occlusion : 1;

    float3 amb = TarkovLinearToGamma(TarkovSH9(n)) * s.albedo;
    float lum = dot(s.albedo, float3(0.2126, 0.7152, 0.0722));
    float haze = (max(1.5 * TOD_Fogginess, _NightVisionOn) * 0.05 + 0.05) * (1 - abs(_SunDirection.y));
    return amb + refl + lum * lum * haze;
}

// ------------------------------------------------------------ Unity reflection probes

#include "UnityStandardUtils.cginc"
#include "UnityImageBasedLighting.cginc"

// UnityGI_IndirectSpecular as the forward glass and the ground GBuffer pass inline it: the two nearest
// baked probes, box projected and blended, at the roughness mip
float3 TarkovUnityProbes(float3 worldPos, float3 r, float perceptualRoughness)
{
    Unity_GlossyEnvironmentData g;
    g.roughness = perceptualRoughness;
    g.reflUVW = BoxProjectedCubemapDirection(r, worldPos, unity_SpecCube0_ProbePosition, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
    float3 env0 = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE(unity_SpecCube0), unity_SpecCube0_HDR, g);
    float blend = unity_SpecCube0_BoxMin.w;
    float3 result = env0;
    UNITY_BRANCH
    if (blend < 0.99999)
    {
        g.reflUVW = BoxProjectedCubemapDirection(r, worldPos, unity_SpecCube1_ProbePosition, unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax);
        float3 env1 = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE_SAMPLER(unity_SpecCube1, unity_SpecCube0), unity_SpecCube1_HDR, g);
        result = lerp(env1, env0, blend);
    }
    return result;
}

// ------------------------------------------------------------ Time of Day scattering

// O'Neil's scale function as the shaders inline it (0.25 applied by the callers)
inline float TarkovScatterScale(float cosAngle)
{
    float x = 1 - cosAngle;
    return exp(-0.00287 + x * (0.459 + x * (3.83 + x * (-6.8 + x * 5.25))));
}

// the in-scatter integral along the sky-space direction dir (unit length is not required; dir.y is
// smoothed through the horizon as the dome shaders do). rayleigh/mie are the two colours the phases scale.
void TarkovScatterIntegral(float3 dir, out float3 rayleigh, out float3 mie)
{
    float y = dir.y;
    float ramp = (y + 0.03125) * (y + 0.03125) * 8;
    y = (y > 0.03125) ? y : ramp;
    y = (dir.y > -0.03125) ? y : 0;
    float3 d = float3(dir.x, y, dir.z);

    float cameraHeight = TOD_kRadius.x + TOD_kScale.w;
    float3 cameraPos = float3(0, cameraHeight, 0);
    float far = sqrt(y * y * TOD_kRadius.y + TOD_kRadius.w - TOD_kRadius.y) - TOD_kRadius.x * y;
    float startOffset = exp(-TOD_kScale.w * TOD_kScale.z) * TarkovScatterScale(y);
    float sampleLength = far * 0.25;
    float scaledLength = sampleLength * TOD_kScale.x;
    float3 sampleRay = d * sampleLength;
    float3 samplePoint = cameraPos + sampleRay * 0.5;
    float3 k4pi = TOD_k4PI.xyz + TOD_k4PI.w;
    float3 front = 0;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float height = length(samplePoint);
        float invHeight = 1 / height;
        float depth = exp(TOD_kScale.z * (TOD_kRadius.x - height));
        float lightAngle = dot(TOD_LocalSunDirection, samplePoint) * invHeight;
        float cameraAngle = dot(d, samplePoint) * invHeight;
        float scatter = startOffset * 0.25 + depth * (TarkovScatterScale(lightAngle) * 0.25 - TarkovScatterScale(cameraAngle) * 0.25);
        float3 attenuate = exp(-scatter * k4pi);
        front += attenuate * (depth * scaledLength);
        samplePoint += sampleRay;
    }
    rayleigh = front * TOD_SunSkyColor * TOD_kSun.xyz;
    mie = front * TOD_SunSkyColor * TOD_kSun.w;
}

// the sky colour in a sky-space direction (unit), before the final gamma sqrt
float3 TarkovSkyColorLinear(float3 dir, float3 rayleigh, float3 mie)
{
    float cosSun = dot(TOD_LocalSunDirection, dir);
    float cos2 = cosSun * cosSun;
    float miePhase = TOD_kBetaMie.x * (1 + cos2) / pow(TOD_kBetaMie.y + TOD_kBetaMie.z * cosSun, 1.5);
    float rayleighPhase = 0.75 + 0.75 * cos2;
    float3 color = miePhase * mie + rayleighPhase * rayleigh;
    color += TOD_MoonSkyColor * (1 - dir.y);
    color += TOD_MoonHaloColor * pow(max(0, dot(dir, TOD_LocalMoonDirection)), TOD_MoonHaloPower);
    float lum = (color.r + color.g + color.b) * 0.333;
    color = lerp(color, lum, TOD_Fogginess);
    float t = saturate(-0.8 * dir.y);
    t = t * t * (3 - 2 * t);
    color = lerp(color, TOD_GroundColor, t);
    color *= TOD_ScatteringBrightness;
    return pow(color, TOD_Contrast);
}

// how much fog there is between the camera and a world position (Hidden/Time of Day/Scattering)
inline float TarkovFogAmount(float3 worldPos)
{
    float3 ray = worldPos - _WorldSpaceCameraPos;
    float heightTerm = 1 / (1 + max(0, (ray.y + _Density.y) * _Density.x));
    float dist = length(ray) * heightTerm * max(_Density.z, 0.001);
    return 1 - exp(-min(dist, 10));
}

// the fog colour the scattering pass would blend towards for a world position (dir is the view ray)
float3 TarkovFogColor(float3 worldPos)
{
    float3 dir = normalize(mul((float3x3)TOD_World2Sky, worldPos - _WorldSpaceCameraPos));
    float3 rayleigh, mie;
    TarkovScatterIntegral(dir, rayleigh, mie);
    return sqrt(TarkovSkyColorLinear(dir, rayleigh, mie));
}

// editor only: 1 on maps with the full weather (the raid runs TOD_Scattering only where a TOD_Sky exists)
float _TarkovScattering;

// Unity's own fog (RenderSettings as LevelSettings.ApplySettings sets them), which the deferred path
// applies to the lit frame before the scattering; 1 = no fog
inline float TarkovUnityFogFactor(float3 worldPos)
{
    float z = -mul(UNITY_MATRIX_V, float4(worldPos, 1)).z;
#if defined(FOG_LINEAR)
    return saturate(z * unity_FogParams.z + unity_FogParams.w);
#elif defined(FOG_EXP)
    return saturate(exp2(-unity_FogParams.y * z));
#elif defined(FOG_EXP2)
    float f = unity_FogParams.x * z;
    return saturate(exp2(-f * f));
#else
    return 1;
#endif
}

// both fog steps for an opaque surface, in the raid's order
inline float3 TarkovApplyFog(float3 color, float3 worldPos)
{
    color = lerp(unity_FogColor.rgb, color, TarkovUnityFogFactor(worldPos));
    if (_TarkovScattering > 0)
        color = lerp(color, TarkovFogColor(worldPos), TarkovFogAmount(worldPos));
    return color;
}

// how much of an additive light survives both fogs
inline float TarkovFogFade(float3 worldPos)
{
    float f = TarkovUnityFogFactor(worldPos);
    if (_TarkovScattering > 0)
        f *= 1 - TarkovFogAmount(worldPos);
    return f;
}

#endif
