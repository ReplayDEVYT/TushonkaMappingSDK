using System.Linq;
using EFT.Ballistics;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Physics props: Arena's small objects (bottles, cans, cups, monitors, boxes) that players push by
    // walking into them, bullets knock over and grenades throw, with impact sounds. The game's
    // SmallPhysicsObject does it all; its SmallPhysicsSystem creates itself.
    //
    //  - the prop rests (settled and asleep) until disturbed: a player's touch (an overlap check against its collider,
    //    since the Loot layer never collides with players), a bullet hit (its BallisticCollider) or a
    //    grenade within BlowUpThreshold metres
    //  - its impact sounds are the game's, picked by SoundType from weaponsounds.bundle; Books and
    //    CardBoard are Arena-only and empty in Tarkov, so they are not offered
    //  - optionally it swaps a Solid model for a Fragmented one when first disturbed (a shattering
    //    monitor), emits a game particle effect, and disappears after TimeOfLife seconds
    //
    // Layout: the Rigidbody on the root; a collision shape on the Loot layer (it lands on Terrain,
    // HighPolyCollider and Default geometry, like dropped loot); the same shape on HighPolyCollider with
    // the BallisticCollider, which bullets hit. Colliders of one Rigidbody never collide with each other.
    public static class TarkovPhysicsProps
    {
        // the sound types Tarkov has sounds for, with the bullet material that suits each
        public static readonly (SmallPhysicsObject.EPhysicsObjectType Type, MaterialType Bullets, BaseBallistic.ESurfaceSound Impact)[] Types =
        {
            (SmallPhysicsObject.EPhysicsObjectType.MediumGeneric, MaterialType.GenericHard, BaseBallistic.ESurfaceSound.Plastic),
            (SmallPhysicsObject.EPhysicsObjectType.SmallBox, MaterialType.Cardboard, BaseBallistic.ESurfaceSound.Garbage),
            (SmallPhysicsObject.EPhysicsObjectType.SmallPaper, MaterialType.GarbagePaper, BaseBallistic.ESurfaceSound.Garbage),
            (SmallPhysicsObject.EPhysicsObjectType.SmallPlastic, MaterialType.Plastic, BaseBallistic.ESurfaceSound.Plastic),
            (SmallPhysicsObject.EPhysicsObjectType.HardPlastic, MaterialType.Plastic, BaseBallistic.ESurfaceSound.Plastic),
            (SmallPhysicsObject.EPhysicsObjectType.Cup, MaterialType.Plastic, BaseBallistic.ESurfaceSound.Plastic),
            (SmallPhysicsObject.EPhysicsObjectType.CoolerBottle, MaterialType.Plastic, BaseBallistic.ESurfaceSound.Plastic),
            (SmallPhysicsObject.EPhysicsObjectType.GlassBottle, MaterialType.Glass, BaseBallistic.ESurfaceSound.Glass),
            (SmallPhysicsObject.EPhysicsObjectType.TincanEmpty, MaterialType.MetalThin, BaseBallistic.ESurfaceSound.MetalThin),
            (SmallPhysicsObject.EPhysicsObjectType.MetalCandlestick, MaterialType.MetalThin, BaseBallistic.ESurfaceSound.MetalThin),
            (SmallPhysicsObject.EPhysicsObjectType.Monitor, MaterialType.Plastic, BaseBallistic.ESurfaceSound.Plastic),
        };

        public static string[] TypeNames => Types.Select(t => ObjectNames.NicifyVariableName(t.Type.ToString())).ToArray();

        // How fast a push sends the prop, whatever it weighs. The game applies each push as an impulse
        // of direction x multiplier (N·s; an explosion's divided by its distance), so a multiplier is a
        // speed times the mass: the same multiplier would send a can flying and leave a monitor still.
        public const float ShotSpeed = 3f;          // m/s
        public const float TouchSpeed = 1.5f;       // m/s, while a player keeps walking into it
        public const float ExplosionSpeed = 12f;    // m/s at 1 m from the blast, less further out

        public static void SetPushSpeeds(SmallPhysicsObject prop, float shot, float touch, float explosion)
        {
            float mass = prop.Rigidbodies.Count > 0 && prop.Rigidbodies[0] != null ? prop.Rigidbodies[0].mass : 1f;
            prop.OnHitForceMultiplier = shot * mass;
            prop.OnTouchForceMultiplier = touch * mass;
            prop.OnBlowUpForceMultiplier = explosion * mass;
        }

        [MenuItem("GameObject/Tarkov SDK/Interactables/Physics Prop", priority = 35)]
        private static void MenuProp(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            // a small placeholder box, sounding like a generic object
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.name = "Box (replace with your model)";
            visual.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            visual.transform.localPosition = new Vector3(0f, 0.15f, 0f);
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            if (material != null)
                visual.GetComponent<MeshRenderer>().sharedMaterial = material;
            var prop = CreateProp(p, visual, null, 0, 2f, false);
            prop.name = "PhysicsProp";
            return prop.gameObject;
        });

        // model: the visual, reparented under the prop; fragmented: optional broken version, shown when
        // first disturbed. typeIndex indexes Types. convex: a convex mesh collider instead of a box.
        public static SmallPhysicsObject CreateProp(Transform parent, GameObject model, GameObject fragmented, int typeIndex, float mass, bool convex)
        {
            var (type, bullets, impact) = Types[Mathf.Clamp(typeIndex, 0, Types.Length - 1)];
            var root = new GameObject(model.name);
            if (parent != null)
                root.transform.SetParent(parent, false);
            model.transform.SetParent(root.transform, false);

            var bounds = LocalBounds(root.transform, model);
            var body = root.AddComponent<Rigidbody>();
            body.mass = Mathf.Max(0.05f, mass);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // small and fast when shot
            // physical, not kinematic: the game only unfreezes a kinematic body it finds asleep, so
            // a dynamic one is the sure way; it settles where placed and sleeps until pushed
            body.isKinematic = false;

            Shape(root.transform, "Collision", "Loot", model, bounds, convex);
            var hitbox = Shape(root.transform, "Hitbox", "HighPolyCollider", model, bounds, convex);
            var ballistic = hitbox.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = bullets;
            ballistic.SurfaceSound = impact;

            var prop = root.AddComponent<SmallPhysicsObject>();
            prop.SoundType = type;
            prop.SolidObject = fragmented != null ? model : null;
            if (fragmented != null)
            {
                fragmented.transform.SetParent(root.transform, false);
                fragmented.SetActive(false);
                prop.FragmentedObject = fragmented;
            }
            prop.BallisticColliders.Add(ballistic);
            prop.Rigidbodies.Add(body);
            prop.Collider = hitbox.GetComponent<Collider>();
            SetPushSpeeds(prop, ShotSpeed, TouchSpeed, ExplosionSpeed);
            return prop;
        }

        private static GameObject Shape(Transform parent, string name, string layer, GameObject model, Bounds bounds, bool convex)
        {
            var go = new GameObject(name) { layer = LayerMask.NameToLayer(layer) };
            go.transform.SetParent(parent, false);
            var filter = model.GetComponentInChildren<MeshFilter>();
            if (convex && filter != null && filter.sharedMesh != null)
            {
                // follows the model's first mesh, placed like it
                go.transform.SetPositionAndRotation(filter.transform.position, filter.transform.rotation);
                go.transform.localScale = filter.transform.lossyScale;
                var mesh = go.AddComponent<MeshCollider>();
                mesh.sharedMesh = filter.sharedMesh;
                mesh.convex = true; // a Rigidbody needs convex mesh colliders
            }
            else
            {
                var box = go.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = Vector3.Max(bounds.size, Vector3.one * 0.02f);
            }
            return go;
        }

        // the model's renderer bounds in the root's space (the root is unrotated and unscaled here)
        private static Bounds LocalBounds(Transform root, GameObject model)
        {
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.one * 0.2f);
            var b = renderers[0].bounds;
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            b.center = root.InverseTransformPoint(b.center);
            return b;
        }
    }
}
