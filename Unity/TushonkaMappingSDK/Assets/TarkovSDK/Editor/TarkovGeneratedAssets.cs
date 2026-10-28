using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // The meshes, textures and materials the SDK's creators make on first use (fire, explosive cylinders, door
    // splinters, wire poles): nothing comes from the game. Saved under Assets/TarkovSDK, made again only when missing.
    public static class TarkovGeneratedAssets
    {
        public const string PropsFolder = "Assets/TarkovSDK/Props";
        public const string EffectsFolder = "Assets/TarkovSDK/Materials/Effects";

        // Unity's legacy particle shaders are in the game's globalgamemanagers.assets, so always loaded
        public const string AdditiveShader = "Legacy Shaders/Particles/Additive";
        public const string AlphaBlendedShader = "Legacy Shaders/Particles/Alpha Blended";

        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        // the mesh at the path, or the one `build` makes saved there
        public static Mesh Mesh(string path, Func<Mesh> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
                return existing;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var mesh = build();
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // replaces the mesh asset's contents in place (keeps its GUID, so scenes keep pointing at it)
        public static Mesh SaveMesh(string path, Mesh mesh)
        {
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            UnityEngine.Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        public static Material Material(string path, string shaderName, Action<Material> setup)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[Tarkov SDK] Shader '{shaderName}' not found; '{Path.GetFileName(path)}' uses Standard.");
                shader = Shader.Find("Standard");
            }
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            setup?.Invoke(material);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // a copy of SDK_Default (the p0 stand-in the game swaps for its own) with its own flat albedo colour
        public static Material SurfaceMaterial(string name, Color albedo, float noise)
        {
            string path = $"Assets/TarkovSDK/Materials/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;
            var source = TarkovSceneTools.DefaultSurfaceMaterial();
            if (source == null)
                return null;
            var material = new Material(source) { name = name };
            material.SetTexture("_MainTex", TarkovSceneTools.SolidTexture($"Assets/TarkovSDK/Materials/{name}_Albedo.png", albedo, noise));
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // a generated PNG texture (u, v in 0..1 -> colour), clamped, alpha as transparency
        public static Texture2D Texture(string path, int width, int height, Func<float, float, Color> pixel)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
                return existing;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    texture.SetPixel(x, y, pixel((x + 0.5f) / width, (y + 0.5f) / height));
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // smooth value noise that tiles every `cells`
        public static float Noise(float x, float y, int cells, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = Mathf.SmoothStep(0f, 1f, x - x0), fy = Mathf.SmoothStep(0f, 1f, y - y0);
            float a = Hash(x0, y0, cells, seed), b = Hash(x0 + 1, y0, cells, seed);
            float c = Hash(x0, y0 + 1, cells, seed), d = Hash(x0 + 1, y0 + 1, cells, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static float Hash(int x, int y, int cells, int seed)
        {
            x = ((x % cells) + cells) % cells;
            y = ((y % cells) + cells) % cells;
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 144269504);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
        }

        public static float Fractal(float u, float v, int seed)
        {
            float sum = 0f, amplitude = 0.5f, total = 0f;
            for (int octave = 0, cells = 3; octave < 4; octave++, cells *= 2, amplitude *= 0.5f)
            {
                sum += amplitude * Noise(u * cells, v * cells, cells, seed + octave);
                total += amplitude;
            }
            return sum / total;
        }

        // ------------------------------------------------------------ meshes

        public sealed class MeshBuilder
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            // TEXCOORD1, written when every vertex has one (Add's uv2)
            public readonly List<Vector2> Uvs2 = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();

            public int Add(Vector3 v, Vector3 n, Vector2 uv)
            {
                Vertices.Add(v);
                Normals.Add(n);
                Uvs.Add(uv);
                return Vertices.Count - 1;
            }

            public int Add(Vector3 v, Vector3 n, Vector2 uv, Vector2 uv2)
            {
                Uvs2.Add(uv2);
                return Add(v, n, uv);
            }

            public void Triangle(int a, int b, int c)
            {
                Triangles.Add(a);
                Triangles.Add(b);
                Triangles.Add(c);
            }

            // a flat-shaded triangle (its own vertices, facing by winding)
            public void Facet(Vector3 a, Vector3 b, Vector3 c)
            {
                var n = Vector3.Cross(b - a, c - a).normalized;
                Triangle(Add(a, n, new Vector2(0f, 0f)), Add(b, n, new Vector2(1f, 0f)), Add(c, n, new Vector2(0.5f, 1f)));
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name };
                if (Vertices.Count > 65000)
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(Vertices);
                mesh.SetNormals(Normals);
                mesh.SetUVs(0, Uvs);
                if (Uvs2.Count == Vertices.Count && Uvs2.Count > 0)
                    mesh.SetUVs(1, Uvs2);
                mesh.SetTriangles(Triangles, 0);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                return mesh;
            }
        }

        // a cylinder standing on y = 0 around the Y axis; `open` leaves the top off and adds the inside wall
        public static Mesh Cylinder(string name, float radius, float height, int sides, bool open, float wall = 0.01f)
        {
            var b = new MeshBuilder();
            void Wall(float r, bool inward)
            {
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    var d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                    var d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                    var n0 = inward ? -d0 : d0;
                    var n1 = inward ? -d1 : d1;
                    int v0 = b.Add(d0 * r, n0, new Vector2(i / (float)sides, 0f));
                    int v1 = b.Add(d1 * r, n1, new Vector2((i + 1) / (float)sides, 0f));
                    int v2 = b.Add(d1 * r + Vector3.up * height, n1, new Vector2((i + 1) / (float)sides, 1f));
                    int v3 = b.Add(d0 * r + Vector3.up * height, n0, new Vector2(i / (float)sides, 1f));
                    if (inward)
                    {
                        b.Triangle(v0, v1, v2);
                        b.Triangle(v0, v2, v3);
                    }
                    else
                    {
                        b.Triangle(v0, v2, v1);
                        b.Triangle(v0, v3, v2);
                    }
                }
            }
            void Cap(float y, float r, bool up)
            {
                var n = up ? Vector3.up : Vector3.down;
                int center = b.Add(new Vector3(0f, y, 0f), n, new Vector2(0.5f, 0.5f));
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    int v0 = b.Add(new Vector3(Mathf.Cos(a0) * r, y, Mathf.Sin(a0) * r), n, new Vector2(0.5f + Mathf.Cos(a0) * 0.5f, 0.5f + Mathf.Sin(a0) * 0.5f));
                    int v1 = b.Add(new Vector3(Mathf.Cos(a1) * r, y, Mathf.Sin(a1) * r), n, new Vector2(0.5f + Mathf.Cos(a1) * 0.5f, 0.5f + Mathf.Sin(a1) * 0.5f));
                    if (up)
                        b.Triangle(center, v1, v0);
                    else
                        b.Triangle(center, v0, v1);
                }
            }
            Wall(radius, false);
            Cap(0f, radius, false);
            if (open)
            {
                Wall(radius - wall, true);
                Cap(0.03f, radius - wall, true);
                // the rim between the walls
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    var d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                    var d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                    var top = Vector3.up * height;
                    int o0 = b.Add(d0 * radius + top, Vector3.up, Vector2.zero), o1 = b.Add(d1 * radius + top, Vector3.up, Vector2.right);
                    int i0 = b.Add(d0 * (radius - wall) + top, Vector3.up, Vector2.up), i1 = b.Add(d1 * (radius - wall) + top, Vector3.up, Vector2.one);
                    b.Triangle(o0, i1, o1);
                    b.Triangle(o0, i0, i1);
                }
            }
            else
            {
                Cap(height, radius, true);
            }
            return b.Build(name);
        }
    }
}
