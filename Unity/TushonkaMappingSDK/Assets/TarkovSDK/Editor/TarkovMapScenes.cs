using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EFT;
using EFT.EnvironmentEffect;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Maps made of several scenes (the Map Definition's Scenes). The game loads the first as Single and the rest
    // Additive, and every scene's LocationScene joins LocationScene.LoadedScenes, so doors, loot, triggers, spawns and
    // the rest work from any of them. What doesn't split across scenes:
    //  - the parts a map has once. Build Map adds the missing ones to the first scene (TarkovScenePreparer), so one
    //    kept in another scene ends up twice; a second EnvironmentManager even switches its own GameObject off
    //    (EnvironmentManager.Init).
    //  - indoor triggers: the game only checks the ones under the EnvironmentManager's first TriggerGroup
    //    (EnvironmentManagerBase.InitIndoors), so those in another scene never put anyone indoors.
    //  - ids (doors, lamps, windows, sync objects, spawn points, exfil names, quest zones) are looked up map-wide;
    //    TarkovScenePreparer keeps them unique across the scenes (a copied scene shares all of them).
    // Gather moves the first two into the first scene with Undo: Build Map runs it before preparing the scenes, the
    // Map Definition's Fix Scene Setup on the open scenes. Unity blocks references between scenes, so what moves
    // here is only what nothing else points at (managers, indoor trigger groups).
    public static class TarkovMapScenes
    {
        private sealed class Part
        {
            public string Label;
            public Func<Scene, GameObject> Find;
        }

        private static readonly Part[] OncePerMap =
        {
            new Part { Label = "LevelSettings", Find = s => First<LevelSettings>(s)?.gameObject },
            new Part { Label = "EnvironmentManager", Find = s => First<EnvironmentManager>(s)?.gameObject },
            new Part { Label = "time of day", Find = TimeOfDay },
            new Part { Label = "AIStationaryController", Find = s => First<AIStationaryController>(s)?.gameObject },
            new Part { Label = "ambient audio system", Find = TarkovAmbientAudioSystem.Find },
        };

        // the full weather system's root, or the simple rig's ("Weather", made by TarkovLightingRig.Create)
        private static GameObject TimeOfDay(Scene scene)
        {
            var full = TarkovWeatherBuilder.Find(scene);
            if (full != null)
                return full;
            var sky = TarkovLightingRig.Find(scene);
            if (sky == null)
                return null;
            for (var t = sky.transform; t != null; t = t.parent)
                if (t.name == "Weather")
                    return t.gameObject;
            return sky.gameObject;
        }

        private static T First<T>(Scene scene) where T : Component =>
            scene.GetRootGameObjects().Select(r => r.GetComponentInChildren<T>(true)).FirstOrDefault(c => c != null);

        // Moves the once-per-map parts and the indoor triggers of scenes[1..] into scenes[0]; returns what it did and
        // what's left to the mapper
        public static List<string> Gather(IList<Scene> scenes)
        {
            var messages = new List<string>();
            if (scenes.Count < 2)
                return messages;
            var first = scenes[0];
            foreach (var part in OncePerMap)
            {
                var kept = part.Find(first);
                for (int i = 1; i < scenes.Count; i++)
                {
                    var go = part.Find(scenes[i]);
                    if (go == null)
                        continue;
                    if (kept != null)
                        messages.Add($"{scenes[i].name}: has its own {part.Label} ('{go.name}'), and so does the first scene, {first.name}; a map has one. " +
                                     "Delete this one");
                    else if (go.GetComponentInChildren<LocationScene>(true) != null)
                        messages.Add($"{scenes[i].name}: its {part.Label} ('{go.name}') shares an object with the scene's LocationScene; a map has one, in its first scene. " +
                                     $"Move it into {first.name} by hand");
                    else
                    {
                        Move(go, first, null);
                        kept = go;
                        messages.Add($"{scenes[i].name}: {part.Label} '{go.name}' moved into the first scene, {first.name} (a map has one, in its first scene)");
                    }
                }
            }

            TriggerGroup root = null;
            for (int i = 1; i < scenes.Count; i++)
            {
                // the largest TriggerGroup above each trigger, short of an EnvironmentManager (a duplicate one stays behind)
                var units = new List<Transform>();
                foreach (var trigger in scenes[i].GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<IndoorTrigger>(true)))
                {
                    var unit = trigger.transform;
                    for (var t = trigger.transform; t != null && t.GetComponent<EnvironmentManager>() == null; t = t.parent)
                        if (t.GetComponent<TriggerGroup>() != null)
                            unit = t;
                    if (!units.Contains(unit))
                        units.Add(unit);
                }
                if (units.Count == 0)
                    continue;
                root ??= IndoorTriggerGroup(first);
                foreach (var unit in units)
                    Move(unit.gameObject, first, root.transform);
                messages.Add($"{scenes[i].name}: {units.Count} indoor trigger group(s) moved under the EnvironmentManager in {first.name}; " +
                             "the game only checks indoor triggers there");
            }
            return messages;
        }

        // the first scene's EnvironmentManager and its first TriggerGroup, made when missing
        private static TriggerGroup IndoorTriggerGroup(Scene scene)
        {
            var manager = First<EnvironmentManager>(scene);
            if (manager == null)
            {
                var go = new GameObject("EnvironmentManager");
                SceneManager.MoveGameObjectToScene(go, scene);
                manager = go.AddComponent<EnvironmentManager>();
                Undo.RegisterCreatedObjectUndo(go, "Create EnvironmentManager");
            }
            var group = manager.GetComponentInChildren<TriggerGroup>(true);
            if (group == null)
            {
                var go = new GameObject("IndoorTriggers");
                go.transform.SetParent(manager.transform, false);
                group = go.AddComponent<TriggerGroup>();
                Undo.RegisterCreatedObjectUndo(go, "Create TriggerGroup");
            }
            return group;
        }

        // only a root object can change scenes
        private static void Move(GameObject go, Scene scene, Transform parent)
        {
            var from = go.scene;
            if (go.transform.parent != null)
                Undo.SetTransformParent(go.transform, null, "Move To First Scene");
            if (go.scene != scene)
                Undo.MoveGameObjectToScene(go, scene, "Move To First Scene");
            if (parent != null)
                Undo.SetTransformParent(go.transform, parent, "Move To First Scene");
            EditorSceneManager.MarkSceneDirty(from);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        // whether the loaded navmesh reaches the scene's ground (null: nothing walkable in it). A navmesh baked before a
        // scene was added, or with it closed, misses it entirely; samples the tops of up to 40 walkable colliders
        public static bool? NavMeshCovers(Scene scene)
        {
            int mask = LayerMask.GetMask("Terrain", "LowPolyCollider");
            var colliders = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Collider>())
                .Where(c => !c.isTrigger && ((1 << c.gameObject.layer) & mask) != 0 && !c.CompareTag("EditorOnly")).Take(40).ToList();
            if (colliders.Count == 0)
                return null;
            foreach (var c in colliders)
            {
                // the collider's own surface under its middle (a terrain's top is its highest hill)
                var b = c.bounds;
                var ray = new Ray(new Vector3(b.center.x, b.max.y + 1f, b.center.z), Vector3.down);
                var point = c.Raycast(ray, out var hit, b.size.y + 2f) ? hit.point : new Vector3(b.center.x, b.max.y, b.center.z);
                if (UnityEngine.AI.NavMesh.SamplePosition(point, out _, 3f, UnityEngine.AI.NavMesh.AllAreas))
                    return true;
            }
            return false;
        }

        public static List<string> Paths(TarkovMapDefinition def) =>
            def.Scenes.Where(s => s != null).Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).ToList();

        // open scenes that look like part of the map (a LocationScene, or in the first scene's folder) but aren't in it
        public static List<Scene> NotInMap(TarkovMapDefinition def)
        {
            var paths = Paths(def);
            var folder = paths.Count > 0 ? Path.GetDirectoryName(paths[0])?.Replace('\\', '/') : null;
            var found = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || string.IsNullOrEmpty(scene.path) || paths.Contains(scene.path))
                    continue;
                if (First<LocationScene>(scene) != null || Path.GetDirectoryName(scene.path)?.Replace('\\', '/') == folder)
                    found.Add(scene);
            }
            return found;
        }

        // ------------------------------------------------------------ Map Definition buttons

        // opens the map's scenes that aren't, keeping the rest, with the first active (new objects go there)
        public static List<Scene> OpenAll(TarkovMapDefinition def)
        {
            var scenes = new List<Scene>();
            foreach (var path in Paths(def))
            {
                var scene = SceneManager.GetSceneByPath(path);
                if (!scene.isLoaded)
                    scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                scenes.Add(scene);
            }
            if (scenes.Count > 0)
                SceneManager.SetActiveScene(scenes[0]);
            return scenes;
        }

        public static void FixSetup(TarkovMapDefinition def)
        {
            var scenes = OpenAll(def);
            if (scenes.Count == 0)
                return;
            var messages = Gather(scenes);
            foreach (var m in messages)
                Debug.Log("[Tarkov SDK] " + m);
            EditorUtility.DisplayDialog("Tarkov SDK", messages.Count == 0
                ? "The map's scenes are set up: its once-per-map parts and indoor triggers are all in the first scene."
                : "- " + string.Join("\n- ", messages) + "\n\nSave the scenes to keep this (Undo puts it back).", "OK");
        }

        // a new scene next to the first, with its LocationScene, added to the map and opened with the others
        public static void AddScene(TarkovMapDefinition def, string name)
        {
            name = (name ?? "").Trim();
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Scene names are letters, digits, underscores and dashes.", "OK");
                return;
            }
            var paths = Paths(def);
            if (paths.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)))
            {
                // the game loads scenes by file name
                EditorUtility.DisplayDialog("Tarkov SDK", $"The map already has a scene named '{name}'.", "OK");
                return;
            }
            var folder = paths.Count > 0 ? Path.GetDirectoryName(paths[0]).Replace('\\', '/') : Path.GetDirectoryName(AssetDatabase.GetAssetPath(def)).Replace('\\', '/');
            var path = $"{folder}/{name}.unity";
            if (File.Exists(path))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", $"{path} already exists. Add it to Scenes instead, or pick another name.", "OK");
                return;
            }
            OpenAll(def);
            Scene scene;
            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Unity couldn't add a scene: " + e.Message + "\n\nSave or close untitled scenes first.", "OK");
                return;
            }
            var location = new GameObject("LocationScene");
            SceneManager.MoveGameObjectToScene(location, scene);
            location.AddComponent<LocationScene>();
            if (!EditorSceneManager.SaveScene(scene, path))
                return;
            Undo.RecordObject(def, "Add Scene");
            def.Scenes.Add(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tarkov SDK] Added {path} to '{def.MapId}'. Click its name in the Hierarchy and Set Active Scene to build in it.");
        }

        public static void AddToMap(TarkovMapDefinition def, Scene scene)
        {
            if (Paths(def).Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), scene.name, StringComparison.OrdinalIgnoreCase)))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", $"The map already has a scene named '{scene.name}'; scene names must be unique.", "OK");
                return;
            }
            Undo.RecordObject(def, "Add Scene");
            def.Scenes.Add(AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path));
            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
        }
    }
}
