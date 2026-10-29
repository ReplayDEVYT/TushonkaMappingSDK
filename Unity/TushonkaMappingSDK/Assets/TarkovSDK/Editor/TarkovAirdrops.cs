using System.Collections.Generic;
using System.Linq;
using EFT.Airdrop;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Airdrop points: where a supply crate can land. The game picks one for a plane drop (base.json
    // AirdropParameters: chance, times, how many; Server Data Editor > Access) and the nearest one to a red
    // flare. Retail scatters hundreds over the open ground (Customs ~600, custom_Scripts' custom_airdrop_points),
    // each an AirdropPoint on an empty object; LocationScene lists them at Build Map.
    public static class TarkovAirdrops
    {
        public const string RootName = "AirdropPoints";
        public const string GeneratedName = "Generated";

        [MenuItem("GameObject/Tarkov SDK/World Events/Airdrop Point", priority = 77)]
        private static void MenuPoint(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreatePoint(p ?? Root(SceneManager.GetActiveScene()).transform, Vector3.zero).gameObject);

        [MenuItem("Tarkov SDK/Generation/Misc/Airdrop Points", priority = 111)]
        private static void MenuGenerate() => EditorApplication.delayCall += () => Generate(SceneManager.GetActiveScene(), 20f);

        public static GameObject Root(Scene scene)
        {
            var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
            if (root == null)
            {
                root = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, scene);
                Undo.RegisterCreatedObjectUndo(root, "Airdrop points");
            }
            return root;
        }

        public static AirdropPoint CreatePoint(Transform parent, Vector3 position)
        {
            var go = TarkovFacilitySystems.Child(parent, "AirdropPoint", Vector3.zero);
            go.transform.position = position;
            return go.AddComponent<AirdropPoint>();
        }

        // a point every 'spacing' metres where the sky is open, the ground flat, and there's room for the crate
        public static int Generate(Scene scene, float spacing)
        {
            if (!TarkovRainCover.SolidBounds(scene, TarkovRainCover.SolidMask, out var bounds))
            {
                Debug.LogWarning("[Tarkov SDK] Airdrop points: the scene has no solid geometry to drop on.");
                return 0;
            }
            var root = Root(scene).transform;
            var old = root.Find(GeneratedName);
            if (old != null)
                Undo.DestroyObjectImmediate(old.gameObject);
            var folder = TarkovFacilitySystems.Child(root, GeneratedName, Vector3.zero);
            Undo.RegisterCreatedObjectUndo(folder, "Generate airdrop points");
            int mask = TarkovRainCover.SolidMask, water = LayerMask.GetMask("Water");
            var indoor = Object.FindObjectsOfType<EFT.EnvironmentEffect.IndoorTrigger>(true).Select(t => TarkovScenePreparer.TransformBounds(t.transform)).ToList();
            int count = 0;
            float top = bounds.max.y + 100f;
            for (float x = bounds.min.x + spacing * 0.5f; x < bounds.max.x; x += spacing)
                for (float z = bounds.min.z + spacing * 0.5f; z < bounds.max.z; z += spacing)
                {
                    // the first thing a crate falling here meets is the ground: open sky
                    if (!Physics.Raycast(new Vector3(x, top, z), Vector3.down, out var hit, top - bounds.min.y + 10f, mask | water, QueryTriggerInteraction.Ignore))
                        continue;
                    if ((water & (1 << hit.collider.gameObject.layer)) != 0 || hit.normal.y < 0.9f)
                        continue;
                    if (indoor.Any(b => b.Contains(hit.point + Vector3.up)))
                        continue;
                    // room for the crate and its parachute around it
                    if (Physics.CheckSphere(hit.point + Vector3.up * 3f, 2.5f, mask, QueryTriggerInteraction.Ignore))
                        continue;
                    CreatePoint(folder.transform, hit.point);
                    count++;
                }
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[Tarkov SDK] Airdrop points: {count} generated every {spacing:0} m on open, flat ground under {RootName}/{GeneratedName}. " +
                      "Plane drops are set in Tarkov SDK > Map Settings > Data > Server Data Editor > Airdrops; a red flare calls one to the nearest point.");
            return count;
        }

        public const string PhysicsSetupName = "SDK_PhysicsSetup";

        // LevelPhysicsSettings, as retail's "PhysicsSetup" (Customs: extents 600 x 200 x 270 around the map, 16
        // subdivisions): the physics broadphase regions, and the box the airdrop plane flies in. ServerPlane.Init
        // reads it unchecked, so without one the plane stays at the origin and never drops. Build Map sizes the
        // SDK's own to every loaded map scene; one placed by hand is left alone.
        public static void EnsurePhysicsSetup(Scene active, TarkovScenePreparer.Report report)
        {
            var all = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded)
                                .SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<LevelPhysicsSettings>(true)).ToList();
            if (all.Count > 1)
                report.Warnings.Add($"{active.name}: {all.Count} LevelPhysicsSettings in the map's scenes; the game uses the first and logs an error for the others");
            var settings = all.FirstOrDefault();
            if (settings != null && settings.name != PhysicsSetupName)
                return;
            bool any = false;
            var bounds = new Bounds();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !TarkovRainCover.SolidBounds(scene, TarkovRainCover.SolidMask, out var b))
                    continue;
                if (!any) bounds = b; else bounds.Encapsulate(b);
                any = true;
            }
            if (!any)
                bounds = new Bounds(Vector3.zero, new Vector3(400f, 100f, 400f));
            // room for the drop's wind drift and the plane's turn at the edges
            bounds.Expand(new Vector3(200f, 100f, 200f));
            if (settings == null)
            {
                var go = new GameObject(PhysicsSetupName);
                SceneManager.MoveGameObjectToScene(go, active);
                settings = go.AddComponent<LevelPhysicsSettings>();
            }
            settings.transform.position = bounds.center;
            settings._mbpBounds = new Bounds(Vector3.zero, bounds.size);
            settings._mbpSubdivisions = 16;
            EditorUtility.SetDirty(settings);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawPoint(AirdropPoint point, GizmoType type)
        {
            var p = point.transform.position;
            Gizmos.color = new Color(0.3f, 0.6f, 1f, (type & GizmoType.Selected) != 0 ? 1f : 0.5f);
            Gizmos.DrawWireCube(p + Vector3.up * 0.6f, new Vector3(1.2f, 1.2f, 1.2f));
            if ((type & GizmoType.Selected) != 0)
                Gizmos.DrawLine(p, p + Vector3.up * 30f);
        }
    }
}
