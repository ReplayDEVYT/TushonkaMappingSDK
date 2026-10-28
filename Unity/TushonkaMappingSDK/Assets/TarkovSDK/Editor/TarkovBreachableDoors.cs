using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Doors that splinter when kicked in, as retail's wooden doors (Customs dorms, Factory; 195 across the retail maps).
    // Any Shut or Locked door with Can Be Breached offers "Breach"; a successful kick (Door.KickOpen) swings it open on
    // its Kick Curve and, when the door's parent has a BrokenDoor (found with GetComponentInChildren, inactive included),
    // calls BrokenDoor.Breach: it switches its On objects on and Off objects off, activates itself, plays its VFX and
    // drops its Splinters (kinematic rigidbodies made dynamic). Retail layout, copied here:
    //   <door root>
    //     <leaf with Door>
    //       Splintered (inactive, an On object: the broken wood round the lock)
    //     BrokenController (inactive, BrokenDoor)
    //       Effect (ParticleSystem, play on awake off)
    //       Splinters/Splinter_n (Rigidbody, kinematic, mass 0.1, with a collider: Init ignores collisions with the leaf)
    // Late joiners get BreachQuick (On/Off only) from the door's synced IsBroken.
    public static class TarkovBreachableDoors
    {
        private const string Menu = "GameObject/Tarkov SDK/Interactables/";
        private const string SplinterMesh = TarkovGeneratedAssets.PropsFolder + "/SDK_Splinter.asset";

        [MenuItem(Menu + "Breachable Door", priority = 28)]
        private static void MenuDoor(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var door = TarkovPrefabTools.CreateDoor(p, 1f, 2.1f);
            door.transform.parent.name = "BreachableDoor";
            MakeBreachable(door, 6, true);
            return door.transform.parent.gameObject;
        });

        [MenuItem("Tarkov SDK/Tools/Interactables/Make Selected Doors Breachable", priority = 117)]
        private static void MenuMakeBreachable()
        {
            var doors = Selection.gameObjects.SelectMany(g => g.GetComponentsInChildren<Door>(true)).Distinct().ToList();
            if (doors.Count == 0)
            {
                EditorUtility.DisplayDialog("Make Doors Breachable", "Select doors (objects with a Door component, or their parents).", "OK");
                return;
            }
            Undo.SetCurrentGroupName("Make doors breachable");
            int made = 0;
            foreach (var door in doors)
                if (MakeBreachable(door, 6, false) != null)
                    made++;
            Debug.Log($"[Tarkov SDK] {made} of {doors.Count} door(s) made breachable; kick one in the game to see it splinter");
        }

        [MenuItem("Tarkov SDK/Tools/Interactables/Make Selected Doors Breachable", true)]
        private static bool CanMakeBreachable() => Selection.gameObjects.Any(g => g.GetComponentInChildren<Door>(true) != null);

        // The breach setup for a door, under its parent (made if the leaf is a root). Null if it has one already.
        public static BrokenDoor MakeBreachable(Door door, int splinters, bool created)
        {
            if (door is KeycardDoor)
                return null;
            var root = door.transform.parent;
            if (root == null)
            {
                var go = new GameObject(door.name + "_Root");
                if (!created)
                    Undo.RegisterCreatedObjectUndo(go, "Door root");
                go.transform.SetPositionAndRotation(door.transform.position, door.transform.rotation);
                if (created)
                    door.transform.SetParent(go.transform, true);
                else
                    Undo.SetTransformParent(door.transform, go.transform, "Door root");
                root = go.transform;
            }
            if (root.GetComponentInChildren<BrokenDoor>(true) != null)
                return null;
            if (!created)
                Undo.RecordObject(door, "Breachable");
            door.CanBeBreached = true;
            door.CanInteractWithBreach = true;
            if (door.KickCurve == null || door.KickCurve.length == 0)
                door.KickCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            EditorUtility.SetDirty(door);

            // where the lock is: the door's handle, else 85 % across the leaf at hand height
            var leafBounds = LeafBounds(door);
            Vector3 lockWorld = door._handle != null ? door._handle.transform.position
                : leafBounds.size == Vector3.zero ? door.transform.position + Vector3.up
                : FarEdge(door, leafBounds);
            var material = door.GetComponentsInChildren<Renderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null)
                           ?? TarkovSceneTools.DefaultSurfaceMaterial();
            float thickness = Mathf.Max(0.03f, Vector3.Dot(leafBounds.size, Abs(door.transform.InverseTransformDirection(door.transform.forward))));

            var controller = new GameObject("BrokenController");
            controller.transform.SetParent(root, false);
            if (!created)
                Undo.RegisterCreatedObjectUndo(controller, "Breach setup");
            var broken = controller.AddComponent<BrokenDoor>();
            // BrokenDoor converts it with its parent's InverseTransformPoint (a quirk of the game's code: the force then
            // lands far from the splinters and they just drop, as on retail doors)
            broken.ExplosionCenter = lockWorld;
            broken.VFX = Effect(controller.transform, lockWorld);

            var holder = new GameObject("Splinters");
            holder.transform.SetParent(controller.transform, false);
            var pieces = new List<Rigidbody>();
            var random = new System.Random(door.GetInstanceID());
            for (int i = 0; i < splinters; i++)
            {
                var piece = new GameObject("Splinter_" + (i + 1));
                piece.transform.SetParent(holder.transform, false);
                var offset = door.transform.right * (float)(random.NextDouble() - 0.5) * 0.18f + door.transform.up * (float)(random.NextDouble() - 0.5) * 0.3f;
                piece.transform.position = lockWorld + offset;
                piece.transform.rotation = door.transform.rotation * Quaternion.Euler(0f, 0f, (float)(random.NextDouble() * 360.0));
                piece.transform.localScale = Vector3.one * (0.7f + (float)random.NextDouble() * 0.6f);
                piece.AddComponent<MeshFilter>().sharedMesh = Splinter();
                piece.AddComponent<MeshRenderer>().sharedMaterial = material;
                var box = piece.AddComponent<BoxCollider>();
                box.size = new Vector3(0.03f, 0.14f, 0.02f);
                var body = piece.AddComponent<Rigidbody>();
                body.mass = 0.1f;
                body.drag = 0.1f;
                body.angularDrag = 0.05f;
                body.isKinematic = true;
                pieces.Add(body);
            }
            broken.Splinters = pieces.ToArray();
            broken.IgnoreColliders = door.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray();

            // the broken wood round the lock, shown once breached
            var splintered = SplinteredWood(door.transform, lockWorld, thickness, material, random);
            if (!created)
                Undo.RegisterCreatedObjectUndo(splintered, "Breach setup");
            broken.On = new[] { splintered };
            broken.Off = new GameObject[0];
            controller.SetActive(false);
            return broken;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // the leaf's renderers in its own space
        private static Bounds LeafBounds(Door door)
        {
            var renderers = door.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
            if (renderers.Count == 0)
                return new Bounds();
            Bounds? bounds = null;
            foreach (var filter in renderers)
            {
                var m = door.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                var b = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, ((i & 4) >> 1) - 1)));
                    if (bounds == null)
                        bounds = new Bounds(corner, Vector3.zero);
                    else
                    {
                        var copy = bounds.Value;
                        copy.Encapsulate(corner);
                        bounds = copy;
                    }
                }
            }
            return bounds.Value;
        }

        // the leaf's edge away from the hinge (the leaf turns about its own position), at hand height
        private static Vector3 FarEdge(Door door, Bounds local)
        {
            float x = Mathf.Abs(local.max.x) > Mathf.Abs(local.min.x) ? local.max.x : local.min.x;
            return door.transform.TransformPoint(new Vector3(x * 0.85f, Mathf.Lerp(local.min.y, local.max.y, 0.48f), local.center.z));
        }

        private static Mesh Splinter() => TarkovGeneratedAssets.Mesh(SplinterMesh, () =>
        {
            // a thin jagged sliver of wood, 15 cm long
            var b = new TarkovGeneratedAssets.MeshBuilder();
            var tip = new Vector3(0.004f, 0.08f, 0f);
            var foot = new Vector3(0f, -0.07f, 0f);
            var l = new Vector3(-0.015f, 0f, 0f);
            var r = new Vector3(0.017f, 0.01f, 0f);
            var f = new Vector3(0f, 0f, 0.008f);
            var k = new Vector3(0f, 0f, -0.008f);
            foreach (var side in new[] { f, k })
            {
                bool front = side == f;
                foreach (var (a, c) in new[] { (l, tip), (tip, r), (r, foot), (foot, l) })
                    if (front)
                        b.Facet(side, c, a);
                    else
                        b.Facet(side, a, c);
            }
            return b.Build("SDK_Splinter");
        });

        // jagged shards standing out of both faces round the lock, under the leaf (they swing with it), off until breached
        private static GameObject SplinteredWood(Transform leaf, Vector3 lockWorld, float thickness, Material material, System.Random random)
        {
            var go = new GameObject("Splintered");
            go.transform.SetParent(leaf, false);
            go.transform.position = lockWorld;
            var b = new TarkovGeneratedAssets.MeshBuilder();
            var lockLocal = go.transform.InverseTransformPoint(lockWorld);
            var toLeaf = go.transform.worldToLocalMatrix * leaf.localToWorldMatrix;
            Vector3 right = toLeaf.MultiplyVector(Vector3.right).normalized, up = toLeaf.MultiplyVector(Vector3.up).normalized, out1 = toLeaf.MultiplyVector(Vector3.forward).normalized;
            for (int face = -1; face <= 1; face += 2)
                for (int i = 0; i < 9; i++)
                {
                    float angle = (float)(random.NextDouble() * Mathf.PI * 2.0);
                    float radius = 0.05f + (float)random.NextDouble() * 0.09f;
                    var basePoint = lockLocal + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius + out1 * face * thickness * 0.5f;
                    var outward = out1 * face * (0.02f + (float)random.NextDouble() * 0.05f) + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * 0.03f;
                    var across = (right * -Mathf.Sin(angle) + up * Mathf.Cos(angle)) * (0.012f + (float)random.NextDouble() * 0.015f);
                    var tip = basePoint + outward;
                    // a double-sided splinter
                    b.Facet(basePoint - across, tip, basePoint + across);
                    b.Facet(basePoint + across, tip, basePoint - across);
                }
            go.AddComponent<MeshFilter>().sharedMesh = SaveSplintered(b.Build("SDK_Splintered"), leaf);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            go.SetActive(false);
            return go;
        }

        // each door's shards are its own mesh, saved next to its scene
        private static Mesh SaveSplintered(Mesh mesh, Transform leaf)
        {
            var scene = leaf.gameObject.scene;
            if (string.IsNullOrEmpty(scene.path))
                return mesh;
            string folder = System.IO.Path.GetDirectoryName(scene.path).Replace('\\', '/') + "/" + scene.name + "_Doors";
            TarkovGeneratedAssets.EnsureFolder(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{leaf.name}_Splintered.asset");
            return TarkovGeneratedAssets.SaveMesh(path, mesh);
        }

        // a burst of wood chips and dust at the lock
        private static ParticleSystem Effect(Transform parent, Vector3 at)
        {
            var go = new GameObject("Effect");
            go.transform.SetParent(parent, false);
            go.transform.position = at;
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 2.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.3f);
            main.startColor = new Color(0.45f, 0.37f, 0.28f, 0.7f);
            main.gravityModifier = 0.6f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 28) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.12f;
            var color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(0f, 1f) });
            color.color = gradient;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = TarkovGeneratedAssets.Material(TarkovGeneratedAssets.EffectsFolder + "/SDK_Dust.mat", TarkovGeneratedAssets.AlphaBlendedShader, m =>
            {
                m.SetTexture("_MainTex", TarkovFire.PuffTexture());
                m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            });
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var broken in roots.SelectMany(r => r.GetComponentsInChildren<BrokenDoor>(true)))
            {
                string where = $"{scene.name}: breachable door '{(broken.transform.parent != null ? broken.transform.parent.name : broken.name)}'";
                // Breach plays it unchecked, and Init ignores collisions with every splinter's collider unchecked
                if (broken.VFX == null)
                    report.Errors.Add($"{where}: its BrokenDoor has no VFX (a ParticleSystem); kicking it in would throw");
                if (broken.Splinters != null && broken.Splinters.Any(s => s != null && s.GetComponentInChildren<Collider>() == null))
                    report.Errors.Add($"{where}: a splinter has no collider; the door would throw when the raid starts");
                if (broken.transform.parent == null || broken.transform.parent.GetComponentInChildren<Door>(true) == null)
                    report.Warnings.Add($"{where}: no Door under the BrokenDoor's parent, so nothing breaks it (the door finds it in its own parent)");
                else if (broken.transform.parent.GetComponentsInChildren<Door>(true).All(d => !d.CanBeBreached))
                    report.Warnings.Add($"{where}: its door has Can Be Breached off, so it can't be kicked in");
                broken.Splinters = (broken.Splinters ?? new Rigidbody[0]).Where(s => s != null).ToArray();
                broken.On = (broken.On ?? new GameObject[0]).Where(g => g != null).ToArray();
                broken.Off = (broken.Off ?? new GameObject[0]).Where(g => g != null).ToArray();
                broken.IgnoreColliders = (broken.IgnoreColliders ?? new Collider[0]).Where(c => c != null).ToArray();
                EditorUtility.SetDirty(broken);
            }
        }
    }
}
