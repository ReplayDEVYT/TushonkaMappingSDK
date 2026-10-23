using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Ballistics;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Builds working doors, lamps, lootable containers and switches from your own model, materials and
    // sounds. Each is the SDK's ready-made object (GameObject > Tarkov SDK) with its placeholder replaced
    // by the model and its colliders fitted to the model's bounds; optionally saved as a prefab to reuse.
    //
    // Lamps can switch their look with the light, in one of the game's three ways:
    //  Swap Materials  two copies of the model, one with the "on" material and one with the "off"
    //                  material; the lamp shows one or the other (LampController On/OffObjects). Any shader.
    //  Fade Emission   the lamp fades the material's _EmissionVisibility, flicker included
    //                  (MaterialEmission); only the game's emissive shaders have it, e.g.
    //                  p0/Reflective/Bumped Emissive Specular SMap.
    //  Emission Color  the lamp sets a colour property to an "on" or "off" colour (MaterialColor), e.g.
    //                  _EmissionColor on Unity's Standard shader.
    public class TarkovObjectBuilderWindow : EditorWindow
    {
        private enum Kind { Door, Lamp, LootableContainer, Switch }
        private enum Emission { None, SwapMaterials, FadeEmission, EmissionColor }

        // container types whose loot pools the server mod has (taken from Factory)
        private static readonly (string Name, string Template)[] Containers =
        {
            ("Wooden crate", TarkovPrefabTools.WoodenCrateTemplate), ("Drawer", "578f87b7245977356274f2cd"),
            ("Jacket", "578f8778245977358849a9b5"), ("Duffle bag", "578f87a3245977356274f2cb"), ("Toolbox", "5909d50c86f774659e6aaebe"),
            ("Medbag SMU06", "5909d24f86f77466f56e6855"), ("Grenade box", "5909d36d86f774660f0bb900"), ("Safe", "578f8782245977354405a1e3"),
            ("Weapon box (5x2)", "5909d5ef86f77467974efbd8"), ("Weapon box (6x3)", "5909d76c86f77471e53d2adf"),
            ("Technical supply crate", "5d6fd45b86f774317075ed43"),
        };

        private Kind _kind;
        private string _name = "";
        private GameObject _model;
        private Material _materialOverride;
        private bool _keepModelColliders;
        private MaterialType _bulletMaterial = MaterialType.WoodThin;
        private BaseBallistic.ESurfaceSound _surfaceSound = BaseBallistic.ESurfaceSound.Wood;
        private readonly List<AudioClip> _openSounds = new List<AudioClip>(), _closeSounds = new List<AudioClip>(), _squeakSounds = new List<AudioClip>();
        // door
        private bool _hingeRight, _reverseSwing;
        private string _keyId = "";
        // lamp
        private bool _spot;
        private Color _lightColor = new Color(1f, 0.86f, 0.68f);
        private float _intensity = 1.4f, _range = 10f;
        private bool _nightOnly, _addFlare;
        private Emission _emission;
        private Material _onMaterial, _offMaterial;
        private int _materialSlot = -1;
        private string _colorProperty = "_EmissionColor";
        private Color _colorOn = Color.white, _colorOff = Color.black;
        private readonly List<AudioClip> _turnOnSounds = new List<AudioClip>();
        private AudioClip _turnOffSound, _humLoop, _flickerLoop;
        // container
        private int _container;
        private string _customTemplate = "";
        // switch
        private Door _switchDoor;
        private LampController _switchLamp;
        // output
        private bool _savePrefab;
        private string _prefabFolder = "Assets/Prefabs";
        private Vector2 _scroll;

        [MenuItem("Tarkov SDK/Tools/Interactive Object Builder...", priority = 200)]
        private static void Open() => GetWindow<TarkovObjectBuilderWindow>("Object Builder").minSize = new Vector2(380f, 420f);

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Make a working Tarkov object from your own model, materials and sounds.", EditorStyles.wordWrappedMiniLabel);
            var kind = (Kind)EditorGUILayout.EnumPopup("Object", _kind);
            if (kind != _kind)
            {
                _kind = kind;
                // sensible surfaces: wooden doors and crates, glass lamps, metal switches
                (_bulletMaterial, _surfaceSound) = kind switch
                {
                    Kind.Lamp => (MaterialType.Glass, BaseBallistic.ESurfaceSound.Glass),
                    Kind.Switch => (MaterialType.MetalThin, BaseBallistic.ESurfaceSound.MetalThin),
                    _ => (MaterialType.WoodThin, BaseBallistic.ESurfaceSound.Wood),
                };
            }
            _name = EditorGUILayout.TextField(new GUIContent("Name", "Empty: the model's name."), _name);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Model", EditorStyles.boldLabel);
            _model = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Model", "A model or prefab asset, or an object in the scene. " +
                                                                                      "It is copied; its pivot doesn't matter, the object is fitted to its bounds."), _model, typeof(GameObject), true);
            _materialOverride = (Material)EditorGUILayout.ObjectField(new GUIContent("Material Override", "Optional: replaces every material on the model."), _materialOverride, typeof(Material), false);
            _keepModelColliders = EditorGUILayout.Toggle(new GUIContent("Keep Model Colliders", "Off: the model's own colliders are removed; the object gets fitted ones."), _keepModelColliders);
            _bulletMaterial = (MaterialType)EditorGUILayout.EnumPopup(new GUIContent("Bullet Material", "What bullets hit: penetration and impact effects."), _bulletMaterial);
            _surfaceSound = (BaseBallistic.ESurfaceSound)EditorGUILayout.EnumPopup("Impact Sound", _surfaceSound);

            EditorGUILayout.Space();
            switch (_kind)
            {
                case Kind.Door: DrawDoor(); break;
                case Kind.Lamp: DrawLamp(); break;
                case Kind.LootableContainer: DrawContainer(); break;
                case Kind.Switch: DrawSwitch(); break;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            _savePrefab = EditorGUILayout.Toggle(new GUIContent("Also Save As Prefab", "Place the object and save it as a prefab, to place more copies later."), _savePrefab);
            if (_savePrefab)
                _prefabFolder = EditorGUILayout.TextField("Prefab Folder", _prefabFolder);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_model == null))
                if (GUILayout.Button(_model == null ? "Pick a model first" : $"Create {ObjectNames.NicifyVariableName(_kind.ToString())}", GUILayout.Height(28f)))
                    Create();
            EditorGUILayout.EndScrollView();
        }

        private void DrawDoor()
        {
            EditorGUILayout.LabelField("Door", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("The model's width is the door's: model it closed, facing -Z or +Z. The hinge is at its left or right edge.", EditorStyles.wordWrappedMiniLabel);
            _hingeRight = EditorGUILayout.Popup("Hinge", _hingeRight ? 1 : 0, new[] { "Left edge", "Right edge" }) == 1;
            _reverseSwing = EditorGUILayout.Toggle(new GUIContent("Reverse Swing", "Open towards +Z instead of -Z."), _reverseSwing);
            _keyId = EditorGUILayout.TextField(new GUIContent("Key Item ID", "Optional: the template id of the key; the door then starts locked."), _keyId);
            ClipList("Open Sounds", _openSounds);
            ClipList("Close Sounds", _closeSounds);
            ClipList("Squeak Sounds", _squeakSounds);
        }

        private void DrawLamp()
        {
            EditorGUILayout.LabelField("Light", EditorStyles.boldLabel);
            _spot = EditorGUILayout.Popup("Type", _spot ? 1 : 0, new[] { "Point (bulb)", "Spot (pointing down)" }) == 1;
            _lightColor = EditorGUILayout.ColorField("Color", _lightColor);
            _intensity = EditorGUILayout.FloatField("Intensity", _intensity);
            _range = EditorGUILayout.FloatField("Range (m)", _range);
            _nightOnly = EditorGUILayout.Toggle(new GUIContent("Only At Night", "On from 22:00 to 7:00, like retail street lights."), _nightOnly);
            _addFlare = EditorGUILayout.Toggle(new GUIContent("Lens Flare", "A glow sprite on the light (needs Tarkov SDK > Map > Lens Flare Settings)."), _addFlare);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("On / Off Look", EditorStyles.boldLabel);
            _emission = (Emission)EditorGUILayout.EnumPopup("Mode", _emission);
            switch (_emission)
            {
                case Emission.SwapMaterials:
                    EditorGUILayout.LabelField("Two copies of the model: one with the On material, one with the Off material.", EditorStyles.wordWrappedMiniLabel);
                    _onMaterial = (Material)EditorGUILayout.ObjectField("On Material", _onMaterial, typeof(Material), false);
                    _offMaterial = (Material)EditorGUILayout.ObjectField("Off Material", _offMaterial, typeof(Material), false);
                    SlotField();
                    break;
                case Emission.FadeEmission:
                    EditorGUILayout.LabelField("Fades _EmissionVisibility, with flicker. Use the game's emissive shader, Tarkov/p0/Reflective/Bumped Emissive Specular SMap.", EditorStyles.wordWrappedMiniLabel);
                    SlotField();
                    break;
                case Emission.EmissionColor:
                    EditorGUILayout.LabelField("Sets a colour property when the lamp turns on and off.", EditorStyles.wordWrappedMiniLabel);
                    _colorProperty = EditorGUILayout.TextField("Property", _colorProperty);
                    _colorOn = EditorGUILayout.ColorField(new GUIContent("On Color"), _colorOn, true, false, true);
                    _colorOff = EditorGUILayout.ColorField(new GUIContent("Off Color"), _colorOff, true, false, true);
                    SlotField();
                    break;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sounds", EditorStyles.boldLabel);
            ClipList("Turn On", _turnOnSounds);
            _turnOffSound = (AudioClip)EditorGUILayout.ObjectField("Turn Off", _turnOffSound, typeof(AudioClip), false);
            _humLoop = (AudioClip)EditorGUILayout.ObjectField(new GUIContent("Hum (loop)", "Plays while the lamp is on."), _humLoop, typeof(AudioClip), false);
            _flickerLoop = (AudioClip)EditorGUILayout.ObjectField(new GUIContent("Flicker (loop)", "Plays while the lamp flickers."), _flickerLoop, typeof(AudioClip), false);
        }

        private void SlotField() =>
            _materialSlot = EditorGUILayout.IntField(new GUIContent("Material Slot", "Which material of each renderer is the lamp's glass or bulb; -1 for all."), _materialSlot);

        private void DrawContainer()
        {
            EditorGUILayout.LabelField("Container", EditorStyles.boldLabel);
            _container = EditorGUILayout.Popup(new GUIContent("Type", "Sets its grid size and loot pool."), _container,
                Containers.Select(c => c.Name).Append("Other (template id)").ToArray());
            if (_container == Containers.Length)
                _customTemplate = EditorGUILayout.TextField("Template ID", _customTemplate);
            EditorGUILayout.LabelField("The player searches it from its -Z side.", EditorStyles.wordWrappedMiniLabel);
            ClipList("Open Sounds", _openSounds);
            ClipList("Close Sounds", _closeSounds);
        }

        private void DrawSwitch()
        {
            EditorGUILayout.LabelField("Switch", EditorStyles.boldLabel);
            _switchDoor = (Door)EditorGUILayout.ObjectField(new GUIContent("Opens Door", "Optional: a door in the scene."), _switchDoor, typeof(Door), true);
            _switchLamp = (LampController)EditorGUILayout.ObjectField(new GUIContent("Toggles Lamp", "Optional: a lamp in the scene."), _switchLamp, typeof(LampController), true);
            ClipList("Sounds", _openSounds);
        }

        private static void ClipList(string label, List<AudioClip> clips)
        {
            EditorGUILayout.LabelField(label);
            using (new EditorGUI.IndentLevelScope())
            {
                int remove = -1;
                for (int i = 0; i < clips.Count; i++)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        clips[i] = (AudioClip)EditorGUILayout.ObjectField(clips[i], typeof(AudioClip), false);
                        if (GUILayout.Button("-", GUILayout.Width(22f)))
                            remove = i;
                    }
                if (remove >= 0)
                    clips.RemoveAt(remove);
                var added = (AudioClip)EditorGUILayout.ObjectField("Add", null, typeof(AudioClip), false);
                if (added != null)
                    clips.Add(added);
            }
        }

        // ------------------------------------------------------------ building

        private void Create()
        {
            // built at the origin, unrotated, so world bounds are the object's own; moved to the view at the end
            GameObject root;
            switch (_kind)
            {
                case Kind.Door: root = CreateDoor(); break;
                case Kind.Lamp: root = CreateLamp(); break;
                case Kind.LootableContainer: root = CreateContainer(); break;
                default: root = CreateSwitch(); break;
            }
            root.name = string.IsNullOrWhiteSpace(_name) ? _model.name : _name.Trim();
            if (SceneView.lastActiveSceneView != null)
                root.transform.position = SceneView.lastActiveSceneView.pivot;
            Undo.RegisterCreatedObjectUndo(root, "Create " + root.name);
            Selection.activeGameObject = root;

            if (_savePrefab)
            {
                Directory.CreateDirectory(_prefabFolder);
                AssetDatabase.Refresh();
                var path = AssetDatabase.GenerateUniqueAssetPath($"{_prefabFolder.TrimEnd('/')}/{root.name}.prefab");
                PrefabUtility.SaveAsPrefabAssetAndConnect(root, path, InteractionMode.UserAction);
                Debug.Log($"[Tarkov SDK] Saved {root.name} as {path}");
            }
        }

        private GameObject CreateDoor()
        {
            var model = Model(null, out var b);
            var door = TarkovPrefabTools.CreateDoor(null, b.size.x, b.size.y, b.size.z, _hingeRight, _reverseSwing);
            var leaf = door.transform;
            Object.DestroyImmediate(leaf.Find("Panel (replace with your model)").gameObject);
            // the model's hinge edge on the leaf's pivot, its bottom on the floor, centred in depth
            float hingeX = _hingeRight ? b.max.x : b.min.x;
            model.transform.SetParent(leaf, true);
            model.transform.position += new Vector3(leaf.position.x - hingeX, -b.min.y, -b.center.z);
            SetBallistic(leaf.Find("HighPoly").gameObject);
            door.OpenSound = _openSounds.ToArray();
            door.ShutSound = _closeSounds.ToArray();
            door.SqueakSound = _squeakSounds.ToArray();
            if (!string.IsNullOrWhiteSpace(_keyId))
            {
                door.KeyId = _keyId.Trim();
                door._doorState = EDoorState.Locked;
            }
            return leaf.parent.gameObject;
        }

        private GameObject CreateLamp()
        {
            var lamp = TarkovLights.CreateLamp(null, _spot ? LightType.Spot : LightType.Point);
            var root = lamp.transform;
            Object.DestroyImmediate(root.Find("Bulb (replace with your model)").gameObject);
            var model = Model(root, out var b);

            var light = lamp.Lights[0];
            // a bulb glows from its middle; a spot shines down from the fixture's underside
            light.transform.localPosition = _spot ? new Vector3(b.center.x, b.min.y, b.center.z) : b.center;
            light.color = _lightColor;
            light.intensity = _intensity;
            light.range = _range;
            var culling = light.GetComponent<CullingLightObject>();
            culling._maxLightIntensity = _intensity;

            // what gets shot out
            var collision = Fitted(root, "Collision", "HighPolyCollider", b);
            lamp.BallisticCollider = SetBallistic(collision);

            switch (_emission)
            {
                case Emission.SwapMaterials:
                    model.name = "Model (on)";
                    var off = Model(root, out _);
                    off.name = "Model (off)";
                    SetSlot(model, _onMaterial);
                    SetSlot(off, _offMaterial);
                    off.SetActive(false); // the lamp starts on
                    lamp.OnObjects = new[] { model };
                    lamp.OffObjects = new[] { off };
                    break;
                case Emission.FadeEmission:
                    lamp._materialsWithEmission = Slots(model)
                        .Where(s => s.Renderer.sharedMaterials[s.Index] != null && s.Renderer.sharedMaterials[s.Index].HasProperty("_EmissionVisibility"))
                        .Select(s => new MaterialEmission { Renderer = s.Renderer, MaterialId = s.Index, _maxEmissionVisibility = 1f }).ToArray();
                    if (lamp._materialsWithEmission.Length == 0)
                        Debug.LogWarning("[Tarkov SDK] Fade Emission: no material on the model has _EmissionVisibility; use Tarkov/p0/Reflective/Bumped Emissive Specular SMap on the glowing part.");
                    break;
                case Emission.EmissionColor:
                    lamp._legacyMaterialsWithEmission = Slots(model)
                        .Where(s => s.Renderer.sharedMaterials[s.Index] != null && s.Renderer.sharedMaterials[s.Index].HasProperty(_colorProperty))
                        .Select(s => new MaterialColor { Renderer = s.Renderer, MaterialId = s.Index, ParemeterName = _colorProperty, ColorOn = _colorOn, ColorOff = _colorOff }).ToArray();
                    if (lamp._legacyMaterialsWithEmission.Length == 0)
                        Debug.LogWarning($"[Tarkov SDK] Emission Color: no material on the model has {_colorProperty}.");
                    break;
            }

            if (_turnOnSounds.Count > 0 || _turnOffSound != null || _humLoop != null || _flickerLoop != null)
            {
                var sound = new GameObject("Sound");
                sound.transform.SetParent(root, false);
                sound.transform.localPosition = b.center;
                var source = sound.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.maxDistance = lamp.Rolloff;
                lamp.AudioSource = source;
                lamp.TurnOnClips = _turnOnSounds.ToArray();
                lamp.TurnOffClip = _turnOffSound;
                lamp.TurnedOnClipLoop = _humLoop;
                lamp.FlickeringClipLoop = _flickerLoop;
            }
            if (_nightOnly)
                lamp.isTimeOfDayDependant = true; // on during MinMaxAmount1/2: 0-7 and 22-24
            if (_addFlare)
                TarkovLights.CreateFlare(light.transform, lamp);
            return root.gameObject;
        }

        private GameObject CreateContainer()
        {
            var container = TarkovPrefabTools.CreateContainer(null);
            var root = container.transform;
            Object.DestroyImmediate(root.Find("Crate (replace with your model)").gameObject);
            Model(root, out var b);
            FitBox(root.GetComponent<BoxCollider>(), b);
            FitBox(root.Find("HighPoly").GetComponent<BoxCollider>(), b);
            FitBox(root.Find("LowPoly").GetComponent<BoxCollider>(), b);
            SetBallistic(root.Find("HighPoly").gameObject);
            container.Template = _container < Containers.Length ? Containers[_container].Template : _customTemplate.Trim();
            container.interactPosition1 = new Vector3(b.center.x, 0f, b.min.z - 0.6f);
            container.viewTarget1 = b.center;
            container.OpenSound = _openSounds.ToArray();
            container.ShutSound = _closeSounds.ToArray();
            return root.gameObject;
        }

        private GameObject CreateSwitch()
        {
            var sw = TarkovPrefabTools.CreateSwitch(null, _switchDoor);
            var root = sw.transform;
            Object.DestroyImmediate(root.Find("Button (replace with your model)").gameObject);
            Model(root, out var b);
            FitBox(root.GetComponent<BoxCollider>(), b);
            if (_switchLamp != null)
            {
                sw.Lamps = new[] { _switchLamp };
                sw.ContextMenuTip = "toggle_light";
            }
            sw.OpenSound = _openSounds.ToArray();
            sw.ShutSound = _openSounds.ToArray();
            return root.gameObject;
        }

        // ------------------------------------------------------------ helpers

        // a copy of the model under parent at its origin, unpacked, without its colliders unless kept;
        // bounds: its renderers' bounds in world space (the object being built is still at the origin)
        private GameObject Model(Transform parent, out Bounds bounds)
        {
            GameObject copy;
            if (EditorUtility.IsPersistent(_model))
            {
                copy = (GameObject)PrefabUtility.InstantiatePrefab(_model);
                PrefabUtility.UnpackPrefabInstance(copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }
            else
            {
                copy = Instantiate(_model);
            }
            copy.name = _model.name;
            copy.transform.SetParent(parent, false);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            if (!_keepModelColliders)
                foreach (var c in copy.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c);
            if (_materialOverride != null)
                foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
                    r.sharedMaterials = Enumerable.Repeat(_materialOverride, r.sharedMaterials.Length).ToArray();

            var renderers = copy.GetComponentsInChildren<Renderer>(true);
            bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(copy.transform.position, Vector3.one * 0.2f);
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            // a flat model still needs a usable collider
            bounds.size = Vector3.Max(bounds.size, Vector3.one * 0.02f);
            return copy;
        }

        private IEnumerable<(Renderer Renderer, int Index)> Slots(GameObject model) =>
            model.GetComponentsInChildren<Renderer>(true).SelectMany(r => Enumerable.Range(0, r.sharedMaterials.Length)
                .Where(i => _materialSlot < 0 || i == _materialSlot).Select(i => (r, i)));

        private void SetSlot(GameObject model, Material material)
        {
            if (material == null)
                return;
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var materials = r.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                    if (_materialSlot < 0 || i == _materialSlot)
                        materials[i] = material;
                r.sharedMaterials = materials;
            }
        }

        private BallisticCollider SetBallistic(GameObject go)
        {
            var ballistic = go.GetComponent<BallisticCollider>();
            if (ballistic == null) // Unity's ==, not ??: a missing component can be a "fake null" in the editor
                ballistic = go.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = _bulletMaterial;
            ballistic.SurfaceSound = _surfaceSound;
            return ballistic;
        }

        // a box collider child on a layer, fitted to bounds given in world space
        private static GameObject Fitted(Transform parent, string name, string layer, Bounds bounds)
        {
            var go = new GameObject(name) { layer = LayerMask.NameToLayer(layer) };
            go.transform.SetParent(parent, false);
            FitBox(go.AddComponent<BoxCollider>(), bounds);
            return go;
        }

        private static void FitBox(BoxCollider box, Bounds world)
        {
            if (box == null)
                return;
            box.center = box.transform.InverseTransformPoint(world.center);
            box.size = world.size;
        }
    }
}
