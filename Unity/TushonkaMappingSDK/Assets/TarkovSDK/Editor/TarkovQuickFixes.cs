using System.Collections.Generic;
using System.Linq;
using EFT.Ballistics;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Tools > Interactables > Fix Broken Interactables: repairs that make older or hand-made objects work in the raid.
    //
    // Fix Broken Interactables checks every door, switch, lootable container and lamp in the open scenes
    // and fixes what the game would trip over:
    //  - switches with an empty CustomProgressCurve (the game reads its keys unchecked: interacting throws,
    //    and the switch neither sounds nor reaches its door or lamps) or a door's empty KickCurve
    //  - interactive objects off the Interactive layer, which the player's interaction ray never finds
    //  - empty or duplicate ids (as Build Map assigns them)
    //  - missing and deleted entries in the lists the game walks (switch lamps, lamp lights, on/off objects)
    //  - distance-culling components that lost their light
    // What it can't decide (a container without a type, an object without a collider) is reported.
    public static class TarkovQuickFixes
    {
        private const string Menu = "Tarkov SDK/Quick Fixes/";

        [MenuItem(Menu + "Fix Broken Interactables", priority = 200)]
        public static void FixInteractables()
        {
            var fixes = new List<string>();
            var problems = new List<string>();
            int layer = LayerMask.NameToLayer("Interactive");
            var ids = new HashSet<string>();

            foreach (var wio in All<WorldInteractiveObject>())
            {
                string where = Where(wio);
                if (wio.gameObject.layer != layer)
                {
                    Record(wio.gameObject);
                    wio.gameObject.layer = layer;
                    fixes.Add($"{where}: moved to the Interactive layer");
                }
                if (string.IsNullOrEmpty(wio.Id) || !ids.Add(wio.Id))
                {
                    Record(wio);
                    wio.Id = $"{wio.gameObject.scene.name}_{wio.gameObject.name}_{System.Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    ids.Add(wio.Id);
                    fixes.Add($"{where}: new id");
                }
                if (wio.GetComponent<Collider>() == null)
                    problems.Add($"{where}: no collider on its object, so it can't be interacted with");

                switch (wio)
                {
                    case Switch sw:
                        if (sw.CustomProgressCurve == null || sw.CustomProgressCurve.length == 0)
                        {
                            Record(sw);
                            sw.CustomProgressCurve = TarkovPrefabTools.SwitchCurve();
                            fixes.Add($"{where}: switch animation curve was empty (interacting threw)");
                        }
                        if (Clean(sw, ref sw.Lamps))
                            fixes.Add($"{where}: removed deleted lamps from its list");
                        if (sw.NextSwitches == null || sw.ConditionStatus == null)
                        {
                            Record(sw);
                            sw.NextSwitches ??= new Switch.SwitchAndOperation[0];
                            sw.ConditionStatus ??= new EExfiltrationStatus[0];
                            fixes.Add($"{where}: empty lists filled in");
                        }
                        if (sw.Door == null && (sw.Lamps == null || sw.Lamps.Length == 0) && sw.ExfiltrationPoint == null
                            && (sw.TriggersMap == null || sw.TriggersMap.Length == 0) && (sw.NextSwitches == null || sw.NextSwitches.Length == 0))
                            problems.Add($"{where}: switch operates nothing (set its Door or Lamps, or connect it in Triggers & Sounds)");
                        break;
                    case Door door:
                        if (door.KickCurve == null || door.KickCurve.length == 0)
                        {
                            Record(door);
                            door.KickCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
                            fixes.Add($"{where}: door kick curve was empty");
                        }
                        break;
                    case LootableContainer container:
                        if (string.IsNullOrEmpty(container.Template))
                            problems.Add($"{where}: container has no Template (container type), so it can't be opened");
                        if (container.FilterExtended == null || container.GameObjectsToDestroy == null)
                        {
                            Record(container);
                            container.FilterExtended ??= new List<LootableContainerParameters>();
                            container.GameObjectsToDestroy ??= new List<GameObject>();
                            fixes.Add($"{where}: empty lists filled in");
                        }
                        Record(container);
                        if (TarkovPrefabTools.KeepContainerInPlace(container))
                            fixes.Add($"{where}: closed/open positions set to where it stands (opening it moved it to its parent's origin)");
                        if (container.GetComponent<Collider>() is BoxCollider box)
                        {
                            Record(box);
                            if (TarkovPrefabTools.EncloseHighPoly(box))
                                fixes.Add($"{where}: interaction box grown around its HighPoly colliders (the interact prompt flickered)");
                        }
                        break;
                }
            }

            foreach (var lamp in All<LampController>())
            {
                string where = Where(lamp);
                bool cleaned = Clean(lamp, ref lamp.Lights) | Clean(lamp, ref lamp.AreaAndTubeLights) | Clean(lamp, ref lamp.CustomLights)
                               | Clean(lamp, ref lamp.MultiFlareLights) | Clean(lamp, ref lamp.OnObjects) | Clean(lamp, ref lamp.OffObjects)
                               | Clean(lamp, ref lamp.DestroyedObjects) | Clean(lamp, ref lamp.TurnOnClips);
                if (lamp._materialsWithEmission == null || lamp._legacyMaterialsWithEmission == null)
                {
                    Record(lamp);
                    lamp._materialsWithEmission ??= new MaterialEmission[0];
                    lamp._legacyMaterialsWithEmission ??= new MaterialColor[0];
                    cleaned = true;
                }
                if (cleaned)
                    fixes.Add($"{where}: removed deleted entries from its lists");
                if (string.IsNullOrEmpty(lamp.Id) || !ids.Add(lamp.Id))
                {
                    Record(lamp);
                    lamp.Id = $"lamp_{lamp.gameObject.scene.name}_{lamp.gameObject.name}_{System.Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    ids.Add(lamp.Id);
                    fixes.Add($"{where}: new id");
                }
                if (lamp.BallisticCollider == null)
                {
                    var ballistic = lamp.GetComponentInChildren<BallisticCollider>(true);
                    if (ballistic != null)
                    {
                        Record(lamp);
                        lamp.BallisticCollider = ballistic;
                        fixes.Add($"{where}: linked its bulb collider");
                    }
                    else
                    {
                        problems.Add($"{where}: lamp has no BallisticCollider, so it can't be shot out");
                    }
                }
                if ((lamp.Lights?.Length ?? 0) + (lamp.AreaAndTubeLights?.Length ?? 0) + (lamp.CustomLights?.Length ?? 0) + (lamp.MultiFlareLights?.Length ?? 0) == 0
                    && (lamp.OnObjects?.Length ?? 0) == 0)
                    problems.Add($"{where}: lamp has no lights or on-objects to switch");
            }

            foreach (var window in All<WindowBreaker>())
            {
                string where = Where(window);
                Record(window);
                if (!TarkovWindows.Setup(window, out var problem))
                    problems.Add($"{where}: breakable window can't break: {problem}");
                if (window.gameObject.layer != LayerMask.NameToLayer("TransparentCollider"))
                {
                    Record(window.gameObject);
                    window.gameObject.layer = LayerMask.NameToLayer("TransparentCollider");
                    fixes.Add($"{where}: window moved to the TransparentCollider layer");
                }
                if (string.IsNullOrEmpty(window.Id) || !ids.Add(window.Id))
                {
                    window.Id = $"window_{window.gameObject.scene.name}_{window.gameObject.name}_{System.Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    ids.Add(window.Id);
                    fixes.Add($"{where}: new id");
                }
            }

            foreach (var prop in All<SmallPhysicsObject>())
            {
                // all three at 1: the SDK's first default, which ignored mass (a can flew, a monitor didn't move)
                if (prop.OnHitForceMultiplier == 1f && prop.OnTouchForceMultiplier == 1f && prop.OnBlowUpForceMultiplier == 1f)
                {
                    Record(prop);
                    TarkovPhysicsProps.SetPushSpeeds(prop, TarkovPhysicsProps.ShotSpeed, TarkovPhysicsProps.TouchSpeed, TarkovPhysicsProps.ExplosionSpeed);
                    fixes.Add($"{Where(prop)}: physics prop push strength set from its mass");
                }
                if (prop.GetComponentsInChildren<Rigidbody>(true).Length == 0)
                    problems.Add($"{Where(prop)}: physics prop has no Rigidbody, so it can't move");
                if (prop.GetComponentsInChildren<BallisticCollider>(true).Length == 0)
                    problems.Add($"{Where(prop)}: physics prop has no BallisticCollider, so bullets don't move it");
            }

            // game triggers: automatic doors, elevators, PA announcements, sound zones
            foreach (var press in All<EFT.GameTriggers.HandlerInteractable>())
            {
                if (press._interactable == null)
                    problems.Add($"{Where(press)}: zone presses nothing (set its Interactable)");
                else if (press._interactableId != press._interactable.Id && !string.IsNullOrEmpty(press._interactable.Id))
                {
                    Record(press);
                    press._interactableId = press._interactable.Id;
                    fixes.Add($"{Where(press)}: zone's interactable id relinked");
                }
            }
            foreach (var sync in All<ISyncAble>())
                if (sync._random == 0)
                {
                    Record(sync);
                    sync._random = System.BitConverter.ToUInt32(System.Guid.NewGuid().ToByteArray(), 0) | 1u;
                    fixes.Add($"{Where(sync)}: new sync id");
                }
            foreach (var sound in All<EFT.GameTriggers.HandlerPlaySoundAdvanced>())
                if (sound._soundsPool != null && sound._soundsPool.Any(c => Mathf.Approximately(c.Pitch, 0f)))
                {
                    Record(sound);
                    for (int i = 0; i < sound._soundsPool.Length; i++)
                        if (Mathf.Approximately(sound._soundsPool[i].Pitch, 0f))
                            sound._soundsPool[i].Pitch = 1f;
                    fixes.Add($"{Where(sound)}: sound pitch 0 (silent in the game) set to 1");
                }
            // PA speakers on the game's indoor or outdoor ambience groups go quiet on the other side
            foreach (var pa in All<QueuePlayer>())
                foreach (var speaker in pa.GetComponentsInChildren<AudioSource>(true))
                {
                    int tag = speaker.name.LastIndexOf('@');
                    if (tag < 0 || !TarkovFacilitySystems.AmbientOnlyGroup(speaker.name.Substring(tag + 1).Trim()))
                        continue;
                    Undo.RecordObject(speaker.gameObject, "Fix speaker mixer");
                    speaker.name = speaker.name.Substring(0, tag).TrimEnd() + " " + TarkovFacilitySystems.SpeakerMixerTag;
                    fixes.Add($"{Where(speaker)}: PA speaker moved to the CommonSounds mixer group (its indoor/outdoor ambience group went quiet on the other side)");
                }

            foreach (var (where, problem) in TarkovTriggerLinks.Problems())
                if (where != null)
                    problems.Add($"{Where(where)}: {problem}");
            foreach (var subscriber in All<InteractiveSubscriber>())
                if (subscriber.Subscribee == null)
                    problems.Add($"{Where(subscriber)}: announcement follows nothing (set its Subscribee); the game throws on it");
            foreach (var zone in All<EFT.GameTriggers.TriggerZone>())
            {
                var collider = zone.GetComponent<Collider>();
                if (collider == null)
                    problems.Add($"{Where(zone)}: trigger zone has no collider");
                else if (!collider.isTrigger)
                {
                    Record(collider);
                    collider.isTrigger = true;
                    fixes.Add($"{Where(zone)}: trigger zone's collider made a trigger");
                }
                if (zone.gameObject.layer != LayerMask.NameToLayer("Triggers"))
                {
                    Record(zone.gameObject);
                    zone.gameObject.layer = LayerMask.NameToLayer("Triggers");
                    fixes.Add($"{Where(zone)}: trigger zone moved to the Triggers layer");
                }
            }

            foreach (var culling in All<CullingLightObject>())
                if (culling._light == null && culling.TryGetComponent<Light>(out var light))
                {
                    Record(culling);
                    culling._light = light;
                    fixes.Add($"{Where(culling)}: light culling relinked to its light");
                }
            foreach (var culling in All<CullingAdvancedLightObject>())
                if (culling._light == null && culling.TryGetComponent<BaseLight>(out var light))
                {
                    Record(culling);
                    culling._light = light;
                    fixes.Add($"{Where(culling)}: area light culling relinked to its light");
                }

            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (fixes.Count > 0)
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(i));
            foreach (var f in fixes)
                Debug.Log("[Tarkov SDK] Fixed " + f);
            foreach (var p in problems)
                Debug.LogWarning("[Tarkov SDK] Needs a fix by hand: " + p);
            EditorUtility.DisplayDialog("Fix Broken Interactables",
                (fixes.Count == 0 ? "Nothing needed fixing." : $"Fixed {fixes.Count} problem(s).") +
                (problems.Count == 0 ? "" : $"\n\n{problems.Count} problem(s) need a fix by hand:\n" + string.Join("\n", problems.Take(8)) +
                                            (problems.Count > 8 ? $"\n... and {problems.Count - 8} more" : "")) +
                (fixes.Count + problems.Count > 0 ? "\n\nDetails are in the Console." : ""), "OK");
        }

        // Fix Night Lighting: Unity's own ambient (LevelSettings' Ambient, copied into RenderSettings at raid
        // start) is added to every p0 surface in its GBuffer pass (albedo x ShadeSH9, converted to gamma, so
        // 0.05 comes out near 0.25) and to the player's weapon, day and night, with no shadow. Retail full
        // weather maps (Customs) keep it Flat, black, intensity 0 and get all ambient from AmbientLight's
        // time-of-day harmonics; Skybox mode bakes a fixed sky into the scene at build. Anything else makes
        // specular maps glow at night.
        public const float SimpleRigAmbientLimit = 0.03f; // Factory: 0.03 at 0.6 = 0.018

        [MenuItem(Menu + "Fix Night Lighting (Bright or Shiny at Night)", priority = 201)]
        public static void FixNightLighting()
        {
            var fixes = new List<string>();
            var problems = new List<string>();
            var active = SceneManager.GetActiveScene();
            LevelSettings activeLevel = null;
            foreach (var level in All<LevelSettings>())
            {
                string where = Where(level);
                bool fullWeather = TarkovWeatherBuilder.Find(level.gameObject.scene) != null;
                if (level.gameObject.scene == active && activeLevel == null)
                    activeLevel = level;
                if (level.AmbientMode != UnityEngine.Rendering.AmbientMode.Flat)
                {
                    RecordLight(level);
                    fixes.Add($"{where}: ambient mode {level.AmbientMode} set to Flat (Skybox bakes a fixed sky into the map; Trilight adds its colours day and night)");
                    level.AmbientMode = UnityEngine.Rendering.AmbientMode.Flat;
                }
                float ambient = level.SkyColor.maxColorComponent * level.AmbientIntensity;
                if (fullWeather && (ambient > 0f || level.SkyColor.maxColorComponent > 0f))
                {
                    // Customs' values
                    RecordLight(level);
                    level.SkyColor = Color.black;
                    level.EquatorColor = Color.black;
                    level.GroundColor = Color.black;
                    level.AmbientIntensity = 0f;
                    fixes.Add($"{where}: ambient {ambient:0.###} set to black, as retail full weather maps (the weather's harmonics light the map)");
                }
                else if (!fullWeather && ambient > SimpleRigAmbientLimit)
                {
                    RecordLight(level);
                    level.SkyColor = new Color(0.03f, 0.03f, 0.03f);
                    level.AmbientIntensity = 0.6f;
                    fixes.Add($"{where}: ambient {ambient:0.###} lowered to Factory's (Sky Color 0.03 at intensity 0.6)");
                }
                if (fullWeather && level.DirectionLightShadowForUnshadowedShaders > 0f)
                {
                    // Customs' value; Factory's 0.832 lights shadowed surfaces and looks bright at night
                    RecordLight(level);
                    level.DirectionLightShadowForUnshadowedShaders = 0f;
                    fixes.Add($"{where}: Direction Light Shadow For Unshadowed Shaders set to 0, as Customs");
                }
            }
            if (activeLevel == null)
                problems.Add($"{active.name}: no LevelSettings in the active scene; the raid keeps the scene's own ambient");

            // the active scene's RenderSettings are what the map's bundle carries (Skybox mode stores the sky's
            // ambient in it); match them to LevelSettings as the raid does
            if (activeLevel != null && (RenderSettings.ambientMode != activeLevel.AmbientMode || RenderSettings.ambientLight != activeLevel.SkyColor
                                        || !Mathf.Approximately(RenderSettings.ambientIntensity, activeLevel.AmbientIntensity)))
            {
                RenderSettings.ambientMode = activeLevel.AmbientMode;
                RenderSettings.ambientSkyColor = activeLevel.SkyColor;
                RenderSettings.ambientEquatorColor = activeLevel.EquatorColor;
                RenderSettings.ambientGroundColor = activeLevel.GroundColor;
                RenderSettings.ambientLight = activeLevel.SkyColor;
                RenderSettings.ambientIntensity = activeLevel.AmbientIntensity;
                EditorSceneManager.MarkSceneDirty(active);
                fixes.Add($"{active.name}: scene ambient (Lighting window) matched to LevelSettings");
            }
            if (RenderSettings.defaultReflectionMode != UnityEngine.Rendering.DefaultReflectionMode.Custom || RenderSettings.customReflectionTexture != null
                                                                                                         || RenderSettings.reflectionIntensity != 0f)
            {
                TarkovLightingPreview.ApplyRetailReflection();
                fixes.Add($"{active.name}: default reflection removed (it reflected a daytime sky on every surface)");
            }

            // baked light data is captured once; dynamic objects (the weapon too) take their ambient from light probes
            foreach (var probe in All<ReflectionProbe>())
                if (probe.mode != UnityEngine.Rendering.ReflectionProbeMode.Realtime && probe.enabled)
                    problems.Add($"{Where(probe)}: baked reflection probe; it reflects the sky it was baked with, day and night (disable or delete it)");
            foreach (var group in All<LightProbeGroup>())
                if (group.enabled)
                    problems.Add($"{Where(group)}: light probe group; baked probes give objects near them fixed ambient light, day and night");
            if (Lightmapping.lightingDataAsset != null)
                problems.Add($"{active.name}: has baked lighting data; lightmaps and probes don't follow the time of day");

            foreach (var f in fixes)
                Debug.Log("[Tarkov SDK] Fixed " + f);
            foreach (var p in problems)
                Debug.LogWarning("[Tarkov SDK] Needs a fix by hand: " + p);
            EditorUtility.DisplayDialog("Fix Night Lighting",
                (fixes.Count == 0 ? "The map's ambient already matches retail maps." : $"Fixed {fixes.Count} problem(s).") +
                (problems.Count == 0 ? "" : $"\n\n{problems.Count} problem(s) need a fix by hand:\n" + string.Join("\n", problems.Take(8)) +
                                            (problems.Count > 8 ? $"\n... and {problems.Count - 8} more" : "")) +
                (fixes.Count + problems.Count > 0 ? "\n\nDetails are in the Console. Build the map again for the raid." : ""), "OK");
        }

        // Fix Shiny Materials: p0 materials without a reflection cubemap (TarkovReflectionCube); selected
        // materials, or with none selected every material on the open scenes' renderers
        [MenuItem(Menu + "Fix Shiny Materials (Missing Reflection Cubemaps)", priority = 202)]
        public static void FixReflectionCubemaps()
        {
            var selected = Selection.GetFiltered<Material>(SelectionMode.Assets);
            var materials = (selected.Length > 0 ? selected : All<Renderer>().SelectMany(r => r.sharedMaterials))
                .Where(TarkovReflectionCube.IsMissing).Distinct().ToList();
            string scope = selected.Length > 0 ? "the selected materials" : "the open scenes";
            var readOnly = materials.Where(m => !IsEditable(m)).ToList();
            materials = materials.Except(readOnly).ToList();
            if (materials.Count > 0)
            {
                Undo.RecordObjects(materials.ToArray(), "Fix shiny materials");
                foreach (var m in materials)
                {
                    TarkovReflectionCube.Assign(m);
                    Debug.Log($"[Tarkov SDK] Fixed {AssetDatabase.GetAssetPath(m)} ({m.name}): reflection cubemap assigned", m);
                }
                AssetDatabase.SaveAssets();
            }
            foreach (var m in readOnly)
                Debug.LogWarning($"[Tarkov SDK] {m.name} is inside {AssetDatabase.GetAssetPath(m)} and can't be changed; extract the model's materials first", m);
            EditorUtility.DisplayDialog("Fix Shiny Materials",
                (materials.Count == 0 && readOnly.Count == 0 ? $"Every p0 material in {scope} has a reflection cubemap." :
                    $"Gave {materials.Count} material(s) in {scope} the SDK's reflection cubemap and a retail reflection colour. " +
                    "Without one the game reflects a flat grey on them day and night." +
                    (readOnly.Count > 0 ? $"\n\n{readOnly.Count} inside model files were skipped (see the Console)." : "") +
                    "\n\nBuild the map again for the raid."), "OK");
        }

        // Convert SMap To Bumped Specular: "p0/Reflective/Bumped Specular SMap" adds a fixed cubemap reflection
        // (_Cube x _ReflectColor) that never follows the time of day; "p0/Bumped Specular" has none. Converts the
        // selected materials, or with none selected every material on the open scenes' renderers.
        //  smoothness: SMap _SpecMap.r x _Specularness -> _SpecPower x _Shininess (the gloss map is dropped)
        //  spec colour: SMap _SpecColor x MainTex.a x _Glossness -> _SpecColor x MainTex.a x _SpecPower
        //  normal map: SMap samples it at the main uv x _BumpTiling -> Bumped Specular uses _BumpMap_ST
        // the SDK stand-ins carry a "Tarkov/" prefix; the client's ShaderSwap puts the game's shader in its place
        private const string SMapShader = "Tarkov/p0/Reflective/Bumped Specular SMap";
        private const string BumpedSpecularShader = "Tarkov/p0/Bumped Specular";

        [MenuItem(Menu + "Convert SMap Materials To Bumped Specular", priority = 203)]
        public static void ConvertSMap()
        {
            var target = Shader.Find(BumpedSpecularShader);
            if (target == null)
            {
                EditorUtility.DisplayDialog("Convert SMap Materials", $"The \"{BumpedSpecularShader}\" stand-in shader is missing from the project.", "OK");
                return;
            }
            var selected = Selection.GetFiltered<Material>(SelectionMode.Assets);
            var materials = (selected.Length > 0 ? selected : All<Renderer>().SelectMany(r => r.sharedMaterials))
                .Where(m => m != null && m.shader != null && m.shader.name == SMapShader).Distinct().ToList();
            string scope = selected.Length > 0 ? "the selected materials" : "the open scenes";
            if (materials.Count == 0)
            {
                EditorUtility.DisplayDialog("Convert SMap Materials", $"No \"{SMapShader}\" materials in {scope}.", "OK");
                return;
            }
            var readOnly = materials.Where(m => !IsEditable(m)).ToList();
            materials = materials.Except(readOnly).ToList();
            if (!EditorUtility.DisplayDialog("Convert SMap Materials",
                    $"Convert {materials.Count} material(s) in {scope} from \"{SMapShader}\" to \"{BumpedSpecularShader}\"? " +
                    "They lose the cubemap reflection, the gloss map, tint and heat glow. Undo restores them." +
                    (readOnly.Count > 0 ? $"\n\n{readOnly.Count} are inside a model file and can't be changed (extract them in the model's Materials tab first)." : ""),
                    "Convert", "Cancel"))
                return;

            Undo.RecordObjects(materials.ToArray(), "Convert SMap materials");
            foreach (var m in materials)
            {
                float glossness = m.GetFloat("_Glossness"), specularness = m.GetFloat("_Specularness"), tiling = m.GetFloat("_BumpTiling");
                if (tiling == 0f)
                    tiling = 1f;
                var mainScale = m.GetTextureScale("_MainTex");
                var mainOffset = m.GetTextureOffset("_MainTex");
                var bump = m.GetTexture("_BumpMap");
                var keep = new[] { "_Color", "_SpecColor", "_SpecVals", "_DefVals", "_Temperature" }
                    .Where(m.HasProperty).ToDictionary(n => n, m.GetVector);
                float stencil = m.HasProperty("_StencilType") ? m.GetFloat("_StencilType") : 0f;
                var main = m.GetTexture("_MainTex");

                m.shader = target;
                foreach (var (name, value) in keep)
                    m.SetVector(name, value);
                m.SetFloat("_StencilType", stencil);
                m.SetTexture("_MainTex", main);
                m.SetTextureScale("_MainTex", mainScale);
                m.SetTextureOffset("_MainTex", mainOffset);
                m.SetTexture("_BumpMap", bump);
                m.SetTextureScale("_BumpMap", mainScale * tiling);
                m.SetTextureOffset("_BumpMap", mainOffset * tiling);
                m.SetFloat("_SpecPower", Mathf.Clamp(glossness, 0.01f, 10f));
                m.SetFloat("_Shininess", Mathf.Clamp(specularness, 0.03f, 1f));
                m.shaderKeywords = new string[0];
                EditorUtility.SetDirty(m);
                Debug.Log($"[Tarkov SDK] Converted {AssetDatabase.GetAssetPath(m)} ({m.name}) to {BumpedSpecularShader}", m);
            }
            foreach (var m in readOnly)
                Debug.LogWarning($"[Tarkov SDK] {m.name} is inside {AssetDatabase.GetAssetPath(m)} and can't be changed; extract the model's materials first", m);
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Convert SMap Materials", $"Converted {materials.Count} material(s)." +
                (readOnly.Count > 0 ? $" {readOnly.Count} inside model files were skipped (see the Console)." : "") + "\n\nBuild the map again for the raid.", "OK");
        }

        // a material saved as its own .mat (or in the scene), not a model's embedded one
        private static bool IsEditable(Material m)
        {
            string path = AssetDatabase.GetAssetPath(m);
            return string.IsNullOrEmpty(path) || path.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase);
        }

        private static void RecordLight(Object o)
        {
            Undo.RecordObject(o, "Fix night lighting");
            EditorUtility.SetDirty(o);
        }

        // every component of a type in the open scenes, inactive ones included
        private static IEnumerable<T> All<T>() where T : Component
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var c in root.GetComponentsInChildren<T>(true))
                        yield return c;
            }
        }

        // drops missing (deleted) entries; false when the list was already clean
        private static bool Clean<T>(Object owner, ref T[] array) where T : Object
        {
            if (array != null && array.All(x => x != null))
                return false;
            Record(owner);
            array = array == null ? new T[0] : array.Where(x => x != null).ToArray();
            return true;
        }

        private static void Record(Object o)
        {
            Undo.RecordObject(o, "Fix broken interactables");
            EditorUtility.SetDirty(o);
        }

        private static string Where(Component c) => $"{c.gameObject.scene.name}/{c.gameObject.name}";
    }
}
