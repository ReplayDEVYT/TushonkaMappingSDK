using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using Object = UnityEngine.Object;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Mounted guns. A StationaryWeapon only works with its item in the raid's loot: the game matches the loot's
    // container entries by Id (GameWorld.SpawnLoot -> LootItem.CreateStationaryWeapon), as retail's staticWeapons
    // (Woods' WOODS_UTES01: the NSV Utes 5cdeb229d7f00c000e7ce174 with its belt; Reserve's AGS_01: the AGS-30).
    //  - Mounted Gun (Utes): a marker at the gunner's feet. The client plugin places the game's own world prefab
    //    (weapon_ckib_nsv_utes_127x108_world in the weapon's container bundle, which the loot loads) on it just before
    //    the loot spawns and registers it in the LocationScene. The prefab's gunner (OperatorTransform, Root_Joint) is
    //    0.917 m below its root and its tripod's feet 0.588 m, so the tripod stands 0.33 m up: retail Customs' Utes sit
    //    on sandbags and pallets (CUSTOM_UTES01_crouch), and the SDK's has a crate platform under it. The Scene view draws
    //    the prefab from the game install (TarkovGameModelPreview).
    //  - Mounted Gun (AGS-30): the AGS-30 has no world prefab in its bundle, only in retail scenes (Customs'
    //    custom_DesignMain CUSTOM_AGS01_crouch, Reserve's AGS_group, Lighthouse, Ground Zero, Streets:
    //    weapon_zid_ags30_30x29_world, its StationaryWeapon + AGSMachinery on a Script child, the belt an MgBelt on
    //    CharacterJoints). The Asset Importer's reader brings that object in from the game install, once, as
    //    Assets/TarkovGameAssets/Prefabs/Mounted Guns/AGS-30.prefab (every reference stays inside it), and it's placed as
    //    an ordinary scene StationaryWeapon, exported InScene like a ripped retail gun (the server's AGS_01 item).
    //  - StationaryWeapons already in the scene (from a ripped retail map): kept, given ids, their item exported too.
    // Bots don't man them (that needs AIStationaryController links the SDK doesn't make).
    public static class TarkovMountedGuns
    {
        // the Utes world prefab: its root above the gunner's feet; its renderers' bounds around the root
        public const float UtesRootHeight = 0.917f;
        public const float UtesTripodHeight = UtesRootHeight - 0.588f;
        private static readonly Vector3 UtesMin = new Vector3(-0.228f, -0.588f, 0.064f);
        private static readonly Vector3 UtesMax = new Vector3(0.549f, 0.255f, 1.973f);
        // StationaryWeapon's yaw and pitch tolerances on the prefab
        private const float UtesYaw = 20f;

        [MenuItem("GameObject/Tarkov SDK/Interactables/Mounted Gun (Utes)", priority = 37)]
        private static void MenuUtes(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(p).transform.parent.gameObject);

        // creates assets the first time, so outside the menu's own call
        [MenuItem("GameObject/Tarkov SDK/Interactables/Mounted Gun (AGS-30)", priority = 38)]
        private static void MenuAgs(MenuCommand cmd) => EditorApplication.delayCall += () => TarkovSceneTools.Place(cmd, CreateAgs);

        public const string AgsPrefabPath = TarkovGameAssets.Root + "/Prefabs/Mounted Guns/AGS-30.prefab";
        private static readonly string[] AgsScenes = { "custom_DesignMain", "Reserve_Base_DesignMain", "Lighthouse_DesignMain", "Sandbox_Design_Main", "City_Design_Main" };

        // the game's AGS-30, imported from the first retail scene that has one (once; reused after)
        public static GameObject AgsPrefab(out string error)
        {
            error = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AgsPrefabPath);
            if (prefab != null)
                return prefab;
            if (!TarkovGameData.Available)
            {
                error = $"The AGS-30 comes from the game install, and there's none at {TarkovGameData.Folder}. Set Spt Root on your Map Definition, " +
                        "or pick the game's EscapeFromTarkov_Data with Change... in Tarkov SDK > Tools > Asset Importer.";
                return null;
            }
            var files = TarkovGameData.Files;
            var scenes = TarkovGameData.Scenes;
            try
            {
                foreach (var name in AgsScenes)
                {
                    int index = scenes.FindIndex(s => s.EndsWith("/" + name + ".unity", StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                        continue;
                    EditorUtility.DisplayProgressBar("Mounted Gun (AGS-30)", $"Reading the AGS-30 from {name}", 0.4f);
                    var tree = GameSceneCache.Get(files, index);
                    var script = tree?.Nodes.Values.FirstOrDefault(n => n.Scripts.Contains("EFT.Animations.AGSMachinery") && n.Scripts.Contains("EFT.Interactive.StationaryWeapon"));
                    var world = script != null ? tree.Get(script.Parent) : null;
                    if (world == null)
                        continue;
                    TarkovGeneratedAssets.EnsureFolder(System.IO.Path.GetDirectoryName(AgsPrefabPath).Replace('\\', '/'));
                    var store = new GameAssetStore(files);
                    var made = TarkovGameObjects.ImportPrefab(files, tree, world, AgsPrefabPath, store);
                    AssetDatabase.SaveAssets();
                    if (store.Notes.Count > 0)
                        Debug.Log($"[Tarkov SDK] AGS-30 from {name}: " + string.Join("; ", store.Notes.Distinct()));
                    if (made != null)
                    {
                        Debug.Log($"[Tarkov SDK] The game's AGS-30 imported from {name} into {AgsPrefabPath}");
                        return made;
                    }
                }
            }
            catch (Exception e)
            {
                error = "The AGS-30 didn't import: " + e.Message;
                return null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            error = "The game's AGS-30 wasn't found in its retail scenes.";
            return null;
        }

        // the game's AGS-30 on the ground at the spot, facing its forward
        public static GameObject CreateAgs(Transform parent)
        {
            var prefab = AgsPrefab(out var error);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Mounted Gun (AGS-30)", error, "OK");
                return null;
            }
            var root = new GameObject("MountedGun_AGS");
            if (parent != null)
                root.transform.SetParent(parent, false);
            var gun = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.scene);
            gun.transform.SetParent(root.transform, false);
            // its tripod's feet on the ground
            var renderers = gun.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                float bottom = renderers.Min(r => r.bounds.min.y);
                gun.transform.localPosition = new Vector3(0f, root.transform.position.y - bottom, 0f);
            }
            return root;
        }

        // the gun's spot: a crate platform for the tripod, and the marker at the gunner's feet
        public static TarkovMountedGun Create(Transform parent)
        {
            var root = new GameObject("MountedGun_Utes");
            if (parent != null)
                root.transform.SetParent(parent, false);
            var center = new Vector3(0.16f, UtesTripodHeight * 0.5f, 1.0f);
            var size = new Vector3(1.1f, UtesTripodHeight, 1.4f);
            var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "Platform (replace with sandbags)";
            Object.DestroyImmediate(platform.GetComponent<Collider>());
            platform.transform.SetParent(root.transform, false);
            platform.transform.localPosition = center;
            platform.transform.localScale = size;
            var material = TarkovGeneratedAssets.SurfaceMaterial("SDK_WoodPole", new Color(0.28f, 0.21f, 0.15f, 0.08f), 0.12f);
            if (material != null)
                platform.GetComponent<MeshRenderer>().sharedMaterial = material;
#pragma warning disable 618
            GameObjectUtility.SetStaticEditorFlags(platform, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618
            foreach (var (layer, name) in new[] { ("LowPolyCollider", "LowPoly"), ("HighPolyCollider", "HighPoly") })
            {
                var go = new GameObject(name) { layer = LayerMask.NameToLayer(layer) };
                go.transform.SetParent(root.transform, false);
                var box = go.AddComponent<BoxCollider>();
                box.center = center;
                box.size = size;
                if (name == "HighPoly")
                {
                    var ballistic = go.AddComponent<EFT.Ballistics.BallisticCollider>();
                    ballistic._typeOfMaterial = EFT.Ballistics.MaterialType.WoodThick;
                    ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.WoodThick;
                }
#pragma warning disable 618
                else
                    GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.NavigationStatic);
#pragma warning restore 618
            }
            return TarkovMapEvents.EditorOnlyChild(root.transform, "SDK_MountedGun").AddComponent<TarkovMountedGun>();
        }

        public static string Weapon(TarkovMountedGun gun) => "Utes";

        // retail scenes' StationaryWeapon: AGS_17 is the AGS-30, the rest the Utes
        public static string Weapon(StationaryWeapon weapon) => weapon.Animation == StationaryWeapon.EStationaryAnimationType.AGS_17 ? "AGS" : "Utes";

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            var seen = new HashSet<string>(report.MountedGuns.Select(g => g.Id));
            string Unique(string id, string name)
            {
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    return id;
                id = $"{scene.name}_{name}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
                seen.Add(id);
                return id;
            }

            foreach (var gun in roots.SelectMany(r => r.GetComponentsInChildren<TarkovMountedGun>(true)))
            {
                gun.Id = Unique(gun.Id, "Utes");
                EditorUtility.SetDirty(gun);
                // the marker is where the gunner sits
                if (!Physics.Raycast(gun.transform.position + Vector3.up * 0.5f, Vector3.down, out var hit, 1.5f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                    report.Warnings.Add($"{scene.name}: mounted gun '{Name(gun)}' has no ground within a metre below it, so its gunner floats");
                else if (hit.point.y > gun.transform.position.y + 0.15f)
                    report.Warnings.Add($"{scene.name}: mounted gun '{Name(gun)}' is {hit.point.y - gun.transform.position.y:0.00} m below the ground, where its gunner sits");
                if (Vector3.Angle(gun.transform.up, Vector3.up) > 10f)
                    report.Warnings.Add($"{scene.name}: mounted gun '{Name(gun)}' is tilted; the game's prefab stands upright, turn it about Y only");
                report.MountedGuns.Add((gun.Id, Weapon(gun), gun.transform, false));
            }

            foreach (var weapon in roots.SelectMany(r => r.GetComponentsInChildren<StationaryWeapon>(true)))
            {
                weapon._id = Unique(weapon._id, Weapon(weapon));
                EditorUtility.SetDirty(weapon);
                // StationaryWeapon reads these unchecked when a player takes it
                var missing = new List<string>();
                if (weapon.Hinge == null) missing.Add("Hinge");
                if (weapon.WeaponTransform == null) missing.Add("Weapon Transform");
                if (weapon.OperatorTransform == null) missing.Add("Operator Transform");
                if (weapon.TripodView == null) missing.Add("Tripod View");
                if (weapon.TripodAnchor == null) missing.Add("Tripod Anchor");
                if (weapon.Machinery == null) missing.Add("Machinery");
                if (missing.Count > 0)
                    report.Errors.Add($"{scene.name}: mounted gun '{weapon.name}' is missing {string.Join(", ", missing)} (a ripped gun without its parts; rip it again or remove it)");
                report.MountedGuns.Add((weapon._id, Weapon(weapon), weapon.transform, true));
            }
        }

        private static string Name(TarkovMountedGun gun) => gun.transform.parent != null ? gun.transform.parent.name : gun.name;

        // ------------------------------------------------------------ gizmo

        // the prefab's footprint, its line of fire and how far it turns
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawGun(TarkovMountedGun gun, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            var t = gun.transform;
            var root = t.position + Vector3.up * UtesRootHeight;
            var rotation = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
            Gizmos.matrix = Matrix4x4.TRS(root, rotation, Vector3.one);
            Gizmos.color = new Color(0.75f, 0.8f, 0.6f, selected ? 0.9f : 0.5f);
            Gizmos.DrawWireCube((UtesMin + UtesMax) * 0.5f, UtesMax - UtesMin);
            // the barrel over the tripod, and the gunner (OperatorTransform) behind it
            Gizmos.DrawLine(new Vector3(0.13f, -0.25f, 0.07f), new Vector3(0.13f, -0.11f, 1.95f));
            Gizmos.DrawWireSphere(new Vector3(-0.02f, -UtesRootHeight + 0.45f, -0.18f), 0.3f);
            Gizmos.matrix = Matrix4x4.identity;
            if (!selected)
                return;
            Handles.color = new Color(1f, 0.4f, 0.3f, 0.6f);
            var forward = rotation * Vector3.forward;
            Handles.DrawWireArc(root, Vector3.up, Quaternion.AngleAxis(-UtesYaw, Vector3.up) * forward, UtesYaw * 2f, 40f);
            Handles.DrawLine(root, root + Quaternion.AngleAxis(-UtesYaw, Vector3.up) * forward * 40f);
            Handles.DrawLine(root, root + Quaternion.AngleAxis(UtesYaw, Vector3.up) * forward * 40f);
            Handles.Label(t.position + Vector3.up * 1.2f, "Utes (placed in the raid)");
        }
    }

    [CustomEditor(typeof(TarkovMountedGun))]
    internal class TarkovMountedGunEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("The game's Utes is placed here in the raid, facing this object's forward (it turns 20 degrees either way). " +
                                    "This marker is where the gunner sits, on the ground; the tripod stands 0.33 m higher, on the platform. " +
                                    "The Scene view shows the game's own model, read from your game install (nothing is copied into the project).", MessageType.None);
            DrawDefaultInspector();
            using (new EditorGUILayout.HorizontalScope())
            {
                TarkovGameModelPreview.Enabled = EditorGUILayout.ToggleLeft(new GUIContent("Show The Gun", "Draws the Utes from the game's bundles in the Scene view (not saved, not built)."),
                    TarkovGameModelPreview.Enabled);
                if (TarkovGameModelPreview.Enabled && TarkovGameModelPreview.UtesError != null)
                    EditorGUILayout.LabelField(TarkovGameModelPreview.UtesError, EditorStyles.miniLabel);
            }
            if (GUILayout.Button("Drop To Ground"))
                foreach (var gun in targets.Cast<TarkovMountedGun>())
                {
                    // the gun's own platform is skipped: the gunner sits beside it; the whole spot moves
                    var moved = gun.transform.parent != null && gun.name == "SDK_MountedGun" ? gun.transform.parent : gun.transform;
                    var hits = Physics.RaycastAll(gun.transform.position + Vector3.up * 2f, Vector3.down, 50f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore)
                        .Where(h => !h.transform.IsChildOf(moved)).OrderBy(h => h.distance).ToList();
                    if (hits.Count == 0)
                        continue;
                    Undo.RecordObject(moved, "Drop to ground");
                    moved.position += Vector3.up * (hits[0].point.y - gun.transform.position.y);
                    moved.rotation = Quaternion.Euler(0f, moved.eulerAngles.y, 0f);
                }
        }
    }
}
