using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Labs' keycard doors (Laboratory_parking), from the game's components:
    //
    //  - KeycardDoor: a Door whose KeyId is a keycard's item id, starting locked. Unlocking plays the
    //    reader's swipe, then GrantedBeep and UnlockSound (or DeniedBeep and the reader's flicker).
    //  - Card reader: an InteractiveProxy on the Interactive layer beside the door. Looking at it uses the
    //    door, with the card-swipe animation (DoorCardOpen). The game reads its Proxies[0] unchecked, and
    //    the proxy's Start needs both lights, so a keycard door always has one.
    //    Lock: the DoorHandle the card slides along; KeyGrip: where the hand goes; StatusLight turns green
    //    once unlocked; FlickerLight flashes on a wrong card.
    public static class TarkovKeycardDoors
    {
        // TerraGroup Labs keycard (Yellow), retail's parking door
        public const string DefaultKeycard = "5c1d0d6d86f7744bb2683e1f";

        [MenuItem("GameObject/Tarkov SDK/Interactables/Keycard Door", priority = 27)]
        private static void MenuKeycardDoor(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateKeycardDoor(p).transform.parent.gameObject);

        public static KeycardDoor CreateKeycardDoor(Transform parent)
        {
            const float width = 1f;
            var door = (KeycardDoor)TarkovPrefabTools.CreateDoor(parent, width, 2.1f, keycard: true);
            door.transform.parent.name = "KeycardDoor";
            door.KeyId = DefaultKeycard;
            door._doorState = EDoorState.Locked;
            door._additionalKeys = new string[0];
            door._openOnUnlock = false;
            door._lockOnShut = false;
            // on the wall beside the latch, facing the same way as the door's front (-Z)
            var proxy = CreateCardReader(door.transform.parent, door);
            proxy.transform.parent.localPosition = new Vector3(width * 0.5f + 0.25f, 1.3f, -0.08f);
            door.Proxies = new[] { proxy };
            return door;
        }

        public static InteractiveProxy CreateCardReader(Transform parent, KeycardDoor door)
        {
            var reader = TarkovFacilitySystems.Child(parent, "CardReader", Vector3.zero);
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Reader (replace with your model)";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(reader.transform, false);
            visual.transform.localScale = new Vector3(0.08f, 0.14f, 0.03f);
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            if (material != null)
                visual.GetComponent<MeshRenderer>().sharedMaterial = material;

            // what the player's interaction ray hits; retail's is 0.15 x 0.2 x 0.21 m
            var proxyObject = TarkovFacilitySystems.Child(reader.transform, "proxy", Vector3.zero, "Interactive");
            var box = proxyObject.AddComponent<BoxCollider>();
            box.size = new Vector3(0.15f, 0.2f, 0.12f);
            box.center = new Vector3(0f, 0f, -0.04f);

            var lockObject = TarkovFacilitySystems.Child(proxyObject.transform, "Lock", new Vector3(0f, 0.05f, -0.03f));
            var handle = lockObject.AddComponent<DoorHandle>();
            handle.pos = true;
            handle.DefaultPosition = lockObject.transform.localPosition;
            handle.OpenPosition = handle.DefaultPosition + new Vector3(0f, -0.1f, 0f);
            handle.DefaultRotation = Quaternion.identity;
            handle.OpenRotation = Quaternion.identity;
            handle.OpenAnimation = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.145f, 1f), new Keyframe(0.273f, 1f), new Keyframe(0.4f, 0f));
            handle.LockedAnimation = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.4f, 1f));
            handle.DownSound = new AudioClip[0];
            handle.UpSound = new AudioClip[0];

            var gripObject = TarkovFacilitySystems.Child(lockObject.transform, "KeyGrip", new Vector3(0f, 0f, -0.02f));
            var grip = gripObject.AddComponent<GripPose>();
            grip.DoorState = EDoorState.Locked;
            grip.Hand = GripPose.EHand.Left;
            grip.GripType = GripPose.EGripType.Common;
            grip.FingerTransforms = new Transform[0];
            grip.Fingers = new Quaternion[0];

            var proxy = proxyObject.AddComponent<InteractiveProxy>();
            proxy.Link = door;
            proxy.Grips = new[] { grip };
            proxy.Handle = handle;
            proxy.StatusLight = ReaderLight(proxyObject.transform, "StatusLight", new Vector3(0.025f, -0.02f, -0.03f));
            proxy.FlickerLight = ReaderLight(proxyObject.transform, "FlickerLight", new Vector3(0.025f, -0.02f, -0.035f));
            // retail: three red flashes over 1.2 s
            proxy.FlickCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.273f, 2f), new Keyframe(0.33f, 0f), new Keyframe(0.514f, 0f),
                                                  new Keyframe(0.732f, 2f), new Keyframe(0.796f, 0f), new Keyframe(0.984f, 0f), new Keyframe(1.179f, 2f),
                                                  new Keyframe(1.219f, 0f));
            // in the proxy's space: what the player looks at, and where they stand (its height is the player's)
            proxy._viewTarget = Vector3.zero;
            proxy._interactionPosition = new Vector3(0f, 0f, -0.6f);
            return proxy;
        }

        // retail's: a 10 cm red point light at intensity 2, faded out from 15 to 25 m
        private static Light ReaderLight(Transform parent, string name, Vector3 localPosition)
        {
            var light = TarkovLights.CreateLight(parent, LightType.Point, name);
            light.transform.localPosition = localPosition;
            light.color = new Color(1f, 0.192f, 0.192f);
            light.range = 0.1f;
            light.intensity = 2f;
            light.shadows = LightShadows.None;
            var culling = light.GetComponent<CullingLightObject>();
            culling.CullDistance = 25f;
            culling._fadeStartDistance = 15f;
            culling._fadeEndDistance = 25f;
            culling._maxLightIntensity = 2f;
            culling._initialShadowsMode = LightShadows.None;
            return light;
        }

        // InteractiveProxy.OnValidate, which only runs in BSG's editor: its grips and handle from its children
        public static bool RefreshProxy(InteractiveProxy proxy)
        {
            var grips = proxy.GetComponentsInChildren<GripPose>(true);
            var handle = proxy.GetComponentInChildren<DoorHandle>(true);
            if (proxy.Grips != null && proxy.Grips.SequenceEqual(grips) && proxy.Handle == handle)
                return false;
            proxy.Grips = grips;
            proxy.Handle = handle;
            return true;
        }
    }
}
