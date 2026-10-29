using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Decompilation > Clean Up Ripped Scene...: the manual jobs after opening a retail scene imported from
    // an AssetRipper export, in one pass over the open scenes. Scan counts each problem; Clean fixes the ticked ones
    // (with Undo); the rest are listed with what to do.
    //  - components whose script didn't come across (classes the SDK has no stub for: culling, impostors, GPU
    //    instancing...): removed, as Unity would refuse to build them
    //  - materials on a game shader the SDK has a stand-in for: moved onto it (as Fix Ripped Materials)
    //  - tree impostors (AmplifyImpostor billboards; their shader and script are retail-only): removed with their LOD
    //  - renderers and mesh colliders without a mesh, LODGroups pointing at removed renderers: removed / tidied
    //  - cameras and audio listeners left in the scene (the raid brings its own): removed
    //  - terrains on retail terrain shaders (MicroSplat ships per map): given SDK_Terrain (Nature/Terrain/Diffuse)
    //  - reported only: statically batched meshes (re-rip with AssetRipper's static mesh separation), scene singletons
    //    the map has more than one of (LocationScene, LevelSettings, the time of day, EnvironmentManager), a retail
    //    spatial audio system (its bake is retail's; set up the SDK's own)
    public class TarkovRipCleanup : EditorWindow
    {
        private sealed class Finding
        {
            public string Label;
            public string Help;
            public bool Fixable;
            public bool Enabled = true;
            public readonly List<Object> Items = new List<Object>();
            public System.Func<Object, bool> Fix;
        }

        private readonly List<Finding> _findings = new List<Finding>();
        private bool _scanned;
        private Vector2 _scroll;
        private string _result;

        [MenuItem("Tarkov SDK/Decompilation/Clean Up Ripped Scene...", priority = 147)]
        public static void Open()
        {
            var window = GetWindow<TarkovRipCleanup>("Clean Up Ripped Scene");
            window.minSize = new Vector2(460f, 420f);
            window.Scan();
        }

        private static IEnumerable<GameObject> AllObjects()
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        yield return t.gameObject;
            }
        }

        private static IEnumerable<T> All<T>() where T : Component => AllObjects().SelectMany(g => g.GetComponents<T>()).Where(c => c != null);

        private void Scan()
        {
            _findings.Clear();
            _result = null;
            var objects = AllObjects().ToList();

            var missing = new Finding { Label = "Missing scripts", Fixable = true, Help = "Components whose class the SDK has no stub for (culling, impostors, instancing...). Removed.",
                Fix = o => GameObjectUtility.RemoveMonoBehavioursWithMissingScript((GameObject)o) > 0 };
            foreach (var go in objects)
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0)
                    missing.Items.Add(go);
            _findings.Add(missing);

            var renderers = All<Renderer>().ToList();
            var materials = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            var standIns = new HashSet<string>(AssetDatabase.FindAssets("t:Shader", new[] { "Assets/TarkovSDK" })
                .Select(g => AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null && s.name.StartsWith("Tarkov/")).Select(s => s.name.Substring(7)));
            var onGame = new Finding { Label = "Materials on game shaders with an SDK stand-in", Fixable = true,
                Help = "Moved onto the stand-in (Tarkov/<name>), which previews it in the editor; the game swaps in its own shader in the raid." };
            onGame.Items.AddRange(materials.Where(m => m.shader != null && standIns.Contains(m.shader.name)));
            onGame.Fix = null;
            _findings.Add(onGame);

            var impostors = new Finding { Label = "Tree impostors", Fixable = true,
                Help = "Retail trees' far billboards (AmplifyImpostor): their shader and script only exist in retail. Removed; the tree's LODs end with its last mesh.",
                Fix = o =>
                {
                    var r = (Renderer)o;
                    RemoveFromLods(r);
                    var go = r.gameObject;
                    if (go.GetComponents<Component>().Length <= 3 && go.transform.childCount == 0)
                        Undo.DestroyObjectImmediate(go);
                    else
                    {
                        var filter = go.GetComponent<MeshFilter>();
                        Undo.DestroyObjectImmediate(r);
                        if (filter != null)
                            Undo.DestroyObjectImmediate(filter);
                    }
                    return true;
                } };
            impostors.Items.AddRange(renderers.Where(IsImpostor));
            _findings.Add(impostors);

            var noMesh = new Finding { Label = "Renderers and mesh colliders without a mesh", Fixable = true,
                Help = "The rip lost their mesh, so they draw or collide with nothing. Removed.",
                Fix = o =>
                {
                    if (o is Renderer r)
                        RemoveFromLods(r);
                    Undo.DestroyObjectImmediate(o);
                    return true;
                } };
            foreach (var r in renderers.OfType<MeshRenderer>())
            {
                var filter = r.GetComponent<MeshFilter>();
                if (!impostors.Items.Contains(r) && (filter == null || filter.sharedMesh == null))
                    noMesh.Items.Add(r);
            }
            noMesh.Items.AddRange(All<MeshCollider>().Where(c => c.sharedMesh == null));
            _findings.Add(noMesh);

            var lods = new Finding { Label = "LOD groups with missing renderers", Fixable = true, Help = "Their missing entries are dropped.",
                Fix = o => TidyLods((LODGroup)o) };
            lods.Items.AddRange(All<LODGroup>().Where(g => g.GetLODs().Any(l => l.renderers.Any(r => r == null))));
            _findings.Add(lods);

            var cameras = new Finding { Label = "Cameras and audio listeners", Fixable = true,
                Help = "Left in retail scenes for BSG's tools; the raid brings its own camera and listener, and these would render or hear on top. Removed.",
                Fix = o =>
                {
                    Undo.DestroyObjectImmediate(o);
                    return true;
                } };
            // the SDK's own (the weather's camera effects template) stay
            bool Ours(Component c) => c.GetComponentsInParent<Transform>(true).Any(t => t.name.StartsWith("SDK_") || t.name == TarkovWeatherBuilder.RootName);
            cameras.Items.AddRange(All<Camera>().Where(c => c.hideFlags == HideFlags.None && !Ours(c)));
            cameras.Items.AddRange(All<AudioListener>().Where(l => !Ours(l)));
            _findings.Add(cameras);

            var terrains = new Finding { Label = "Terrains on retail terrain shaders", Fixable = true,
                Help = "Retail terrain shaders (MicroSplat) only load with their own map; these get SDK_Terrain (Unity's Diffuse terrain shader, always loaded). Repaint layers if needed.",
                Fix = o =>
                {
                    var terrain = (Terrain)o;
                    Undo.RecordObject(terrain, "Terrain material");
                    terrain.materialTemplate = TarkovTerrains.TerrainMaterial();
                    return true;
                } };
            terrains.Items.AddRange(All<Terrain>().Where(t => t.materialTemplate != null && !TarkovTerrains.GameHasShader(t.materialTemplate)));
            _findings.Add(terrains);

            var batched = new Finding { Label = "Statically batched meshes (can't be fixed here)",
                Help = "Renderers drawing a 'Combined Mesh': AssetRipper exported the batch, not the object's own mesh, so they draw the wrong geometry. " +
                       "Export again with AssetRipper's Static Mesh Separation on." };
            foreach (var filter in All<MeshFilter>())
                if (filter.sharedMesh != null && filter.sharedMesh.name.StartsWith("Combined Mesh"))
                    batched.Items.Add(filter);
            _findings.Add(batched);

            var singletons = new Finding { Label = "Scene singletons the map has more than one of",
                Help = "Keep one per map (LocationScene: one per scene). Usually the ripped scene's copy goes and the SDK skeleton's stays, or the other way round; Build Map warns about the rest." };
            void Several<T>() where T : Component
            {
                var found = All<T>().ToList();
                bool perScene = typeof(T) == typeof(LocationScene);
                if (perScene ? found.GroupBy(f => f.gameObject.scene).Any(g => g.Count() > 1) : found.Count > 1)
                    singletons.Items.AddRange(found);
            }
            Several<LocationScene>();
            Several<LevelSettings>();
            Several<EFT.EnvironmentEffect.EnvironmentManager>();
            Several<TOD_Sky>();
            Several<EFT.Weather.WeatherController>();
            Several<AIStationaryController>();
            _findings.Add(singletons);

            var audio = new Finding { Label = "A retail spatial audio system",
                Help = "Its rooms point at a bake from the retail map. Remove it and generate the SDK's (Tarkov SDK > Generation > Audio > Spatial Audio...), or keep its rooms and bake them there." };
            // the SDK's own bakes are under CustomMapLoader/
            foreach (var system in All<Audio.SpatialSystem.SpatialAudioSystem>())
                if (system._locationInfo == null || system._locationInfo.relativeBakeDataPath == null ||
                    !system._locationInfo.relativeBakeDataPath.StartsWith(TarkovSpatialAudio.BakePathPrefix))
                    audio.Items.Add(system);
            _findings.Add(audio);
            _scanned = true;
        }

        private static bool IsImpostor(Renderer r)
        {
            var name = r.gameObject.name;
            if (name.IndexOf("impostor", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name.IndexOf("Impostor", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void RemoveFromLods(Renderer r)
        {
            var group = r.GetComponentInParent<LODGroup>();
            if (group == null)
                return;
            Undo.RecordObject(group, "Tidy LODs");
            var list = group.GetLODs().Select(l => new LOD(l.screenRelativeTransitionHeight, l.renderers.Where(x => x != null && x != r).ToArray())).Where(l => l.renderers.Length > 0).ToArray();
            group.SetLODs(list);
        }

        private static bool TidyLods(LODGroup group)
        {
            Undo.RecordObject(group, "Tidy LODs");
            group.SetLODs(group.GetLODs().Select(l => new LOD(l.screenRelativeTransitionHeight, l.renderers.Where(x => x != null).ToArray())).Where(l => l.renderers.Length > 0).ToArray());
            return true;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox("Fixes a retail scene imported from an AssetRipper export (Decompilation > Import AssetRipper Export...) in the open scenes. " +
                                    "Scan, untick what to keep, then Clean. Save the scenes afterwards.", MessageType.None);
            if (GUILayout.Button("Scan"))
                Scan();
            if (!_scanned)
            {
                EditorGUILayout.EndScrollView();
                return;
            }
            foreach (var finding in _findings)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (finding.Fixable && finding.Items.Count > 0)
                            finding.Enabled = EditorGUILayout.ToggleLeft($"{finding.Label}: {finding.Items.Count}", finding.Enabled, EditorStyles.boldLabel);
                        else
                            EditorGUILayout.LabelField($"{finding.Label}: {finding.Items.Count}", finding.Items.Count > 0 ? EditorStyles.boldLabel : EditorStyles.label);
                        if (finding.Items.Count > 0 && GUILayout.Button("Select", GUILayout.Width(52f)))
                            Selection.objects = finding.Items.Select(i => i is Component c ? c.gameObject : i).Distinct().ToArray();
                    }
                    if (finding.Items.Count > 0)
                        EditorGUILayout.LabelField(finding.Help, EditorStyles.wordWrappedMiniLabel);
                }
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!_findings.Any(f => f.Fixable && f.Enabled && f.Items.Count > 0)))
                if (GUILayout.Button("Clean", GUILayout.Height(28f)))
                    EditorApplication.delayCall += Clean;
            if (!string.IsNullOrEmpty(_result))
                EditorGUILayout.HelpBox(_result, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void Clean()
        {
            Undo.SetCurrentGroupName("Clean up ripped scene");
            int group = Undo.GetCurrentGroup();
            var done = new List<string>();
            foreach (var finding in _findings.Where(f => f.Fixable && f.Enabled && f.Items.Count > 0))
            {
                int fixedCount;
                if (finding.Fix == null)
                    fixedCount = TarkovRipImport.FixMaterials(finding.Items.OfType<Material>());
                else
                {
                    fixedCount = 0;
                    foreach (var item in finding.Items)
                        if (item != null)
                        {
                            if (item is GameObject go)
                                Undo.RegisterCompleteObjectUndo(go, finding.Label);
                            if (finding.Fix(item))
                                fixedCount++;
                        }
                }
                done.Add($"{finding.Label.ToLowerInvariant()}: {fixedCount}");
            }
            Undo.CollapseUndoOperations(group);
            for (int s = 0; s < SceneManager.sceneCount; s++)
                if (SceneManager.GetSceneAt(s).isLoaded)
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(s));
            AssetDatabase.SaveAssets();
            Debug.Log("[Tarkov SDK] Clean Up Ripped Scene: " + string.Join(", ", done));
            Scan();
            _result = "Cleaned: " + string.Join(", ", done) + ". Save the scenes to keep it.";
            Repaint();
        }
    }
}
