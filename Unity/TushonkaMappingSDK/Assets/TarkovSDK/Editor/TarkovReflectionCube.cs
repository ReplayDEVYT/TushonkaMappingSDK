using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // The p0 shaders add a per-material cubemap reflection (_Cube x _ReflectColor x MainTex.a) straight into the
    // GBuffer's emission: no light, shadow or time of day touches it. Retail materials always have one assigned
    // (Customs' SMap materials: 126 of 130, blurred environment cubemaps averaging 18-45% grey, with
    // _ReflectColor about 0.2-0.3). With none, the game samples Unity's default grey cube, so every surface
    // reflects mid-grey day and night and shines at night. Retail's cubemaps are game assets, so the SDK makes
    // its own: a blurred neutral environment as bright as the game's patron_cubemap_metall (about 18% grey).
    public static class TarkovReflectionCube
    {
        public const string Path = "Assets/TarkovSDK/Materials/SDK_ReflectionCube.cubemap";
        // the median of Customs' SMap materials
        public static readonly Color RetailReflectColor = new Color(0.23f, 0.23f, 0.23f, 0.5f);

        public static Cubemap Get()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(Path);
            if (existing != null)
                return existing;
            const int size = 64;
            var cube = new Cubemap(size, TextureFormat.RGBA32, true) { name = "SDK_ReflectionCube" };
            var pixels = new Color[size * size];
            for (int face = 0; face < 6; face++)
            {
                for (int row = 0; row < size; row++)
                for (int col = 0; col < size; col++)
                {
                    // D3D face layout, row 0 at the top: only the height matters here
                    float sc = (col + 0.5f) / size * 2f - 1f, tc = (row + 0.5f) / size * 2f - 1f;
                    var dir = (CubemapFace)face switch
                    {
                        CubemapFace.PositiveX => new Vector3(1f, -tc, -sc),
                        CubemapFace.NegativeX => new Vector3(-1f, -tc, sc),
                        CubemapFace.PositiveY => new Vector3(sc, 1f, tc),
                        CubemapFace.NegativeY => new Vector3(sc, -1f, -tc),
                        CubemapFace.PositiveZ => new Vector3(sc, -tc, 1f),
                        _ => new Vector3(-sc, -tc, -1f),
                    };
                    pixels[row * size + col] = Environment(dir.normalized.y);
                }
                cube.SetPixels(pixels, (CubemapFace)face);
            }
            cube.Apply(true, false);
            AssetDatabase.CreateAsset(cube, Path);
            AssetDatabase.SaveAssets();
            Debug.Log("[Tarkov SDK] Created " + Path);
            return cube;
        }

        // gamma values: dark warm ground, grey horizon, light cool sky
        private static Color Environment(float y)
        {
            var ground = new Color(0.08f, 0.075f, 0.07f);
            var horizon = new Color(0.2f, 0.2f, 0.2f);
            var sky = new Color(0.3f, 0.32f, 0.34f);
            var c = y < 0f ? Color.Lerp(horizon, ground, Mathf.SmoothStep(0f, 1f, -y * 2f)) : Color.Lerp(horizon, sky, Mathf.SmoothStep(0f, 1f, y * 1.5f));
            c.a = 1f;
            return c;
        }

        // a p0 material reflecting Unity's default cube
        public static bool IsMissing(Material m) =>
            m != null && m.shader != null && m.shader.name.Contains("p0/") && m.HasProperty("_Cube") && m.GetTexture("_Cube") == null;

        // assigns the SDK cube; a reflection colour left brighter than retail's (the SDK used to set 0.5 or 1)
        // comes down to the retail median
        public static void Assign(Material m)
        {
            m.SetTexture("_Cube", Get());
            if (m.HasProperty("_ReflectColor") && m.GetColor("_ReflectColor").maxColorComponent > 0.4f)
                m.SetColor("_ReflectColor", RetailReflectColor);
            EditorUtility.SetDirty(m);
        }
    }
}
