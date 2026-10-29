using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using EFT.Ballistics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Meta XR Audio room acoustics (reverb and early reflections from the map's real geometry), as the game
    // uses them: Meta's own components from com.meta.xr.sdk.audio 71.0.0, the version the game ships (its
    // MetaXRAudioUnity is native 1.103.0 and its serialized fields match), so they load in the raid as they are.
    //
    //   MetaXRAcousticGeometry   on each geometry root: its meshes baked to an .xrageo file
    //   MetaXRAcousticMaterial   on each mesh: how it absorbs, lets through and scatters sound, from a
    //                            MetaXRAcousticMaterialProperties preset chosen by the bullet material
    //   MetaXRAcousticMap        under the spatial audio system: the baked .xramap the reverb is read from
    //                            (retail: StreamingAssets/Acoustics/<map>_Sound.xramap)
    //
    // In the raid the game loads these from StreamingAssets; CustomMapLoader finds the map's files in its
    // audio folder instead. The package is used through reflection, so the SDK compiles without it.
    public static class TarkovAcoustics
    {
        public const string Folder = "StreamingAssets/Acoustics";
        private const string MaterialsFolder = "Assets/TarkovSDK/Audio/AcousticMaterials";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type Meta(string name) => Type.GetType(name + ", Meta.XR.Audio");
        public static Type GeometryType => Meta("MetaXRAcousticGeometry");
        public static Type MapType => Meta("MetaXRAcousticMap");
        public static Type AcousticMaterialType => Meta("MetaXRAcousticMaterial");
        public static Type PropertiesType => Meta("MetaXRAcousticMaterialProperties");
        public static bool Available => GeometryType != null && MapType != null && AcousticMaterialType != null && PropertiesType != null;

        public const string InstallNote = "Room acoustics need the Meta XR Audio SDK 71.0.0 (com.meta.xr.sdk.audio, from Meta's npm registry). " +
                                          "Tarkov SDK > Install Packages > Meta XR Audio SDK (71.0.0) adds it.";

        // bullet material -> Meta's built-in acoustic material
        private static string Preset(EFT.Ballistics.MaterialType type)
        {
            switch (type)
            {
                case EFT.Ballistics.MaterialType.Concrete: return "Concrete";
                case EFT.Ballistics.MaterialType.Asphalt: return "ConcreteRough";
                case EFT.Ballistics.MaterialType.Stone:
                case EFT.Ballistics.MaterialType.Pebbles: return "Stone";
                case EFT.Ballistics.MaterialType.Tile: return "CeramicTile";
                case EFT.Ballistics.MaterialType.WoodThin: return "WoodThin";
                case EFT.Ballistics.MaterialType.WoodThick: return "WoodThick";
                case EFT.Ballistics.MaterialType.MetalThin:
                case EFT.Ballistics.MaterialType.MetalThick:
                case EFT.Ballistics.MaterialType.MetalNoDecal:
                case EFT.Ballistics.MaterialType.GarbageMetal:
                case EFT.Ballistics.MaterialType.Grate:
                case EFT.Ballistics.MaterialType.Chainfence: return "Steel";
                case EFT.Ballistics.MaterialType.Glass:
                case EFT.Ballistics.MaterialType.GlassShattered: return "Glass";
                case EFT.Ballistics.MaterialType.Cardboard:
                case EFT.Ballistics.MaterialType.GarbagePaper: return "Cardboard";
                case EFT.Ballistics.MaterialType.Fabric:
                case EFT.Ballistics.MaterialType.GenericSoft: return "Carpet";
                case EFT.Ballistics.MaterialType.Plastic:
                case EFT.Ballistics.MaterialType.Rubber:
                case EFT.Ballistics.MaterialType.Tyre: return "Rubber";
                case EFT.Ballistics.MaterialType.GrassHigh:
                case EFT.Ballistics.MaterialType.GrassLow: return "Grass";
                case EFT.Ballistics.MaterialType.Gravel: return "Gravel";
                case EFT.Ballistics.MaterialType.Soil:
                case EFT.Ballistics.MaterialType.SoilForest: return "Soil";
                case EFT.Ballistics.MaterialType.Mud:
                case EFT.Ballistics.MaterialType.Swamp: return "Mud";
                case EFT.Ballistics.MaterialType.Water:
                case EFT.Ballistics.MaterialType.WaterPuddle: return "Water";
                case EFT.Ballistics.MaterialType.Snow: return "Snow";
                default: return "MetaDefault";
            }
        }

        // over the scene's whole map (TarkovMapScenes.MapScenes): its terrains carry their own geometry in their scene
        public static Component FindMap(Scene scene) =>
            !Available ? null : TarkovMapScenes.MapRoots(scene).Select(g => g.GetComponentInChildren(MapType, true)).FirstOrDefault(c => c != null);

        public static Component[] Geometries(Scene scene) =>
            !Available ? new Component[0] : TarkovMapScenes.MapRoots(scene).SelectMany(g => g.GetComponentsInChildren(GeometryType, true)).ToArray();

        // ------------------------------------------------------------ what is baked

        // Meta bakes every MeshFilter under a geometry, visible or not, moving or not. So the map's own objects
        // carry no geometry: the SDK copies the meshes that count into proxies (a MeshFilter and an acoustic
        // material each) under SpatialAudioSystem/AcousticGeometry, one geometry per scene root. The proxies are
        // EditorOnly, so they never ship; the geometry objects do, and load their baked files in the raid.
        // Terrains carry their own geometry (Meta reads the terrain itself).
        public const string GeometryRootName = "AcousticGeometry";
        // smaller things (bottles, boxes, cables) don't change how a room sounds, and cost bake time
        public const float MinObjectSize = 1f;

        public class Source
        {
            public string Name;                 // the geometry's name: the scene root's, or "Terrain <name>"
            public GameObject Root;
            public Terrain Terrain;
            public readonly List<(MeshFilter Filter, Object Properties)> Meshes = new List<(MeshFilter, Object)>();
            public string Hash;
        }

        public class Selection
        {
            public readonly List<Source> Sources = new List<Source>();
            public int Meshes, Triangles, Dynamic, Hidden, NotStatic, Small, Lod;

            public override string ToString() =>
                $"{Meshes} mesh(es) ({Triangles:N0} triangles) in {Sources.Count} geometry file(s); left out: {Dynamic} moving (physics props, doors, animated), " +
                $"{Hidden} invisible, {NotStatic} not marked Static, {Small} under {MinObjectSize:0.#} m, {Lod} finer LODs";
        }

        public static Selection Select(Scene scene, SpatialAudioSystem system)
        {
            var selection = new Selection();
            if (!Available || system == null)
                return selection;
            var skip = new HashSet<GameObject>(new[] { system.gameObject, TarkovAmbientAudioSystem.Find(scene) }.Where(g => g != null));
            var names = new HashSet<string>();
            // every scene of the map: the one spatial audio system hears all of it
            foreach (var root in TarkovMapScenes.MapRoots(scene))
            {
                if (skip.Contains(root) || root.CompareTag("EditorOnly") || root.name.StartsWith("SDK_") || root.GetComponentInChildren<AmbientAudioSystem>(true) != null)
                    continue;
                var name = root.name;
                for (int i = 2; !names.Add(name); i++)
                    name = $"{root.name} {i}";
                var source = new Source { Name = name, Root = root };
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!Counts(renderer, selection))
                        continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    source.Meshes.Add((filter, MaterialFor(renderer)));
                    selection.Meshes++;
                    selection.Triangles += Triangles(filter.sharedMesh);
                }
                if (source.Meshes.Count > 0)
                {
                    source.Hash = TarkovAudioBakeState.SourceHash(source);
                    selection.Sources.Add(source);
                }
                foreach (var terrain in root.GetComponentsInChildren<Terrain>(true).Where(t => t.isActiveAndEnabled && t.terrainData != null && !EditorOnly(t.transform)))
                {
                    var t = new Source { Name = "Terrain " + terrain.name, Root = terrain.gameObject, Terrain = terrain };
                    t.Hash = TarkovAudioBakeState.SourceHash(t);
                    selection.Sources.Add(t);
                }
            }
            return selection;
        }

        private static bool EditorOnly(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.CompareTag("EditorOnly"))
                    return true;
            return false;
        }

        // static, visible geometry that never moves
        private static bool Counts(MeshRenderer renderer, Selection tally)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || EditorOnly(renderer.transform))
                return false;
            // meshes made at runtime (TextMeshPro text) are empty when the scene loads; Meta can't bake them
            if (filter.sharedMesh.vertexCount == 0 || renderer.GetComponents<MonoBehaviour>().Any(c => c != null && c.GetType().Namespace == "TMPro"))
                return false;
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
            {
                tally.Hidden++;
                return false;
            }
            if (renderer.GetComponentInParent<Rigidbody>(true) != null || renderer.GetComponentInParent<EFT.Interactive.WorldInteractiveObject>(true) != null
                || renderer.GetComponentInParent<Animator>(true) != null)
            {
                tally.Dynamic++;
                return false;
            }
            // breakable windows stay in, Static or not, so the glass keeps blocking sound (the baked copy stays
            // when one breaks; the window's audio portal still opens)
            bool window = renderer.GetComponentInParent<EFT.Interactive.WindowBreaker>(true) != null;
            if (!window && GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) == 0)
            {
                tally.NotStatic++;
                return false;
            }
            var size = renderer.bounds.size;
            if (Mathf.Max(size.x, size.y, size.z) < MinObjectSize)
            {
                tally.Small++;
                return false;
            }
            // in a LOD group only its coarsest level: acoustics don't need the detail, the bake is faster
            var group = renderer.GetComponentInParent<LODGroup>(true);
            if (group != null)
            {
                var lods = group.GetLODs();
                var coarsest = lods.LastOrDefault(l => l.renderers != null && l.renderers.Any(r => r != null));
                if (lods.Any(l => l.renderers != null && l.renderers.Contains(renderer)) && (coarsest.renderers == null || !coarsest.renderers.Contains(renderer)))
                {
                    tally.Lod++;
                    return false;
                }
            }
            return true;
        }

        private static int Triangles(Mesh mesh)
        {
            long count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                count += mesh.GetIndexCount(i) / 3;
            return (int)Math.Min(count, int.MaxValue);
        }

        // the mesh's own acoustic material if it has one (set up by an older SDK, or by hand), else by its
        // bullet material, else by its look
        private static Object MaterialFor(Renderer renderer)
        {
            var own = renderer.GetComponent(AcousticMaterialType);
            var properties = own != null ? new SerializedObject(own).FindProperty("properties")?.objectReferenceValue : null;
            if (properties != null)
                return properties;
            var ballistic = renderer.GetComponent<BallisticCollider>() ?? renderer.GetComponentInChildren<BallisticCollider>(true)
                            ?? renderer.transform.parent?.GetComponentsInChildren<BallisticCollider>(true).FirstOrDefault(b => b.name.StartsWith(renderer.name));
            return Properties(ballistic != null ? Preset(ballistic._typeOfMaterial) : GuessPreset(renderer));
        }

        // ------------------------------------------------------------ setup

        public static Transform GeometryRoot(SpatialAudioSystem system, bool create)
        {
            var root = system.transform.Find(GeometryRootName);
            if (root == null && create)
            {
                root = new GameObject(GeometryRootName).transform;
                root.SetParent(system.transform, false);
                Undo.RegisterCreatedObjectUndo(root.gameObject, "Acoustic geometry");
            }
            return root;
        }

        public static Component GeometryOf(SpatialAudioSystem system, Source source)
        {
            if (source.Terrain != null)
                return source.Terrain.GetComponent(GeometryType);
            var go = GeometryRoot(system, false)?.Find(source.Name);
            return go != null ? go.GetComponent(GeometryType) : null;
        }

        // the geometries and their proxies for these sources (all when null), the old SDK's geometries on scene
        // roots removed, unused ones removed, and the acoustic map
        public static Selection Setup(Scene scene, SpatialAudioSystem system, ICollection<Source> only = null)
        {
            var selection = Select(scene, system);
            if (!Available)
                return selection;
            var container = GeometryRoot(system, true);
            var wanted = new HashSet<Component>();
            foreach (var source in selection.Sources)
            {
                var geometry = GeometryOf(system, source);
                if (only != null && !only.Any(s => s.Name == source.Name) && geometry != null)
                {
                    wanted.Add(geometry);
                    continue;
                }
                if (source.Terrain != null)
                {
                    geometry ??= Undo.AddComponent(source.Terrain.gameObject, GeometryType);
                    if (source.Terrain.GetComponent(AcousticMaterialType) == null)
                    {
                        var material = Undo.AddComponent(source.Terrain.gameObject, AcousticMaterialType);
                        var mso = new SerializedObject(material);
                        Set(mso, "properties", Properties("Soil"));
                        mso.ApplyModifiedProperties();
                    }
                    Configure(geometry, scene, source.Name, children: false);
                    wanted.Add(geometry);
                    continue;
                }
                if (geometry == null)
                {
                    var go = new GameObject(source.Name);
                    go.transform.SetParent(container, false);
                    Undo.RegisterCreatedObjectUndo(go, "Acoustic geometry");
                    geometry = go.AddComponent(GeometryType);
                }
                geometry.gameObject.isStatic = true;
                Configure(geometry, scene, source.Name, children: true);
                BuildProxies(geometry.transform, source);
                wanted.Add(geometry);
            }
            // anything else carrying a geometry: the old SDK's on scene roots, and roots that no longer have meshes
            foreach (var geometry in Geometries(scene).Where(g => !wanted.Contains(g)).ToList())
            {
                if (geometry.transform.parent == container)
                    Undo.DestroyObjectImmediate(geometry.gameObject);
                else
                    Undo.DestroyObjectImmediate(geometry);
            }

            if (FindMap(scene) == null)
            {
                var go = new GameObject("AcousticMap");
                go.transform.SetParent(system.transform, false);
                Undo.RegisterCreatedObjectUndo(go, "Add acoustic map");
                var map = go.AddComponent(MapType);
                var so = new SerializedObject(map);
                // 1 m head height, points up to 20 m above the floor (retail, Labyrinth_Sound); spacing and
                // reflections from the quality
                Set(so, "relativeFilePath", $"{Folder}/{scene.name}.xramap");
                Set(so, "HeadHeight", 1f);
                Set(so, "MaxHeight", 20f);
                so.ApplyModifiedProperties();
                SetQuality(map, 1);
            }
            return selection;
        }

        private static void Configure(Component geometry, Scene scene, string name, bool children)
        {
            var so = new SerializedObject(geometry);
            Set(so, "IncludeChildMeshes", children);
            Set(so, "FileEnabled", true);
            Set(so, "useColliders", false);
            if (string.IsNullOrEmpty(so.FindProperty("relativeFilePath")?.stringValue))
                Set(so, "relativeFilePath", $"{Folder}/{scene.name}/{Sanitize(name)}_{Guid.NewGuid().ToString("N").Substring(0, 6)}.xrageo");
            so.ApplyModifiedProperties();
        }

        // EditorOnly copies of the meshes, placed as the originals, with their acoustic material; rebuilt each time
        private static void BuildProxies(Transform geometry, Source source)
        {
            for (int i = geometry.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(geometry.GetChild(i).gameObject);
            var toLocal = geometry.worldToLocalMatrix;
            foreach (var (filter, properties) in source.Meshes)
            {
                var go = new GameObject(filter.name) { tag = "EditorOnly", hideFlags = HideFlags.NotEditable };
                go.transform.SetParent(geometry, false);
                var m = toLocal * filter.transform.localToWorldMatrix;
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                go.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var material = go.AddComponent(AcousticMaterialType);
                var so = new SerializedObject(material);
                Set(so, "properties", properties);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ------------------------------------------------------------ map quality

        // spacing between the map's points and reflections per point: the bake time is mostly points x reflections
        public static readonly (string Label, float MinSpacing, float MaxSpacing, int Reflections)[] Qualities =
        {
            ("Retail (0.5-8 m, 8 reflections: slowest)", 0.5f, 8f, 8),
            ("Balanced (1-10 m, 6 reflections: Meta's default)", 1f, 10f, 6),
            ("Fast (2-12 m, 4 reflections)", 2f, 12f, 4),
        };

        public static int Quality(Component map)
        {
            var so = new SerializedObject(map);
            float min = so.FindProperty("MinSpacing")?.floatValue ?? 0f, max = so.FindProperty("MaxSpacing")?.floatValue ?? 0f;
            int reflections = (int)(so.FindProperty("ReflectionCount")?.longValue ?? 0);
            return Array.FindIndex(Qualities, q => Mathf.Approximately(q.MinSpacing, min) && Mathf.Approximately(q.MaxSpacing, max) && q.Reflections == reflections);
        }

        public static void SetQuality(Component map, int index)
        {
            var q = Qualities[index];
            var so = new SerializedObject(map);
            Set(so, "MinSpacing", q.MinSpacing);
            Set(so, "MaxSpacing", q.MaxSpacing);
            Set(so, "ReflectionCount", q.Reflections);
            so.ApplyModifiedProperties();
        }

        private static string GuessPreset(Renderer renderer)
        {
            var name = (renderer.sharedMaterial != null ? renderer.sharedMaterial.name : renderer.name).ToLowerInvariant();
            if (name.Contains("glass") || name.Contains("window")) return "Glass";
            if (name.Contains("metal") || name.Contains("steel") || name.Contains("iron")) return "Steel";
            if (name.Contains("brick")) return "Brick";
            if (name.Contains("wood") || name.Contains("plank")) return "WoodThick";
            if (name.Contains("tile")) return "CeramicTile";
            if (name.Contains("carpet") || name.Contains("fabric")) return "Carpet";
            if (name.Contains("grass")) return "Grass";
            if (name.Contains("dirt") || name.Contains("soil") || name.Contains("ground")) return "Soil";
            return "Concrete";
        }

        // one shared properties asset per built-in preset
        private static Object Properties(string preset)
        {
            var path = $"{MaterialsFolder}/{preset}.asset";
            var existing = AssetDatabase.LoadAssetAtPath(path, PropertiesType);
            if (existing != null)
                return existing;
            Directory.CreateDirectory(MaterialsFolder);
            var asset = ScriptableObject.CreateInstance(PropertiesType);
            var presetType = PropertiesType.GetNestedType("BuiltinPreset", Any);
            var property = PropertiesType.GetProperty("Preset", Any);
            if (presetType != null && property != null)
                property.SetValue(asset, Enum.Parse(presetType, preset));
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        // ------------------------------------------------------------ bake

        public enum BakeMode
        {
            Changes, // only geometry that changed, and the map only when anything did
            Quick,   // as Changes, with a coarse, fast map (Draft): for listening tests
            Full,    // everything, whatever changed
        }

        // a draft map: points every 2 to 16 m and 4 reflections instead of retail's 0.5 to 8 m and 8
        private static readonly (string Field, object Value)[] DraftSettings = { ("MinSpacing", 2f), ("MaxSpacing", 16f), ("ReflectionCount", 4) };

        private static string Relative(Component c) => new SerializedObject(c).FindProperty("relativeFilePath")?.stringValue;

        private static bool FileExists(Component c)
        {
            var relative = Relative(c);
            return !string.IsNullOrEmpty(relative) && File.Exists(Path.Combine(Application.dataPath, relative));
        }

        public class Status
        {
            public Selection Selection = new Selection();
            public List<Source> Changed = new List<Source>();
            public bool MapStale, MapDraft, MapMissing, OldSetup;
            public string MapHash;

            public override string ToString() =>
                MapMissing ? "Not baked yet."
                : Changed.Count == 0 && !MapStale ? (MapDraft ? "Up to date (quick bake: draft quality)." : "Up to date.")
                : $"{Changed.Count} geometry file(s) to bake{(MapStale ? ", the acoustic map needs computing again" : "")}.";
        }

        // what changed since the last bake, by the meshes that count (not the proxies, which are rebuilt to bake)
        public static Status Check(Scene scene, TarkovAudioBakeState state = null)
        {
            var status = new Status();
            var system = TarkovSpatialAudio.Find(scene);
            if (!Available || system == null)
                return status;
            state ??= TarkovAudioBakeState.Load(scene);
            status.Selection = Select(scene, system);
            var container = GeometryRoot(system, false);
            // geometry left on scene roots by the SDK before 1.6.0 bakes everything under them
            status.OldSetup = Geometries(scene).Any(g => g.transform.parent != container && g.GetComponent<Terrain>() == null);
            foreach (var source in status.Selection.Sources)
            {
                var geometry = GeometryOf(system, source);
                if (geometry == null || !FileExists(geometry) || !state.Geometries.TryGetValue(Relative(geometry) ?? "", out var old) || old != source.Hash)
                    status.Changed.Add(source);
            }
            var map = FindMap(scene);
            if (map != null)
            {
                status.MapHash = TarkovAudioBakeState.MapHash(map, status.Selection.Sources.Select(s => s.Hash));
                status.MapMissing = !FileExists(map);
                status.MapStale = status.MapMissing || state.Map != status.MapHash;
                status.MapDraft = state.MapDraft;
            }
            return status;
        }

        // the geometry files, then the acoustic map, computed on Meta's worker thread; FinishCompute on the
        // main thread when it's done, as Meta's own inspector does. What hasn't changed since the last bake
        // is skipped unless the mode is Full.
        public static bool Bake(Scene scene, BakeMode mode, Action<bool, string> done)
        {
            if (!Available)
            {
                done(false, InstallNote);
                return false;
            }
            var system = TarkovSpatialAudio.Find(scene);
            if (system == null)
            {
                done(false, "no spatial audio system");
                return false;
            }
            var state = TarkovAudioBakeState.Load(scene);
            var status = Check(scene, state);
            // the proxies of what's baked now, the old setup's geometries and unused ones removed
            var bake = mode == BakeMode.Full ? status.Selection.Sources : status.Changed;
            Setup(scene, system, bake.ToList());
            int written = 0;
            var write = GeometryType.GetMethod("WriteFile", Any, null, Type.EmptyTypes, null);
            for (int i = 0; i < bake.Count; i++)
            {
                var source = bake[i];
                var geometry = GeometryOf(system, source);
                EditorUtility.DisplayProgressBar("Acoustics", $"Writing geometry {source.Name}", (float)i / bake.Count);
                if (geometry != null && write != null && (bool)write.Invoke(geometry, null))
                {
                    written++;
                    state.Geometries[Relative(geometry) ?? ""] = source.Hash;
                }
                else
                    Debug.LogWarning($"[Tarkov SDK] Acoustics: could not bake the geometry of {source.Name}", geometry);
            }
            EditorUtility.ClearProgressBar();
            state.Save(scene);
            string geometryNote = $"{status.Selection}. {written} geometry file(s) written, {status.Selection.Sources.Count - bake.Count} unchanged";
            var map = FindMap(scene);
            if (map == null)
            {
                done(true, $"{geometryNote}; no acoustic map to compute (Set Up Acoustics adds one)");
                return true;
            }
            // a quick bake replaces a full one only when something changed; a full map isn't redone just
            // because the last one was a draft, unless asked
            bool draft = mode == BakeMode.Quick;
            bool compute = mode == BakeMode.Full || status.MapStale || (mode == BakeMode.Changes && state.MapDraft);
            if (!compute)
            {
                done(true, $"{geometryNote}; the acoustic map is up to date{(state.MapDraft ? " (draft quality)" : "")}");
                return true;
            }

            var so = new SerializedObject(map);
            var saved = DraftSettings.Select(s => (s.Field, Value: Read(so.FindProperty(s.Field)))).ToList();
            if (draft)
            {
                foreach (var (field, value) in DraftSettings)
                    Set(so, field, value);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            void Restore()
            {
                if (!draft || map == null)
                    return;
                var restore = new SerializedObject(map);
                foreach (var (field, value) in saved)
                    if (value != null)
                        Set(restore, field, value);
                restore.ApplyModifiedPropertiesWithoutUndo();
            }

            var computeMethod = MapType.GetMethod("Compute", Any, null, new[] { typeof(bool) }, null);
            if (computeMethod == null || !(bool)computeMethod.Invoke(map, new object[] { false }))
            {
                Restore();
                done(false, $"{geometryNote}; the acoustic map could not start computing (see the Console)");
                return false;
            }
            var finished = MapType.GetProperty("ComputeFinished", Any);
            var progress = MapType.GetProperty("ComputeProgress", Any);
            var finish = MapType.GetMethod("FinishCompute", Any);
            void Poll()
            {
                if (map == null)
                {
                    EditorApplication.update -= Poll;
                    EditorUtility.ClearProgressBar();
                    return;
                }
                if (finished == null || !(bool)finished.GetValue(map))
                {
                    float p = progress != null ? (float)progress.GetValue(map) : 0f;
                    if (EditorUtility.DisplayCancelableProgressBar("Acoustics", "Computing the acoustic map", p))
                        MapType.GetMethod("CancelCompute", Any)?.Invoke(map, null);
                    return;
                }
                EditorApplication.update -= Poll;
                EditorUtility.ClearProgressBar();
                bool ok = finish == null || (bool)finish.Invoke(map, null);
                Restore();
                EditorUtility.SetDirty(map);
                if (ok)
                {
                    // the hash of the map as configured, so a draft is recognised as one
                    var after = TarkovAudioBakeState.Load(scene);
                    after.Map = status.MapHash;
                    after.MapDraft = draft;
                    after.Save(scene);
                }
                done(ok, ok ? $"{geometryNote}; the acoustic map computed{(draft ? " (quick bake: draft quality)" : "")}"
                            : $"{geometryNote}; the acoustic map failed to compute (see the Console)");
            }
            EditorApplication.update += Poll;
            return true;
        }

        // the files the raid loads: the map's and every geometry's, relative to Assets
        public static List<string> Files(Scene scene)
        {
            var files = new List<string>();
            if (!Available)
                return files;
            foreach (var component in Geometries(scene).Concat(new[] { FindMap(scene) }).Where(c => c != null))
            {
                var relative = new SerializedObject(component).FindProperty("relativeFilePath")?.stringValue;
                if (string.IsNullOrEmpty(relative))
                    continue;
                var full = Path.Combine(Application.dataPath, relative);
                if (File.Exists(full))
                    files.Add(full);
            }
            return files;
        }

        private static object Read(SerializedProperty p) =>
            p == null ? null : p.propertyType == SerializedPropertyType.Float ? (object)p.floatValue : p.propertyType == SerializedPropertyType.Integer ? p.intValue : null;

        private static void Set(SerializedObject so, string name, object value)
        {
            var p = so.FindProperty(name);
            if (p == null)
            {
                Debug.LogWarning($"[Tarkov SDK] Acoustics: {so.targetObject.GetType().Name} has no field '{name}' (a different Meta XR Audio version?)");
                return;
            }
            switch (value)
            {
                case bool b: p.boolValue = b; break;
                case int i: p.intValue = i; break;
                case float f: p.floatValue = f; break;
                case string s: p.stringValue = s; break;
                case Object o: p.objectReferenceValue = o; break;
            }
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }
    }
}
