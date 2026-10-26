# Decompiling the game's shaders

How the editor stand-ins under `Assets/TarkovSDK/Shaders/Game/` were derived from the game's compiled
shaders, and what each one computes. Read this before changing a stand-in: the point of a stand-in is
to match the raid, so every formula below is the game's, not a design choice.

## Tooling

The game ships shaders as DXBC (Direct3D 11 bytecode) inside `StreamingAssets/Windows/shaders`
(Unity 2022 format: each shader's programs are LZ4 segments; segment 0 holds an entry table of
`(offset, length, segment)`; each entry is a subprogram = keywords + DXBC + a parameter blob naming its
constant-buffer slots and textures). UnityPy's shader export only prints the ShaderLab skeleton, so
`Tools/ShaderStandins/decompile/` has its own pipeline:

```
extract.py <bundle> <cache dir> shaders.txt      pulls the named shaders out of the bundle (slow)
dump.py <cache/x.pkl> <out dir> [--all]          summary.txt (properties, passes, states, every variant's
                                                  keywords and bindings) + one annotated listing per variant
```

Listings are Microsoft's D3D disassembly (`d3dcompiler_47.dll`, present on every Windows) with every
`cbN[k]` / `tN` / `sN` annotated with the Unity parameter it holds. Variants with only engine keywords
(stereo, instancing, fog, lightmaps, TAA/DLSS, snow, rain...) are skipped unless `--all`.

Facts about the game's rendering that every stand-in depends on:

- **Gamma colour space.** `globalgamemanagers` PlayerSettings `m_ActiveColorSpace = 0`. The compiled
  code confirms it: spherical-harmonics ambient goes through `LinearToGammaSpace`
  (`pow(x, 1/2.4) * 1.055 - 0.055`) and the specular term takes `sqrt`, which Unity only emits under
  `UNITY_COLORSPACE_GAMMA`. The SDK project must be Gamma too, or nothing matches.
- **Deferred shading.** GraphicsSettings `m_Deferred.m_Mode = 2` (custom) with BSG's
  `Hidden/Internal-DeferredShading`; every opaque shader has a GBuffer pass; ambient is a screen-space
  pass over the GBuffer. The forward passes of the p0 shaders use plain Blinn-Phong and are *not* what
  the player sees. The stand-ins therefore reproduce **GBuffer pass → deferred light → screen ambient**
  in one forward pass.
- Unity's own `unity_SHAr..` (RenderSettings ambient, `LevelSettings` Sky/Equator/Ground) only feed the
  small ambient written into GBuffer3 by each material; the real ambient is `_SHAr.._SHC` from
  `AmbientLight.SetSH` (the time-of-day system's harmonics), applied by `Hidden/WriteScreenAmbient`.

## Register conventions

All DXBC listings share these: `v0` POSITION, `v1` TANGENT, `v2` NORMAL, `v3` TEXCOORD0 (object space in);
`cb` slots are named per variant in the listing header. In the fragment programs below, `r` registers
are named by what they hold once the data flow is followed; the names are used in the sections.

### p0 family GBuffer pass (`p0/Reflective/Bumped Specular SMap` pass 2, `UNITY_HDR_ON`)

Vertex (`o` = interpolators):

| o | holds | from |
| --- | --- | --- |
| o1.xy | uv (`_MainTex_ST`) | v3 |
| o2.xyz / o3.xyz / o4.xyz | tangent-to-world rows (x: tangent, y: bitangent, z: normal), one row per interpolator | `unity_ObjectToWorld` * v1, `unity_WorldToObject`^T * v2, cross * `tangent.w * unity_WorldTransformParams.w` |
| o2.w / o3.w / o4.w | world position xyz | `unity_ObjectToWorld` * v0 |
| o5 | object normal, o6 object position | v2, v0 (heat glow and triplanar use them) |
| o7 | **tangent-space view vector** (camera - worldPos projected on T, B, N) | used by the `_SpecVals/_DefVals` fresnel |
| o9 | SH L2 part of the ambient (`unity_SHBr..SHC`) | `ShadeSH9` split: L2 in the vertex shader, L0/L1 in the fragment |

Fragment:

```
r1      = normalize(cameraPos - worldPos)                    world view vector V
r2      = normalize(o7)                                     tangent-space view vector Vt
r3      = _MainTex * _Color                                 base colour (a = MainTex.a, the reflect/gloss mask)
o1.w    = _SpecMap.r * _Specularness                        GBuffer1.a = smoothness
r4      = _BumpMap sampled at uv * _BumpTiling              normal: xy = (a*x, y)*2-1 (DXT5nm), scaled by
          ... * _NormalUVMultiplier? no: the sample's components are scaled by _NormalUVMultiplier (w),
          then xy *= _NormalIntensity (z); z = sqrt(1 - saturate(dot(xy,xy)))   tangent normal Nt
r4/r5   = Nt * TBN rows                                     world normal N (o2 GBuffer = N*0.5+0.5)
r1'     = reflect(-V, N)  (computed as N*2*dot(-V,N)*-1 - V)
refl    = _Cube(reflect) * MainTex.a * _ReflectColor         per-material cubemap reflection
f       = 0.5 * (1 - dot(Vt, Nt))^2                          BSG's fresnel term (tangent space, no normalisation of Vt here)
specMul = 0.5 * (_SpecVals.x + _SpecVals.y * f)
difMul  = _DefVals.x + _DefVals.y * f
albedo' = base.rgb * difMul                                  GBuffer0.rgb; GBuffer0.a = 1 (occlusion)
gloss   = MainTex.a * _Glossness * specMul
o1.rgb  = _SpecColor * gloss                                 GBuffer1.rgb = specular colour
thermal = (_ThermalVisionOn > 0) ? clamp(albedo' * _Temperature.z, _Temperature.x, _Temperature.y) + _Temperature.w : albedo'
o3.rgb  = refl * specMul + albedo' * LinearToGamma(max(0, ShadeSH9(N)))      GBuffer3 = "emission": reflection + Unity ambient
```

`TINTMASK`: `base.rgb = lerp(MainTex*_Color, MainTex*_BaseTintColor, _TintMask.rgb)` (per channel).
`USEHEAT`: adds `_HeatColor1→2` glow around `_HeatCenter` within `_HeatSize` scaled by
`((_HeatTemp - 30) * 0.1)^2`, times `_HeatVisibleFactor`, into albedo (`*10`) and GBuffer3 (`*_HeatVisible`).
`USERAIN`: the same code plus wetting from rain globals; dry in the editor, so the stand-in treats it as off.
`TRIPLANAR` exists but no SDK material sets it.

Other members differ only in inputs:

| shader | smoothness (GBuffer1.a) | spec colour | base / notes |
| --- | --- | --- | --- |
| `p0/Reflective/Bumped Specular` | `_SpecPower * _Shininess` | `_SpecColor * MainTex.a * _Shininess * specMul` | `_BumpMap_ST` uv; `_DXT1_NM` unused in code |
| `p0/Bumped Specular` | `_SpecPower * _Shininess` | `_SpecColor * MainTex.a * _SpecPower * fresnelLUT / π` | albedo also `* fresnelLUT`; `_FresnelTex` is a 1D LUT sampled at `dot(Vt, Nt)` that nothing in the game sets (not in any material, not in code), so it reads Unity's default texture |
| `p0/Cutout/Bumped Diffuse` | 0, spec 0 | — | `clip(MainTex.a * _Color.a - _Cutoff)`; albedo `* difMul`; GBuffer3 = 0 |
| `p0/Reflective/Bumped Emissive Specular SMap` | `_SpecMap.r * _Specularness` | `_SpecColor * MainTex.a * _Glossness * specMul` | GBuffer3 = `lerp(refl, _EmissionMap * _EmissiveColor * _EmissionPower, _EmissionVisibility)` + ambient (`_EmissiveColor` is a float3 at 240 and `_EmissionVisibility` the float after it, so they share a register; the lamp code fades `_EmissionVisibility`) |
| `... SMap Transparent Cutoff` | `_MainTex.r * _Specularness` | from `_SpecMap` rgb (the two textures swap roles: SpecMap = colour + alpha, MainTex.r = gloss) | `clip(SpecMap.a - _Cutoff)` after thermal |
| `p0/AlphaBumpedFresnel` | forward only (transparent) | none | `alpha = MainTex.a * _Color.a + _AddAlpha`, Blinn-Phong forward, albedo `* difMul` |

### Deferred light (`Hidden/Internal-DeferredShading` pass 0, `DIRECTIONAL`)

Unity's `BRDF1_Unity_PBS` in gamma space, fed from the GBuffer; BSG's copy differs from Unity's only by
reading `_LightDir`/`_LightColor` the same way. With `s` = GBuffer1.a, `spec` = GBuffer1.rgb,
`N` = GBuffer2, `L` = `-_LightDir`, `H` = normalize(L + V):

```
perceptualRoughness = 1 - s;  roughness = max(pr^2, 0.002);  a2 = roughness^2
D   = a2 / π / (NdotH^2 (a2 - 1) + 1)^2                                 GGX
Vis = 0.5 / (NdotL (NdotV (1-roughness) + roughness) + NdotV (NdotL (1-roughness) + roughness) + 1e-5)
specTerm = sqrt(max(1e-4, Vis * D * π)) * NdotL * any(spec)           (the sqrt is the gamma-space branch)
F   = spec + (1 - spec) * (1 - LdotH)^5
fd90 = 0.5 + 2 LdotH^2 pr;  disney = (1 + (fd90-1)(1-NdotL)^5) (1 + (fd90-1)(1-|NdotV|)^5)
out = albedo * lightColor * disney * NdotL + lightColor * specTerm * F
```

`POINT`/`SPOT` variants multiply `lightColor` by the usual Unity attenuation textures. The LDR variant
stores `exp2(-out)`; the HDR one stores `out`.

### Screen ambient (`Hidden/WriteScreenAmbient`, `UNITY_HDR_ON`)

Per pixel, from depth + GBuffer + the time-of-day harmonics:

```
V      = normalize(frustum ray * linearEyeDepth)      (camera -> pixel)
N      = GBuffer2 * 2 - 1
fres   = (1 - |dot(N, -V)|)^5
spec   = GBuffer1.rgb;  s = GBuffer1.a
grazing = saturate(s + max(spec.r, spec.g, spec.b))                 Unity's grazingTerm
F      = spec + fres * (grazing - spec)                              FresnelLerp
R      = reflect(V, N);  mip = pr (1.7 - 0.7 pr) * 6                 Unity_GlossyEnvironment
probe  = _MyGlobalReflectionProbe.SampleLevel(R, mip) * saturate(R.y + _ReflectionBottomShade)
refl   = probe * GBuffer0.a * (1 - 0.28 pr^3) * F * _ReflectionIntensity
shN    = LinearToGamma(max(0, SH9(N) using _SHAr.._SHC))
shTop  = LinearToGamma(max(0, (_SHAr.y + _SHAr.w, same g, b) - _SHC))      = SH evaluated straight up
k      = 1 - (1 - stencilShadow)^_AmbientBlur
amb    = (shN + k (shTop - shN)) * GBuffer0.rgb
out    = amb * (1 - stencilShadow) + refl * (1 - stencilShadow) * motionFactor
       + luminance(GBuffer0)^2 * (max(1.5 TOD_Fogginess, _NightVisionOn) * 0.05 + 0.05) * (1 - |_SunDirection.y|)
```

`stencilShadow` (`_StencilShadow`, BSG's placed shadow volumes) and `motionFactor`
(`_CameraMotionVectorsDepth`) are 0 / 1 on SDK maps. `_SHAr..` are packed exactly like Unity's
`unity_SHAr..` (`AmbientLight.SetSH`), so the editor feeds the same harmonics to both.
`Hidden/AmbientHighlihgt` adds the same `amb` term again, scaled by a sun-height curve, but retail only
enables it for stencil type Hands (the player's arms), so maps don't get it.

### Glass (`Global Fog/Transparent Reflective Specular`, forward, `HDR GAMMA DIRECTIONAL`)

A Unity Standard (specular setup) forward pass with BSG additions:

```
alpha   = saturate(MainTex.a^_Smoothness.y + _Smoothness.z)         (_Smoothness is a 4-vector in the cbuffer: x smoothness, y/z/w = _Metallic/_AlphaOffset/_DirtStrength packed after it)
dirt    = MainTex.a * _DirtStrength
spec colour = lerp(0.2209, base, metallic)  (Unity's unity_ColorSpaceDielectricSpec.rgb in gamma)
refl    = _MyGlobalReflectionProbe(R, mip) * saturate(R.y + _ReflectionBottomShade) * surfaceReduction * FresnelLerp * _GlobalReflectionStrength
ambient = _Color * _EFT_Ambient + refl * _GlobalReflectionStrength    (then faded by the stencil shadow)
sun     = cascaded shadow (`_SunCascadedShadowMap`, 4 split spheres) with floor 0.3, scaled by vertex fog factor
o1.z (vertex) = fog factor from `_Density` (see Scattering): the fragment blends `ambient` to `_ScatteringTex` by it
+ Unity BRDF1 for the sun and the reflection probes (`unity_SpecCube0/1` box projection) as in Standard
```

### Vertex-painted ground (`Custom/Vert Paint Shader Solid`)

Pass 0 is the GBuffer pass (4 outputs; `USE_MERGED_TEXTURES` packs the three layers), pass 1 is a
placeholder (`SceneSelection`, constant colour). Three layers blended by vertex colour (`o7 = COLOR`) and
`_Heights`, each with its own `_Color/_MainTex/_BumpMap/_Smoothness/_Specular/_FresnelDiffuse/_FresnelSpecular`.

### Lit particles (`Global Fog/Particle Alpha Blended Lighted`, vertex lit)

Vertex: colour = `2 * glstate_lightmodel_ambient + Σ 4 vertex lights (1 / (1 + d^2 * atten))`, times
`_TintColor * vertex colour`; the fog colour for the vertex is the full atmospheric scattering
(`TOD_*` constants, the same integral as the sky) and the fog amount from `_Density`
(`o3.w`). Fragment: `tex * colour * (1 + _EFT_Ambient)` lerped to the fog colour by `o3.w`.

### Sky

All sky shaders are Sean O'Neil atmospheric scattering with the "Time of Day" asset's constants, set as
globals by `TOD_Sky.UpdateShaderProperties` (`TOD_kSun`, `TOD_k4PI`, `TOD_kRadius`, `TOD_kScale`,
`TOD_kBetaMie`, `TOD_SunSkyColor`, `TOD_MoonSkyColor`, `TOD_GroundColor`, `TOD_AmbientColor`,
`TOD_MoonHaloColor`, `TOD_LocalSun/MoonDirection`, `TOD_Contrast/Brightness/ScatteringBrightness`,
`TOD_SpaceTiling/Brightness`, `TOD_Sun/MoonMeshContrast/Brightness`, `TOD_World2Sky`). The dome is
`TOD_Sky`'s child mesh, rendered with the camera's position zeroed (Atmosphere, Space, Clear Alpha multiply
view by the rotation part only).

- **Atmosphere** (`PER_PIXEL`): vertex computes, for the dome direction `dir` (y clamped by the
  `0.03125` horizon smoothing), the 4-sample in-scatter integral: `o2 = Rayleigh colour`, `o3 = Mie colour`
  (both `* TOD_SunSkyColor * TOD_kSun`), `o4 = dir`. Fragment: `rayleighPhase = 0.75 (1 + cos^2)`,
  `miePhase = kBetaMie.x (1 + cos^2) / (kBetaMie.y + kBetaMie.z cos)^1.5`, plus night sky
  `lerp(TOD_MoonSkyColor, 0, dir.y)`, moon halo `TOD_MoonHaloColor * max(0, dot(dir, moonDir))^MoonHaloPower`,
  desaturation towards the mean by `TOD_Fogginess`, blend to `TOD_GroundColor` below the horizon
  (`smoothstep` on `-0.8 dir.y`), `* TOD_ScatteringBrightness`, `pow(TOD_Contrast)`, `sqrt` (gamma), `min 4`
  (HDR; the LDR variant tonemaps with `1 - 2^(-Brightness * c)` instead). The scalar globals share one
  cbuffer register: `TOD_Contrast` 444, `TOD_Brightness` 448, `TOD_ScatteringBrightness` 452,
  `TOD_Fogginess` 456, `TOD_MoonHaloPower` 460.
  Additive blend over Space; Cull Front.
- **Skybox** (`Time of Day/Skybox`, the material `LevelSettings.skybox` points at on retail maps): the same
  integral per vertex; below the horizon `pow(TOD_AmbientColor, Contrast)`; fragment `sqrt`.
- **Sun**: billboard quad at the sun; `pow(1 - smoothstep(0.5 disc), SunMeshContrast) * saturate(25 * height) * SunMeshBrightness * TOD_SunMeshColor`.
- **Moon**: `_MainTex * max(0, dot(N, TOD_SunDirection))^MoonMeshContrast * saturate((2 height)^2) * MoonMeshBrightness`.
- **Space**: star texture tiled by `TOD_SpaceTiling`, brightness `TOD_SpaceBrightness * |dir.y|`, drawn where stencil == 0.
- **Clear Alpha**: writes alpha 0 over the dome (ColorMask A), for the post stack.

### Atmospheric fog (`Hidden/Time of Day/Scattering`, image effect)

Applied after opaque rendering over the whole frame. `_Density = (HeightFalloff, cameraY - ZeroLevel,
GlobalDensity)` from `TOD_Scattering` (`LevelSettings.HeightFalloff/ZeroLevel`; density from the
weather, `WeatherController.method_4`: `WeatherCurve.Fog * FogMultyplyer(sunHeight)`, the client's
`WeatherFogDensityPatch` puts `LevelSettings.fogDensity` there on SDK maps):

```
ray    = frustum corner * linearEyeDepth  (world offset camera -> pixel)
height = 1 / (1 + max(0, (ray.y + _Density.y) * _Density.x))            height falloff
amount = 1 - exp2(-min(10, |ray| * height * max(_Density.z, 0.001)))     (sky pixels: 0)
colour = the same scattering integral as the sky along normalize(ray), with `_SunrizeGlow` applied to the
         direction, `+ dither * 0.0154`, `sqrt`
out    = lerp(scene, colour, amount)
```

The vertex fog factor in the glass and particle shaders is the same formula evaluated per vertex.

### Camera post-processing (`Hidden/PrismEffects` pass 0, `PRISM_FILMIC_TONEMAP PRISM_USE_EXPOSURE`)

The raid camera's Prism Effects final pass, in this order (each step behind its own switch):

```
c = source
chromatic aberration (off on retail)
exposure: c *= clamp(_ExposureMiddleGrey / brightness, _ExposureLowerLimit, _ExposureUpperLimit)
          brightness = the adapted average luminance (_BrightnessTexture); retail 0.12 / -10 / 0.9, so the
          frame is only ever darkened, down to middle grey
ambient obscurance, Prism's fog (off on retail)
vignette: d = |uv - 0.5|; t = smoothstep(saturate((d * _VignetteIntensity - _VignetteStart) / (_VignetteEnd - _VignetteStart)))
          c = lerp(c * _VignetteColor, c, t)                       retail 0.9 / 0.4 / 0.887, black
filmic:   x = max(c - _SecondaryToneParams.x, 0)                    _ToneParams = toneValues.xyzz = (7, 0.3, 0.3)
          c = (x (A x + B) / (x (A x + C) + _SecondaryToneParams.y))^2      secondary = (-0.05, 0.25, 0)
gamma (pow, off), god rays, grain (off)
```

`Hidden/Tarkov SDK/Post Preview` runs the exposure, vignette and tonemap steps over the Scene view with the
frame's mean luminance for `brightness`; `TarkovPostPresets` writes the retail values into a PrismPreset.

## What the editor must supply

Globals the game sets at runtime, provided by `TarkovShaderGlobals` (editor) each Scene view frame:
`_SHAr.._SHC`, `_EFT_Ambient` (= SH straight up), `_MyGlobalReflectionProbe` (cubemap of the current
sky), `_ReflectionIntensity`, `_ReflectionBottomShade` (= `2 - AmbientLight.ReflectionBottomShade`),
`_AmbientBlur`, `_Density`, `_SunDirection`, `TOD_LightDirection`, `TOD_Fogginess`, `_DirectionLightShadow`,
`_WaterLevel`, `_ThermalVisionOn` (0), `_NightVisionOn` (0), `_StencilShadow` (black),
`_ScatteringTex` (black), and the `TOD_*` sky constants from the scene's TOD_Sky (or the simple rig's sun).
Unity's own `unity_SHAr..` come from `RenderSettings` ambient exactly as `LevelSettings.ApplySettings` sets
it in the raid.
