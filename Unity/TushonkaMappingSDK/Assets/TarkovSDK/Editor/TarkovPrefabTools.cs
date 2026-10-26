using EFT.Ballistics;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Ready-made interactive objects and collider setups, laid out like retail ones:
    //
    //  Door    leaf on the Interactive layer, pivoting at its hinge; a trigger box for interaction,
    //          a HighPolyCollider (bullets, with BallisticCollider) and a DoorLowPolyCollider
    //          (movement), a DoorHandle and a placeholder panel to replace with a real model
    //  Switch  small solid box on the Interactive layer; set its Door to open a door, or Lamps
    //  Lootable Container  a wooden crate on the Interactive layer with an interaction box,
    //          high and low poly colliders and a placeholder model; Template sets the container type
    //  Low + High Poly Colliders  splits an object's collision the way retail maps do: movement
    //          on LowPolyCollider, bullets and AI sight on HighPolyCollider with a BallisticCollider
    public static class TarkovPrefabTools
    {
        private const string Menu = "GameObject/Tarkov SDK/";

        [MenuItem(Menu + "Interactables/Door", priority = 27)]
        private static void MenuDoor(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateDoor(p, 1f, 2.1f).gameObject);

        [MenuItem(Menu + "Interactables/Switch", priority = 29)]
        private static void MenuSwitch(MenuCommand cmd)
        {
            // right-clicking a door makes a switch that opens it
            var door = (cmd.context as GameObject)?.GetComponentInChildren<Door>();
            if (door == null)
            {
                TarkovSceneTools.Place(cmd, p => CreateSwitch(p, null).gameObject);
                return;
            }
            // beside the door's hinge, at hand height
            var sw = CreateSwitch(null, door);
            sw.transform.position = door.transform.position - door.transform.right * 0.4f + Vector3.up * 1.2f;
            sw.transform.rotation = door.transform.rotation;
            Undo.RegisterCreatedObjectUndo(sw.gameObject, "Create Switch");
            Selection.activeGameObject = sw.gameObject;
        }

        [MenuItem(Menu + "Spawns/Loot Spawn", priority = 12)]
        private static void MenuLootSpawn(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var go = new GameObject("LootSpawn") { tag = "EditorOnly" };
            if (p != null)
                go.transform.SetParent(p, false);
            go.AddComponent<TarkovLootSpawn>();
            return go;
        });

        [MenuItem(Menu + "Interactables/Lootable Container", priority = 30)]
        private static void MenuContainer(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateContainer(p).gameObject);

        // ------------------------------------------------------------ door

        // hingeRight puts the hinge on the right edge (seen from -Z); reverseSwing opens it the other way
        public static Door CreateDoor(Transform parent, float width, float height, float thickness = 0.05f, bool hingeRight = false, bool reverseSwing = false,
                                      bool keycard = false)
        {
            var root = new GameObject("Door");
            if (parent != null)
                root.transform.SetParent(parent, false);
            float side = hingeRight ? -1f : 1f; // the leaf extends along +X from a left hinge, -X from a right one

            // the leaf rotates around its own Y axis at the hinge
            var leaf = new GameObject("DoorLeaf") { layer = LayerMask.NameToLayer("Interactive") };
            leaf.transform.SetParent(root.transform, false);
            leaf.transform.localPosition = new Vector3(-side * width * 0.5f, 0f, 0f);

            var interaction = leaf.AddComponent<BoxCollider>();
            interaction.isTrigger = true;
            interaction.center = new Vector3(side * width * 0.55f, height * 0.5f, 0f);
            interaction.size = new Vector3(width * 0.7f, 1.2f, Mathf.Max(0.3f, thickness + 0.2f));

            var panelSize = new Vector3(width, height, thickness);
            var panelCenter = new Vector3(side * width * 0.5f, height * 0.5f, 0f);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Panel (replace with your model)";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(leaf.transform, false);
            visual.transform.localPosition = panelCenter;
            visual.transform.localScale = panelSize;
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            if (material != null)
                visual.GetComponent<MeshRenderer>().sharedMaterial = material;

            var high = BoxChild(leaf.transform, "HighPoly", "HighPolyCollider", panelCenter, panelSize);
            var ballistic = high.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.WoodThin;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.Wood;
            BoxChild(leaf.transform, "LowPoly", "DoorLowPolyCollider", panelCenter, panelSize);

            var handle = new GameObject("Handle");
            handle.transform.SetParent(leaf.transform, false);
            handle.transform.localPosition = new Vector3(side * width * 0.88f, 1.0f, 0f);
            var doorHandle = handle.AddComponent<DoorHandle>();

            var door = keycard ? leaf.AddComponent<KeycardDoor>() : leaf.AddComponent<Door>();
            door.DoorAxis = WorldInteractiveObject.EDoorAxis.Y;
            door.DoorForward = WorldInteractiveObject.EDoorAxis.Z;
            // positive swings a left-hinged leaf towards -Z; a right hinge needs the opposite to swing the same way
            door.OpenAngle = 95f * side * (reverseSwing ? -1f : 1f);
            door.CloseAngle = 0f;
            door._doorState = EDoorState.Shut;
            door.Snap = EDoorState.Locked | EDoorState.Shut;
            door.Operatable = true;
            door.CanBeBreached = true;
            door.CanInteractWithBreach = true;
            door._handle = doorHandle;
            // where the player stands on either side, and what they look at, in the leaf's space
            door.interactPosition1 = new Vector3(side * width * 0.55f, 0f, -0.8f);
            door.interactPosition2 = new Vector3(side * width * 0.55f, 0f, 0.8f);
            door.viewTarget1 = new Vector3(side * width * 0.88f, 1.0f, 0f);
            door.KickCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            // Id is assigned when the map is built
            return door;
        }

        private static GameObject BoxChild(Transform parent, string name, string layer, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name) { layer = LayerMask.NameToLayer(layer) };
            go.transform.SetParent(parent, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size;
            return go;
        }

        // ------------------------------------------------------------ switch

        public static Switch CreateSwitch(Transform parent, Door door)
        {
            var go = new GameObject(door != null ? "Switch_" + door.transform.parent.name : "Switch") { layer = LayerMask.NameToLayer("Interactive") };
            if (parent != null)
                go.transform.SetParent(parent, false);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(0.2f, 0.2f, 0.2f);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Button (replace with your model)";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(go.transform, false);
            visual.transform.localScale = new Vector3(0.12f, 0.18f, 0.05f);

            var sw = go.AddComponent<Switch>();
            sw.interactWithoutAnimation = true;
            sw.DontAnimateRotation = true;
            sw.Snap = EDoorState.Shut | EDoorState.Open | EDoorState.Interacting;
            sw._doorState = EDoorState.Shut;
            sw.Operatable = true;
            sw.ContextMenuTip = door != null ? "Open" : "toggle_light";
            sw.Door = door;
            sw.Interaction = EFT.EInteractionType.Open;
            sw.Lamps = new LampController[0];
            // the game animates a switch by this curve and reads its keys unchecked: empty, interacting throws
            sw.CustomProgressCurve = SwitchCurve();
            sw.NextSwitches = new Switch.SwitchAndOperation[0];
            sw.ConditionStatus = new EExfiltrationStatus[0];
            return sw;
        }

        // ------------------------------------------------------------ container

        // "Wooden crate" (5x2): one of the container types whose loot pools the server mod takes from Factory
        public const string WoodenCrateTemplate = "578f87ad245977356274f2cc";

        public static LootableContainer CreateContainer(Transform parent)
        {
            var size = new Vector3(1f, 0.6f, 0.6f);
            var center = new Vector3(0f, size.y * 0.5f, 0f);
            var go = new GameObject("LootableContainer") { layer = LayerMask.NameToLayer("Interactive") };
            if (parent != null)
                go.transform.SetParent(parent, false);
            // the player's interaction ray looks for this box; it stands clear of the HighPoly box (see EncloseHighPoly)
            var box = go.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size + Vector3.one * (2f * InteractMargin);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Crate (replace with your model)";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(go.transform, false);
            visual.transform.localPosition = center;
            visual.transform.localScale = size;
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            if (material != null)
                visual.GetComponent<MeshRenderer>().sharedMaterial = material;

            var high = BoxChild(go.transform, "HighPoly", "HighPolyCollider", center, size);
            var ballistic = high.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.WoodThick;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.Wood;
            var low = BoxChild(go.transform, "LowPoly", "LowPolyCollider", center, size);
#pragma warning disable 618
            GameObjectUtility.SetStaticEditorFlags(low, StaticEditorFlags.NavigationStatic);
#pragma warning restore 618

            var container = go.AddComponent<LootableContainer>();
            container.Template = WoodenCrateTemplate;
            container.SpawnType = ELootableContainerSpawnType.CanBeEmpty;
            container.SpawnChance = 100;
            container._openInteraction = EInteraction.ContainerOpenDefault;
            container._closeInteraction = EInteraction.ContainerCloseDefault;
            container._doorState = EDoorState.Shut;
            container.Snap = EDoorState.Shut | EDoorState.Open | EDoorState.Interacting;
            container.Operatable = true;
            // the player searches it from the front (-Z), looking at its middle
            container.interactPosition1 = new Vector3(0f, 0f, -0.9f);
            container.viewTarget1 = center;
            container.FilterExtended = new System.Collections.Generic.List<LootableContainerParameters>();
            container.GameObjectsToDestroy = new System.Collections.Generic.List<GameObject>();
            // Id, ClosedPosition and OpenPosition are set when the map is built
            return container;
        }

        // How far a container's interaction box stands out from its HighPoly colliders.
        public const float InteractMargin = 0.05f;

        // GameWorld.FindInteractable drops what the player looks at when a HighPolyCollider lies between
        // the eye and the hit point. A HighPoly box flush with the interaction box makes that test flip
        // frame to frame (the prompt flickers), so the interaction box is grown to enclose them.
        public static bool EncloseHighPoly(BoxCollider box)
        {
            var t = box.transform;
            int highPoly = LayerMask.NameToLayer("HighPolyCollider");
            bool any = false;
            var inner = new Bounds();
            foreach (var c in t.GetComponentsInChildren<Collider>(true))
            {
                if (c.gameObject.layer != highPoly)
                    continue;
                foreach (var corner in Corners(c))
                {
                    var p = t.InverseTransformPoint(corner);
                    if (!any)
                        inner = new Bounds(p, Vector3.zero);
                    inner.Encapsulate(p);
                    any = true;
                }
            }
            if (!any)
                return false;
            var s = t.lossyScale;
            var margin = new Vector3(InteractMargin / Mathf.Max(1e-4f, Mathf.Abs(s.x)), InteractMargin / Mathf.Max(1e-4f, Mathf.Abs(s.y)),
                                     InteractMargin / Mathf.Max(1e-4f, Mathf.Abs(s.z)));
            var current = new Bounds(box.center, box.size);
            // already clear by at least a fifth of the margin on every side
            var needed = new Bounds(inner.center, inner.size + margin * 0.4f);
            if (current.Contains(needed.min) && current.Contains(needed.max))
                return false;
            current.Encapsulate(new Bounds(inner.center, inner.size + margin * 2f));
            box.center = current.center;
            box.size = current.size;
            return true;
        }

        private static Vector3[] Corners(Collider c)
        {
            var corners = new Vector3[8];
            if (c is BoxCollider b)
            {
                for (int i = 0; i < 8; i++)
                    corners[i] = b.transform.TransformPoint(b.center + Vector3.Scale(b.size * 0.5f,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                return corners;
            }
            var w = c.bounds;
            for (int i = 0; i < 8; i++)
                corners[i] = w.center + Vector3.Scale(w.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            return corners;
        }

        // Opening a container moves it between ClosedPosition and OpenPosition (made for drawers), so
        // left at zero it jumps to its parent's origin and its interaction state breaks. One that
        // doesn't slide (both equal) gets its current position.
        public static bool KeepContainerInPlace(LootableContainer container)
        {
            var here = container.transform.localPosition;
            if (container.ClosedPosition != container.OpenPosition || container.ClosedPosition == here)
                return false;
            container.ClosedPosition = here;
            container.OpenPosition = here;
            return true;
        }

        // the curve most retail switches use: linear, over one second
        public static AnimationCurve SwitchCurve() => AnimationCurve.Linear(0f, 0f, 1f, 1f);

        // ------------------------------------------------------------ colliders

        [MenuItem("Tarkov SDK/Tools/Geometry/Add Low + High Poly Colliders To Selected", priority = 120)]
        private static void MenuLowHighPoly()
        {
            if (Selection.gameObjects.Length == 0)
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Select the objects (or their parents) to give collision.", "OK");
                return;
            }
            int choice = EditorUtility.DisplayDialogComplex("Low + High Poly Colliders",
                "High poly (bullets, AI sight) always uses the exact mesh. What shape should the low poly (movement) collider be?\n\n" +
                "Box is cheapest and smoothest to walk along; Mesh follows the model exactly.",
                "Box", "Cancel", "Mesh");
            if (choice == 1)
                return;
            bool box = choice == 0;
            int count = 0;
            foreach (var root in Selection.gameObjects)
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    if (AddLowHigh(filter, box))
                        count++;
            Debug.Log($"[Tarkov SDK] Low + high poly colliders added to {count} mesh(es).");
        }

        public static bool AddLowHigh(MeshFilter filter, bool boxLowPoly)
        {
            var mesh = filter.sharedMesh;
            var renderer = filter.GetComponent<MeshRenderer>();
            if (mesh == null || renderer == null || filter.name.EndsWith("_HighPoly") || filter.name.EndsWith("_LowPoly"))
                return false;
            var go = filter.gameObject;
            Undo.RegisterFullObjectHierarchyUndo(go, "Low + high poly colliders");

            // collision moves to the two children
            foreach (var old in go.GetComponents<Collider>())
                if (!old.isTrigger)
                    Undo.DestroyObjectImmediate(old);
            var oldBallistic = go.GetComponent<BallisticCollider>();
            if (oldBallistic != null)
                Undo.DestroyObjectImmediate(oldBallistic);
            foreach (Transform child in go.transform)
                if (child.name == go.name + "_HighPoly" || child.name == go.name + "_LowPoly")
                    Undo.DestroyObjectImmediate(child.gameObject);

            var high = new GameObject(go.name + "_HighPoly") { layer = LayerMask.NameToLayer("HighPolyCollider") };
            high.transform.SetParent(go.transform, false);
            high.AddComponent<MeshCollider>().sharedMesh = mesh;
            var (type, sound) = TarkovModelConverter.Surface(renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "");
            var ballistic = high.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = type;
            ballistic.SurfaceSound = sound;

            var low = new GameObject(go.name + "_LowPoly") { layer = LayerMask.NameToLayer("LowPolyCollider") };
            low.transform.SetParent(go.transform, false);
            if (boxLowPoly)
            {
                var b = low.AddComponent<BoxCollider>();
                b.center = mesh.bounds.center;
                b.size = mesh.bounds.size;
            }
            else
            {
                low.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            // both stay static and in the navmesh bake like the visual mesh
#pragma warning disable 618
            var flags = StaticEditorFlags.NavigationStatic | StaticEditorFlags.BatchingStatic;
#pragma warning restore 618
            GameObjectUtility.SetStaticEditorFlags(high, flags);
            GameObjectUtility.SetStaticEditorFlags(low, flags);
            Undo.RegisterCreatedObjectUndo(high, "High poly collider");
            Undo.RegisterCreatedObjectUndo(low, "Low poly collider");
            return true;
        }
    }
}
