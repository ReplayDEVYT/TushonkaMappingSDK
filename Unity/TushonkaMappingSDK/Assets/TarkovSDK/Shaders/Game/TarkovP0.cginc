// The p0 material family (decompiled GBuffer passes, see Tools/ShaderStandins/DECOMPILE.md), lit the way
// the raid lights them, in a forward pass. The shader file picks the member with one define:
//   P0_SMAP       p0/Reflective/Bumped Specular SMap
//   P0_EMISSIVE   p0/Reflective/Bumped Emissive Specular SMap
//   P0_CUTOFF     p0/Reflective/Bumped Specular SMap Transparent Cutoff
//   P0_REFLECTIVE p0/Reflective/Bumped Specular
//   P0_BUMPED     p0/Bumped Specular
//   P0_CUTOUT     p0/Cutout/Bumped Diffuse
#ifndef TARKOV_P0_INCLUDED
#define TARKOV_P0_INCLUDED

#include "TarkovGame.cginc"
#include "AutoLight.cginc"

sampler2D _MainTex;
float4 _MainTex_ST;
sampler2D _BumpMap;
float4 _BumpMap_ST;
float4 _Color;
float4 _Temperature;
#if defined(P0_SMAP) || defined(P0_EMISSIVE) || defined(P0_CUTOFF)
sampler2D _SpecMap;
float _Glossness, _Specularness, _BumpTiling;
#endif
#if defined(P0_SMAP)
float _NormalIntensity, _NormalUVMultiplier;
sampler2D _TintMask;
float4 _BaseTintColor;
float4 _HeatColor1, _HeatColor2, _HeatCenter, _HeatSize;
float _HeatTemp, _HeatVisible;
#endif
#if defined(P0_EMISSIVE)
sampler2D _EmissionMap;
float4 _EmissiveColor;
float _EmissionVisibility, _EmissionPower;
#endif
#if defined(P0_REFLECTIVE) || defined(P0_BUMPED)
float _SpecPower, _Shininess;
#endif
#if !defined(P0_BUMPED) && !defined(P0_CUTOUT)
samplerCUBE _Cube;
float4 _ReflectColor;
float4 _SpecVals;
#endif
#if defined(P0_CUTOUT) || defined(P0_CUTOFF)
float _Cutoff;
#endif
float4 _DefVals;
#if defined(P0_BUMPED)
// a 1D fresnel LUT the game never binds (not in any material or script), so it samples Unity's default
#define TARKOV_FRESNEL_LUT 0.5
#endif

struct TarkovP0Input
{
    float4 vertex : POSITION;
    float4 tangent : TANGENT;
    float3 normal : NORMAL;
    float2 texcoord : TEXCOORD0;
    float2 texcoord1 : TEXCOORD1;
};

struct TarkovP0V2F
{
    float4 pos : SV_POSITION;
    float4 uv : TEXCOORD0;      // xy _MainTex, zw _BumpMap
    float4 tbn0 : TEXCOORD1;    // xyz tangent-to-world rows, w world position
    float4 tbn1 : TEXCOORD2;
    float4 tbn2 : TEXCOORD3;
    float3 viewTS : TEXCOORD4;  // camera - position in tangent space (BSG's fresnel reads this)
    float3 objPos : TEXCOORD5;  // heat glow
    UNITY_LIGHTING_COORDS(6, 7)
};

TarkovP0V2F TarkovP0Vert(TarkovP0Input v)
{
    TarkovP0V2F o;
    UNITY_INITIALIZE_OUTPUT(TarkovP0V2F, o);
    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv.xy = TRANSFORM_TEX(v.texcoord, _MainTex);
    o.uv.zw = TRANSFORM_TEX(v.texcoord, _BumpMap);
    float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
    float3 t = normalize(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz));
    float3 n = normalize(mul(v.normal, (float3x3)unity_WorldToObject));
    float3 b = cross(n, t) * (v.tangent.w * unity_WorldTransformParams.w);
    o.tbn0 = float4(t.x, b.x, n.x, worldPos.x);
    o.tbn1 = float4(t.y, b.y, n.y, worldPos.y);
    o.tbn2 = float4(t.z, b.z, n.z, worldPos.z);
    float3 toCamera = _WorldSpaceCameraPos - worldPos;
    o.viewTS = float3(dot(toCamera, t), dot(toCamera, b), dot(toCamera, n));
    o.objPos = v.vertex.xyz;
    UNITY_TRANSFER_LIGHTING(o, v.texcoord1.xy);
    return o;
}

// the material's GBuffer pass; alpha is what a cutout tests
TarkovSurface TarkovP0Surface(TarkovP0V2F i, float3 worldPos, out float alpha)
{
    TarkovSurface s;
    float3 viewDir = normalize(_WorldSpaceCameraPos - worldPos);
    float3 viewTS = normalize(i.viewTS);

    // ---- textures
    float4 mainTex = tex2D(_MainTex, i.uv.xy);
#if defined(P0_CUTOFF)
    // this member reads its colour from _SpecMap (rgb, a = gloss mask) and its gloss from _MainTex.r
    float4 specMap = tex2D(_SpecMap, i.uv.xy);
    float3 base = specMap.rgb * _Color.rgb;
    float reflectMask = specMap.a;
    float smoothness = mainTex.r * _Specularness;
    alpha = mainTex.a;
#elif defined(P0_SMAP) || defined(P0_EMISSIVE)
    float specMap = tex2D(_SpecMap, i.uv.xy).r;
    float3 base = mainTex.rgb * _Color.rgb;
    #if defined(P0_SMAP) && defined(TINTMASK)
        float3 tintMask = tex2D(_TintMask, i.uv.xy).rgb;
        base = lerp(base, _BaseTintColor.rgb, tintMask);
    #endif
    float reflectMask = mainTex.a;
    float smoothness = specMap * _Specularness;
    alpha = 1;
#else
    float3 base = mainTex.rgb * _Color.rgb;
    float reflectMask = mainTex.a;
    #if defined(P0_CUTOUT)
        float smoothness = 0;
        alpha = mainTex.a * _Color.a;
    #else
        float smoothness = _SpecPower * _Shininess;
        alpha = 1;
    #endif
#endif

    // ---- normal (DXT5nm: x *= a, z from xy)
#if defined(P0_SMAP) || defined(P0_EMISSIVE) || defined(P0_CUTOFF)
    float4 bump = tex2D(_BumpMap, i.uv.xy * _BumpTiling);
#else
    float4 bump = tex2D(_BumpMap, i.uv.zw);
#endif
#if defined(P0_SMAP)
    bump.xyw *= _NormalUVMultiplier;
#endif
    float3 nt;
    nt.xy = float2(bump.a * bump.r, bump.g) * 2 - 1;
    nt.z = sqrt(1 - min(dot(nt.xy, nt.xy), 1));
#if defined(P0_SMAP)
    nt.xy *= _NormalIntensity;
#endif
    float3 n = normalize(float3(dot(i.tbn0.xyz, nt), dot(i.tbn1.xyz, nt), dot(i.tbn2.xyz, nt)));
    float3 nWorldUnnormalised = float3(dot(i.tbn0.xyz, nt), dot(i.tbn1.xyz, nt), dot(i.tbn2.xyz, nt));

    // ---- BSG's fresnel: tangent-space view against the (unnormalised) tangent normal
    float f = 1 - dot(viewTS, nt);
    f = f * f * 0.5;
    float difMul = _DefVals.x + _DefVals.y * f;
#if defined(P0_BUMPED) || defined(P0_CUTOUT)
    float specMul = 1;
#else
    float specMul = 0.5 * (_SpecVals.x + _SpecVals.y * f);
#endif

    // ---- reflection and specular
#if defined(P0_BUMPED) || defined(P0_CUTOUT)
    float3 refl = 0;
#else
    float3 r = reflect(-viewDir, nWorldUnnormalised);
    float3 refl = texCUBE(_Cube, r).rgb * reflectMask * _ReflectColor.rgb;
#endif
#if defined(P0_BUMPED)
    float lut = TARKOV_FRESNEL_LUT;
    float3 albedo = base * lut;
    float gloss = mainTex.a * _SpecPower * lut;
    float3 specColor = _SpecColor.rgb * gloss * UNITY_INV_PI;
#elif defined(P0_CUTOUT)
    float3 albedo = base * difMul;
    float3 specColor = 0;
#elif defined(P0_REFLECTIVE)
    float3 albedo = base * difMul;
    float gloss = mainTex.a * _SpecPower * specMul;
    float3 specColor = _SpecColor.rgb * gloss;
#elif defined(P0_CUTOFF)
    float3 albedo = base * difMul;
    float gloss = specMap.a * _Glossness * specMul;
    float3 specColor = _SpecColor.rgb * gloss;
#else
    float3 albedo = base * difMul;
    float gloss = mainTex.a * _Glossness * specMul;
    float3 specColor = _SpecColor.rgb * gloss;
#endif

#if defined(P0_SMAP) && defined(USEHEAT)
    // metal heat glow around _HeatCenter (object space); the game also folds the reflection into the albedo here
    float3 heatP = (i.objPos - _HeatCenter.xyz) / _HeatSize.xyz;
    float hot = max(0, (_HeatTemp - 30) * 0.1);
    hot *= hot;
    float k = max(0, 1 - length(heatP)) * hot;
    float3 heat = lerp(_HeatColor1.rgb, _HeatColor2.rgb, k) * k * _HeatVisibleFactor * specMul;
    float3 reflAndHeat = refl * specMul + heat;
    albedo = albedo + reflAndHeat * 10;
#endif

    // ---- thermal vision override (off in the editor)
#if defined(P0_SMAP)
    float3 thermal = max(albedo * _Temperature.z, _Temperature.x * _HeatThermalFactor);
#else
    float3 thermal = max(albedo * _Temperature.z, _Temperature.x);
#endif
    thermal = min(thermal, _Temperature.y) + _Temperature.w;
    albedo = (_ThermalVisionOn > 0) ? thermal : albedo;

    // ---- the ambient every material writes into GBuffer3: Unity's own harmonics (RenderSettings)
    float3 unityAmbient = TarkovLinearToGamma(ShadeSH9(float4(n, 1)));
#if defined(P0_CUTOUT)
    float3 emission = 0;
#elif defined(P0_EMISSIVE)
    float3 emissive = tex2D(_EmissionMap, i.uv.xy).rgb * _EmissiveColor.rgb * _EmissionPower;
    float3 emission = lerp(refl, emissive, _EmissionVisibility) + unityAmbient * albedo;
#elif defined(P0_SMAP) && defined(USEHEAT)
    float3 emission = reflAndHeat * _HeatVisible + unityAmbient * albedo;
#elif defined(P0_BUMPED)
    float3 emission = 0;
#else
    float3 emission = refl * specMul + unityAmbient * albedo;
#endif

    s.albedo = albedo;
    s.occlusion = 1;
    s.specColor = specColor;
    s.smoothness = smoothness;
    s.normal = n;
    s.emission = emission;
    return s;
}

// forward base: GBuffer3 + screen ambient + the sun, then the atmospheric fog
float4 TarkovP0FragBase(TarkovP0V2F i) : SV_Target
{
    float3 worldPos = float3(i.tbn0.w, i.tbn1.w, i.tbn2.w);
    float alpha;
    TarkovSurface s = TarkovP0Surface(i, worldPos, alpha);
#if defined(P0_CUTOUT) || defined(P0_CUTOFF)
    clip(alpha - _Cutoff);
#endif
    float3 toPixel = normalize(worldPos - _WorldSpaceCameraPos);
    UNITY_LIGHT_ATTENUATION(atten, i, worldPos)
    float3 color = s.emission + TarkovScreenAmbient(s, toPixel);
    color += TarkovDeferredLight(s, -toPixel, _WorldSpaceLightPos0.xyz, _LightColor0.rgb * atten);
    return float4(TarkovApplyFog(color, worldPos), 1);
}

// forward add: one more light, fogged towards black like the frame it is added to
float4 TarkovP0FragAdd(TarkovP0V2F i) : SV_Target
{
    float3 worldPos = float3(i.tbn0.w, i.tbn1.w, i.tbn2.w);
    float alpha;
    TarkovSurface s = TarkovP0Surface(i, worldPos, alpha);
#if defined(P0_CUTOUT) || defined(P0_CUTOFF)
    clip(alpha - _Cutoff);
#endif
    float3 viewDir = normalize(_WorldSpaceCameraPos - worldPos);
    float3 lightDir = normalize(UnityWorldSpaceLightDir(worldPos));
    UNITY_LIGHT_ATTENUATION(atten, i, worldPos)
    float3 color = TarkovDeferredLight(s, viewDir, lightDir, _LightColor0.rgb * atten);
    return float4(color * TarkovFogFade(worldPos), 1);
}

// ------------------------------------------------------------ shadow caster

struct TarkovP0ShadowV2F
{
    V2F_SHADOW_CASTER;
    float2 uv : TEXCOORD1;
};

TarkovP0ShadowV2F TarkovP0VertShadow(appdata_base v)
{
    TarkovP0ShadowV2F o;
    TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
    o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
    return o;
}

float4 TarkovP0FragShadow(TarkovP0ShadowV2F i) : SV_Target
{
#if defined(P0_CUTOUT)
    clip(tex2D(_MainTex, i.uv).a * _Color.a - _Cutoff);
#elif defined(P0_CUTOFF)
    clip(tex2D(_MainTex, i.uv).a - _Cutoff);
#endif
    SHADOW_CASTER_FRAGMENT(i)
}

#endif
