using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // CustomMapLoader's triggered spawns (Tarkov has none): bots, loot and Unity objects that appear when a player fires
    // a trigger id. The settings are SDK components on EditorOnly objects (TarkovSpawnBots, TarkovSpawnLoot,
    // TarkovSpawnPrefab) whose children are the spawn points; Build Map exports them to sdk_export.json "Features" and
    // the client plugin runs them (Client/Features/SdkSpawns.cs). A prefab asset isn't in the map's bundle unless the
    // scene has it, so Build Map puts a switched-off copy of each under SDK_SpawnTemplates; the client finds templates
    // and scene objects by scene and hierarchy path.
    public static class TarkovTriggeredSpawns
    {
        public const string TemplatesRoot = "SDK_SpawnTemplates";
        private const string Menu = "GameObject/Tarkov SDK/Triggers/";

        [MenuItem(Menu + "Spawn Bots", priority = 88)]
        private static void MenuBots(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateBots(p).gameObject);

        [MenuItem(Menu + "Spawn Loot", priority = 89)]
        private static void MenuLoot(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateLoot(p).gameObject);

        [MenuItem(Menu + "Spawn Prefab", priority = 90)]
        private static void MenuPrefab(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var spawn = CreatePrefab(p);
            // a prefab picked in the Project window is the one to spawn
            if (Selection.activeObject is GameObject asset && PrefabUtility.IsPartOfPrefabAsset(asset))
                spawn.Prefab = asset;
            return spawn.gameObject;
        });

        public static TarkovSpawnBots CreateBots(Transform parent)
        {
            var spawn = TarkovMapEvents.EditorOnlyChild(parent, "SDK_SpawnBots").AddComponent<TarkovSpawnBots>();
            Point(spawn.transform, new Vector3(-1.5f, 0f, 0f));
            Point(spawn.transform, new Vector3(1.5f, 0f, 0f));
            return spawn;
        }

        public static TarkovSpawnLoot CreateLoot(Transform parent)
        {
            // the items come at the spawner itself (At This Object); children only count with that off
            return TarkovMapEvents.EditorOnlyChild(parent, "SDK_SpawnLoot").AddComponent<TarkovSpawnLoot>();
        }

        public static TarkovSpawnPrefab CreatePrefab(Transform parent)
        {
            var spawn = TarkovMapEvents.EditorOnlyChild(parent, "SDK_SpawnPrefab").AddComponent<TarkovSpawnPrefab>();
            Point(spawn.transform, Vector3.zero);
            return spawn;
        }

        private static void Point(Transform parent, Vector3 local)
        {
            var go = TarkovMapEvents.EditorOnlyChild(parent, "Point " + (parent.childCount + 1));
            go.transform.localPosition = local;
        }

        // the spawn points: the object's children, or the object itself without any (or, for loot, when At This Object)
        public static List<Transform> Points(Component spawn)
        {
            if (spawn is TarkovSpawnLoot loot && loot.AtThisObject)
                return new List<Transform> { spawn.transform };
            var children = spawn.transform.Cast<Transform>().ToList();
            return children.Count > 0 ? children : new List<Transform> { spawn.transform };
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var bots in roots.SelectMany(r => r.GetComponentsInChildren<TarkovSpawnBots>(true)))
            {
                if (string.IsNullOrEmpty(bots.TriggerId))
                {
                    report.Warnings.Add($"{scene.name}: bot spawn '{bots.name}' has no trigger id, so it never spawns (connect one in Triggers & Sounds)");
                    continue;
                }
                foreach (var point in Points(bots).Where(p => !UnityEngine.AI.NavMesh.SamplePosition(p.position, out _, 2f, UnityEngine.AI.NavMesh.AllAreas)))
                    report.Warnings.Add($"{scene.name}: bot spawn '{bots.name}': point '{point.name}' isn't on the navmesh, so a bot there can't move");
                report.SpawnBots.Add(bots);
            }
            foreach (var loot in roots.SelectMany(r => r.GetComponentsInChildren<TarkovSpawnLoot>(true)))
            {
                var valid = loot.Items.Where(i => i != null && Regex.IsMatch(i.Template?.Trim() ?? "", "^[0-9a-fA-F]{24}$")).ToList();
                if (string.IsNullOrEmpty(loot.TriggerId))
                    report.Warnings.Add($"{scene.name}: loot spawn '{loot.name}' has no trigger id, so it never spawns (connect one in Triggers & Sounds)");
                else if (valid.Count == 0)
                    report.Warnings.Add($"{scene.name}: loot spawn '{loot.name}' has no items with a valid 24-character template id");
                else
                {
                    if (valid.Count < loot.Items.Count)
                        report.Warnings.Add($"{scene.name}: loot spawn '{loot.name}': {loot.Items.Count - valid.Count} item(s) without a valid template id are skipped");
                    report.SpawnLoot.Add(loot);
                }
            }

            Transform templates = null;
            foreach (var spawn in roots.SelectMany(r => r.GetComponentsInChildren<TarkovSpawnPrefab>(true)))
            {
                if (string.IsNullOrEmpty(spawn.TriggerId))
                {
                    report.Warnings.Add($"{scene.name}: prefab spawn '{spawn.name}' has no trigger id, so it never happens (connect one in Triggers & Sounds)");
                    continue;
                }
                if (spawn.Prefab == null)
                {
                    report.Errors.Add($"{scene.name}: prefab spawn '{spawn.name}' has no Prefab");
                    continue;
                }
                bool asset = PrefabUtility.IsPartOfPrefabAsset(spawn.Prefab) || !spawn.Prefab.scene.IsValid();
                GameObject target;
                if (asset)
                {
                    if (spawn.Action != TarkovSpawnPrefab.SpawnAction.Spawn)
                    {
                        report.Errors.Add($"{scene.name}: prefab spawn '{spawn.name}' can only {spawn.Action} an object in the scene; '{spawn.Prefab.name}' is a prefab asset (use Spawn, or drag it into the scene)");
                        continue;
                    }
                    if (templates == null)
                    {
                        var root = new GameObject(TemplatesRoot);
                        SceneManager.MoveGameObjectToScene(root, scene);
                        templates = root.transform;
                    }
                    target = (GameObject)PrefabUtility.InstantiatePrefab(spawn.Prefab, scene);
                    target.transform.SetParent(templates, false);
                    target.name = $"Template {templates.childCount} {spawn.Prefab.name}";
                    target.SetActive(false);
                }
                else
                {
                    target = spawn.Prefab;
                    if (target.scene != scene)
                    {
                        report.Errors.Add($"{scene.name}: prefab spawn '{spawn.name}' uses '{target.name}' from another scene; keep it in the same scene");
                        continue;
                    }
                    if (target.GetComponentsInParent<Transform>(true).Any(t => t.CompareTag("EditorOnly")))
                    {
                        report.Errors.Add($"{scene.name}: prefab spawn '{spawn.name}': '{target.name}' is (under) an EditorOnly object, so it isn't in the build; move it out");
                        continue;
                    }
                    if (spawn.Action == TarkovSpawnPrefab.SpawnAction.Show && target.activeSelf)
                        report.Warnings.Add($"{scene.name}: prefab spawn '{spawn.name}' shows '{target.name}', which is already on; switch it off in the scene");
                    if (spawn.Action == TarkovSpawnPrefab.SpawnAction.Hide && !target.activeSelf)
                        report.Warnings.Add($"{scene.name}: prefab spawn '{spawn.name}' hides '{target.name}', which is already off");
                }
                var path = PathOf(target.transform);
                if (Resolve(scene, path) != target)
                    report.Errors.Add($"{scene.name}: prefab spawn '{spawn.name}': another object has the same place and name as '{path}', so the game can't tell them apart; rename it");
                if (target.GetComponentsInChildren<EFT.Interactive.WorldInteractiveObject>(true).Any() || target.GetComponentsInChildren<EFT.Interactive.ExfiltrationPoint>(true).Any())
                    report.Warnings.Add($"{scene.name}: prefab spawn '{spawn.name}': '{target.name}' has doors, switches, containers or extracts, which the raid registers at start; " +
                                        "spawned or shown later they don't work (it's for props, lights, effects and sounds)");
                report.SpawnPrefabs.Add((spawn, scene.name, path));
            }
        }

        // templates are made again on every build
        public static void ClearTemplates(Scene scene)
        {
            foreach (var old in scene.GetRootGameObjects().Where(r => r.name == TemplatesRoot))
                Object.DestroyImmediate(old);
        }

        public static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (var x = t; x != null; x = x.parent)
                names.Insert(0, x.name);
            return string.Join("/", names);
        }

        // as the client finds it: the first root with the name, then the first child with each name
        internal static GameObject Resolve(Scene scene, string path)
        {
            var names = path.Split('/');
            var current = scene.GetRootGameObjects().FirstOrDefault(g => g.name == names[0])?.transform;
            for (int i = 1; i < names.Length && current != null; i++)
                current = current.Cast<Transform>().FirstOrDefault(c => c.name == names[i]);
            return current != null ? current.gameObject : null;
        }
    }

    [CustomEditor(typeof(TarkovSpawnLoot))]
    public class TarkovSpawnLootEditor : UnityEditor.Editor
    {
        private readonly TarkovItemPicker.State _picker = new TarkovItemPicker.State();

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var spawn = (TarkovSpawnLoot)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Add Items", EditorStyles.boldLabel);
            var picked = TarkovItemPicker.Draw(_picker, "Add");
            if (picked != null)
            {
                Undo.RecordObject(spawn, "Add loot item");
                spawn.Items.Add(new TarkovLootSpawn.Item { Template = picked, Name = TarkovItemPicker.Name(picked) });
                EditorUtility.SetDirty(spawn);
            }
            EditorGUILayout.LabelField(spawn.AtThisObject
                ? "The items appear at this object (raise it a little off the ground; with gravity they drop and settle)."
                : "The items appear at this object's children, one per child in turn (add more to spread them out).", EditorStyles.wordWrappedMiniLabel);
        }
    }

    public static class TarkovTriggeredSpawnGizmos
    {
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawBots(TarkovSpawnBots spawn, GizmoType type) =>
            Draw(spawn, new Color(1f, 0.3f, 0.3f), type, p => Gizmos.DrawWireCube(p.position + Vector3.up * 0.9f, new Vector3(0.6f, 1.8f, 0.6f)),
                $"Spawns {spawn.Count} {spawn.BotRole}");

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawLoot(TarkovSpawnLoot spawn, GizmoType type) =>
            Draw(spawn, new Color(1f, 0.85f, 0.2f), type, p => Gizmos.DrawWireCube(p.position, Vector3.one * 0.3f), $"Spawns {spawn.Items.Count} item(s)");

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawPrefab(TarkovSpawnPrefab spawn, GizmoType type)
        {
            if (spawn.Action != TarkovSpawnPrefab.SpawnAction.Spawn)
            {
                if (spawn.Prefab != null && spawn.Prefab.scene.IsValid())
                {
                    Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.7f);
                    Gizmos.DrawLine(spawn.transform.position, spawn.Prefab.transform.position);
                }
                Handles.Label(spawn.transform.position + Vector3.up * 0.5f, $"{spawn.Action} {(spawn.Prefab != null ? spawn.Prefab.name : "(no prefab)")}");
                return;
            }
            Draw(spawn, new Color(0.3f, 0.9f, 1f), type, p =>
            {
                Gizmos.matrix = Matrix4x4.TRS(p.position, p.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.up * 0.5f, Vector3.one);
                Gizmos.DrawLine(Vector3.up * 0.5f, Vector3.up * 0.5f + Vector3.forward);
                Gizmos.matrix = Matrix4x4.identity;
            }, $"Spawns {(spawn.Prefab != null ? spawn.Prefab.name : "(no prefab)")}");
        }

        private static void Draw(Component spawn, Color color, GizmoType type, System.Action<Transform> drawPoint, string label)
        {
            bool selected = (type & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0;
            Gizmos.color = new Color(color.r, color.g, color.b, selected ? 1f : 0.5f);
            foreach (var p in TarkovTriggeredSpawns.Points(spawn))
            {
                drawPoint(p);
                if (p != spawn.transform)
                    Gizmos.DrawLine(spawn.transform.position, p.position);
            }
            if (selected)
                Handles.Label(spawn.transform.position + Vector3.up * 2.2f, label);
        }
    }
}
