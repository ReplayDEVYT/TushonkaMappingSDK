using System.IO;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // A local patch of fog (a foggy room, mist over water, a smoky hall): a box of slow, soft particle
    // puffs on the game's "Global Fog/Particle Alpha Blended Lighted", which the scene lights and the
    // weather fog shade like everything else. Retail maps draw such fog with fog-sheet billboards,
    // but their shader only ships inside retail scenes, so SDK maps can't use it; this particle
    // shader is in the game's shared shader bundle.
    //
    // The box is the particle system's Shape (scale the object or Shape > Scale); thicker fog is a
    // higher Start Color alpha or Emission rate. The puff texture is generated, not taken from the game.
    public static class TarkovFogVolume
    {
        private const string MaterialPath = "Assets/TarkovSDK/Materials/FogVolume.mat";
        private const string TexturePath = "Assets/TarkovSDK/Materials/FogPuff.png";
        private const string ShaderName = "Tarkov/Global Fog/Particle Alpha Blended Lighted";

        [MenuItem("GameObject/Tarkov SDK/Environment/Fog Volume", priority = 68)]
        private static void MenuFogVolume(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateFogVolume(p).gameObject);

        public static ParticleSystem CreateFogVolume(Transform parent)
        {
            var go = new GameObject("FogVolume");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = true;
            main.prewarm = true; // full from the first frame of the raid
            main.duration = 20f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(16f, 24f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.08f);
            main.startSize = new ParticleSystem.MinMaxCurve(3f, 5f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.78f, 0.8f, 0.82f, 0.3f);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Shape; // scaling the object scales the box, not the puffs
            main.maxParticles = 150;
            main.playOnAwake = true;

            var emission = ps.emission;
            emission.rateOverTime = 5f; // about 100 puffs alive

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(10f, 2f, 10f);
            shape.position = new Vector3(0f, 1f, 0f); // the box sits on the object's position

            // fade in and out so puffs never pop
            var color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
            color.color = gradient;

            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.1f, 0.1f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = FogMaterial();
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.maxParticleSize = 3f; // stays large up close instead of shrinking to half the screen
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }

        public static Material FogMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null)
                return material;
            material = new Material(Shader.Find(ShaderName)) { name = "FogVolume" };
            material.SetTexture("_MainTex", PuffTexture());
            material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f)); // neutral: the particles' colour is the fog's
            material.SetFloat("_InvFade", 0.4f); // soft where puffs meet walls and floors
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        // a soft cloudy puff: fractal value noise under a round falloff, white with the shape in alpha
        private static Texture2D PuffTexture()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (existing != null)
                return existing;
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var random = new System.Random(1337);
            var lattice = new float[17, 17];
            for (int y = 0; y < 17; y++)
                for (int x = 0; x < 17; x++)
                    lattice[x, y] = (float)random.NextDouble();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    float noise = 0f, amplitude = 0.5f, total = 0f;
                    for (int octave = 0, cells = 2; octave < 4; octave++, cells *= 2, amplitude *= 0.5f)
                    {
                        noise += amplitude * Lattice(lattice, u * cells, v * cells, cells);
                        total += amplitude;
                    }
                    noise /= total;
                    float distance = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2f;
                    float falloff = 1f - Mathf.SmoothStep(0f, 1f, distance);
                    float alpha = Mathf.Clamp01(falloff * falloff * (0.55f + 0.9f * noise) - 0.05f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            File.WriteAllBytes(TexturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(TexturePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(TexturePath);
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        }

        // smooth value noise on a lattice that wraps every `cells` cells
        private static float Lattice(float[,] lattice, float x, float y, int cells)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = Mathf.SmoothStep(0f, 1f, x - x0), fy = Mathf.SmoothStep(0f, 1f, y - y0);
            float a = lattice[x0 % cells, y0 % cells], b = lattice[(x0 + 1) % cells, y0 % cells];
            float c = lattice[x0 % cells, (y0 + 1) % cells], d = lattice[(x0 + 1) % cells, (y0 + 1) % cells];
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        // the box, also when not selected, so fog volumes are easy to find
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawFogVolume(ParticleSystem ps, GizmoType type)
        {
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer == null || renderer.sharedMaterial == null || renderer.sharedMaterial.name != "FogVolume")
                return;
            var shape = ps.shape;
            Gizmos.matrix = ps.transform.localToWorldMatrix;
            Gizmos.color = new Color(0.8f, 0.85f, 0.9f, (type & GizmoType.Selected) != 0 ? 0.9f : 0.35f);
            Gizmos.DrawWireCube(shape.position, shape.scale);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
