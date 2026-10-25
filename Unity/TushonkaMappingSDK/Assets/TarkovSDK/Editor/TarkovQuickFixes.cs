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
