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
    //  Low + High Poly Colliders  splits an object's collision the way retail maps do: movement
    //          on LowPolyCollider, bullets and AI sight on HighPolyCollider with a BallisticCollider
    public static class TarkovPrefabTools
    {
        private const string Menu = "GameObject/Tarkov SDK/";

        [MenuItem(Menu + "Door", priority = 30)]
        private static void MenuDoor(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateDoor(p, 1f, 2.1f).gameObject);

        [MenuItem(Menu + "Switch", priority = 31)]
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

        [MenuItem(Menu + "Loot Spawn", priority = 32)]
        private static void MenuLootSpawn(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var go = new GameObject("LootSpawn") { tag = "EditorOnly" };
            if (p != null)
                go.transform.SetParent(p, false);
            go.AddComponent<TarkovLootSpawn>();
            return go;
        });

        // ------------------------------------------------------------ door

        public static Door CreateDoor(Transform parent, float width, float height)
        {
            var root = new GameObject("Door");
            if (parent != null)
                root.transform.SetParent(parent, false);

            // the leaf rotates around its own Y axis at the hinge (its left edge)
            var leaf = new GameObject("DoorLeaf") { layer = LayerMask.NameToLayer("Interactive") };
            leaf.transform.SetParent(root.transform, false);
            leaf.transform.localPosition = new Vector3(-width * 0.5f, 0f, 0f);

            var interaction = leaf.AddComponent<BoxCollider>();
            interaction.isTrigger = true;
            interaction.center = new Vector3(width * 0.55f, height * 0.5f, 0f);
            interaction.size = new Vector3(width * 0.7f, 1.2f, 0.3f);

            var panelSize = new Vector3(width, height, 0.05f);
            var panelCenter = new Vector3(width * 0.5f, height * 0.5f, 0f);

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
            handle.transform.localPosition = new Vector3(width * 0.88f, 1.0f, 0f);
            var doorHandle = handle.AddComponent<DoorHandle>();

            var door = leaf.AddComponent<Door>();
            door.DoorAxis = WorldInteractiveObject.EDoorAxis.Y;
            door.DoorForward = WorldInteractiveObject.EDoorAxis.Z;
            door.OpenAngle = 95f;
            door.CloseAngle = 0f;
            door._doorState = EDoorState.Shut;
            door.Snap = EDoorState.Locked | EDoorState.Shut;
            door.Operatable = true;
            door.CanBeBreached = true;
            door.CanInteractWithBreach = true;
            door._handle = doorHandle;
            // where the player stands on either side, and what they look at, in the leaf's space
            door.interactPosition1 = new Vector3(width * 0.55f, 0f, -0.8f);
            door.interactPosition2 = new Vector3(width * 0.55f, 0f, 0.8f);
            door.viewTarget1 = new Vector3(width * 0.88f, 1.0f, 0f);
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
            sw.NextSwitches = new Switch.SwitchAndOperation[0];
            sw.ConditionStatus = new EExfiltrationStatus[0];
            return sw;
        }

        // ------------------------------------------------------------ colliders

        [MenuItem("Tarkov SDK/Collision/Add Low + High Poly Colliders To Selected", priority = 42)]
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
