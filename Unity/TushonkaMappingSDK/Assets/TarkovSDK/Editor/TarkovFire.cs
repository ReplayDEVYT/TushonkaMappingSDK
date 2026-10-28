using EFT.Ballistics;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Fires that burn, laid out like retail's bonfires and burning barrels (Interchange, Customs, Lighthouse):
    // particle flames, smoke and sparks, a point light the game's FlickeringLights flickers, and a trigger box with the
    // game's FlameDamageTrigger (a DamageTrigger: each body part inside burns, BodyPartCollider.ProceedFlame; Awake
    // moves it to the Triggers layer; LocationScene.FlameDamages lists it). Retail's flame and smoke materials only ship
    // inside retail scenes, so the textures here are generated and the materials use Unity's legacy particle shaders,
    // which the game always has loaded.
    //
    //  Fire         flames on the object's position, for your own brazier, bonfire or burning wreck
    //  Fire Barrel  the same in a generated open steel drum with its colliders
    public static class TarkovFire
    {
        private const string Menu = "GameObject/Tarkov SDK/Hazards/";

        [MenuItem(Menu + "Fire", priority = 47)]
        private static void MenuFire(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateFire(p, 1f).gameObject);

        [MenuItem(Menu + "Fire Barrel", priority = 48)]
        private static void MenuBarrel(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateFireBarrel(p));

        // ------------------------------------------------------------ fire

        // the fire's root; size scales the flames, light and burning box (1 = a barrel's worth)
        public static Transform CreateFire(Transform parent, float size)
        {
            var root = new GameObject("Fire");
            if (parent != null)
                root.transform.SetParent(parent, false);

            Flames(root.transform, size);
            Smoke(root.transform, size);
            Sparks(root.transform, size);

            // retail: an orange point light, range 2 to 2.5, flickered by FlickeringLights between 1 and 4
            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.localPosition = new Vector3(0f, 0.5f * size, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.57f, 0.19f);
            light.range = 4f * size;
            light.intensity = 2.5f;
            light.shadows = LightShadows.None;
            var flicker = lightObject.AddComponent<FlickeringLights>();
            flicker.MinIntensity = 1.2f;
            flicker.MaxIntensity = 3f;
            flicker.FlickerRate = 5f;

            AddBurnZone(root.transform, new Vector3(0f, 0.5f * size, 0f), new Vector3(0.7f, 1f, 0.7f) * size);
            return root.transform;
        }

        // the box that burns whoever stands in it
        public static FlameDamageTrigger AddBurnZone(Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject("BurnZone") { layer = LayerMask.NameToLayer("Triggers") };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;
            return go.AddComponent<FlameDamageTrigger>();
        }

        public static GameObject CreateFireBarrel(Transform parent)
        {
            const float radius = 0.29f, height = 0.88f;
            var root = new GameObject("FireBarrel");
            if (parent != null)
                root.transform.SetParent(parent, false);

            var model = new GameObject("Barrel");
            model.transform.SetParent(root.transform, false);
            model.AddComponent<MeshFilter>().sharedMesh = TarkovGeneratedAssets.Mesh(TarkovGeneratedAssets.PropsFolder + "/SDK_FireBarrel.asset",
                () => TarkovGeneratedAssets.Cylinder("SDK_FireBarrel", radius, height, 18, open: true));
            model.AddComponent<MeshRenderer>().sharedMaterial = TarkovGeneratedAssets.SurfaceMaterial("SDK_RustyMetal", new Color(0.26f, 0.15f, 0.09f, 0.1f), 0.12f);
#pragma warning disable 618
            GameObjectUtility.SetStaticEditorFlags(model, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618

            // movement on LowPolyCollider, bullets on HighPolyCollider, as the map's own props
            var low = new GameObject("LowPoly") { layer = LayerMask.NameToLayer("LowPolyCollider") };
            low.transform.SetParent(root.transform, false);
            var lowCapsule = low.AddComponent<CapsuleCollider>();
            lowCapsule.radius = radius;
            lowCapsule.height = height;
            lowCapsule.center = new Vector3(0f, height * 0.5f, 0f);
#pragma warning disable 618
            GameObjectUtility.SetStaticEditorFlags(low, StaticEditorFlags.NavigationStatic);
#pragma warning restore 618
            var high = new GameObject("HighPoly") { layer = LayerMask.NameToLayer("HighPolyCollider") };
            high.transform.SetParent(root.transform, false);
            var highCapsule = high.AddComponent<CapsuleCollider>();
            highCapsule.radius = radius;
            highCapsule.height = height;
            highCapsule.center = lowCapsule.center;
            var ballistic = high.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.MetalThin;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.MetalThin;

            var fire = CreateFire(root.transform, 0.75f);
            fire.localPosition = new Vector3(0f, height - 0.12f, 0f);
            // the flames stand above the rim; reaching into them burns
            var burn = fire.GetComponentInChildren<FlameDamageTrigger>().transform;
            burn.localPosition = new Vector3(0f, 0.45f, 0f);
            burn.GetComponent<BoxCollider>().size = new Vector3(radius * 2f, 0.6f, radius * 2f);
            return root;
        }

        // ------------------------------------------------------------ particles

        private static ParticleSystem System(Transform parent, string name, Vector3 local, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            // emitting upwards (a particle system emits along its +Z)
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            return ps;
        }

        private static Gradient Fade(Color a, Color b, Color c, float peak)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 0.45f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(peak, 0.15f), new GradientAlphaKey(peak * 0.6f, 0.6f), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        private static void Flames(Transform parent, float size)
        {
            var ps = System(parent, "Flames", Vector3.zero, FlameMaterial());
            var main = ps.main;
            main.duration = 2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 0.95f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f * size, 1.1f * size);
            main.startSize = new ParticleSystem.MinMaxCurve(0.35f * size, 0.6f * size);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
            main.maxParticles = 120;
            var emission = ps.emission;
            emission.rateOverTime = 45f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 8f;
            shape.radius = 0.22f * size;
            var color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = Fade(new Color(1f, 0.85f, 0.45f), new Color(1f, 0.45f, 0.12f), new Color(0.5f, 0.1f, 0.03f), 1f);
            var sizeOver = ps.sizeOverLifetime;
            sizeOver.enabled = true;
            sizeOver.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.25f)));
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.25f * size;
            noise.frequency = 1.5f;
            noise.scrollSpeed = 1f;
        }

        private static void Smoke(Transform parent, float size)
        {
            var ps = System(parent, "Smoke", new Vector3(0f, 0.6f * size, 0f), SmokeMaterial());
            var main = ps.main;
            main.duration = 6f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 6.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f * size, 1f * size);
            main.startSize = new ParticleSystem.MinMaxCurve(0.6f * size, 1f * size);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.22f, 0.21f, 0.2f, 1f);
            main.maxParticles = 60;
            var emission = ps.emission;
            emission.rateOverTime = 6f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f;
            shape.radius = 0.15f * size;
            var color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = Fade(Color.white, Color.white, Color.white, 0.4f);
            var sizeOver = ps.sizeOverLifetime;
            sizeOver.enabled = true;
            sizeOver.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(1f, 3f)));
            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.sortMode = ParticleSystemSortMode.Distance;
        }

        private static void Sparks(Transform parent, float size)
        {
            var ps = System(parent, "Sparks", new Vector3(0f, 0.25f * size, 0f), FlameMaterial());
            var main = ps.main;
            main.duration = 3f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f * size, 2.5f * size);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.045f);
            main.startColor = new Color(1f, 0.6f, 0.25f, 1f);
            main.gravityModifier = -0.15f;
            main.maxParticles = 40;
            var emission = ps.emission;
            emission.rateOverTime = 7f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 25f;
            shape.radius = 0.15f * size;
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.6f;
            noise.frequency = 2f;
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.04f;
            renderer.lengthScale = 1f;
        }

        // ------------------------------------------------------------ materials

        // a soft licking flame, brighter at the bottom
        public static Material FlameMaterial() => TarkovGeneratedAssets.Material(TarkovGeneratedAssets.EffectsFolder + "/SDK_Flame.mat", TarkovGeneratedAssets.AdditiveShader, m =>
        {
            m.SetTexture("_MainTex", TarkovGeneratedAssets.Texture(TarkovGeneratedAssets.EffectsFolder + "/SDK_Flame.png", 64, 128, (u, v) =>
            {
                float x = (u - 0.5f) * 2f;
                // a teardrop: wide low down, a point at the top
                float width = Mathf.Lerp(0.85f, 0.05f, Mathf.Pow(v, 0.8f));
                float body = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Abs(x) / Mathf.Max(width, 0.001f));
                float ends = Mathf.SmoothStep(0f, 0.15f, v) * (1f - Mathf.SmoothStep(0.75f, 1f, v));
                float noise = TarkovGeneratedAssets.Fractal(u, v * 0.5f, 11);
                float a = Mathf.Clamp01(body * ends * (0.55f + 0.8f * noise));
                return new Color(1f, 1f, 1f, a);
            }));
            m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
        });

        public static Material SmokeMaterial() => TarkovGeneratedAssets.Material(TarkovGeneratedAssets.EffectsFolder + "/SDK_Smoke.mat", TarkovGeneratedAssets.AlphaBlendedShader, m =>
        {
            m.SetTexture("_MainTex", PuffTexture());
            m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
        });

        // a cloudy round puff (smoke, dust)
        public static Texture2D PuffTexture() => TarkovGeneratedAssets.Texture(TarkovGeneratedAssets.EffectsFolder + "/SDK_Puff.png", 128, 128, (u, v) =>
        {
            float distance = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2f;
            float falloff = 1f - Mathf.SmoothStep(0f, 1f, distance);
            float noise = TarkovGeneratedAssets.Fractal(u, v, 7);
            return new Color(1f, 1f, 1f, Mathf.Clamp01(falloff * falloff * (0.5f + noise) - 0.04f));
        });

        // ------------------------------------------------------------ gizmo

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawBurnZone(FlameDamageTrigger trigger, GizmoType type)
        {
            var box = trigger.GetComponent<BoxCollider>();
            if (box == null)
                return;
            Gizmos.matrix = trigger.transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.45f, 0.1f, (type & GizmoType.Selected) != 0 ? 0.9f : 0.35f);
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
