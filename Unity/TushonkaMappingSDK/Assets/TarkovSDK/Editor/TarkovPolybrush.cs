using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Polybrush (com.unity.polybrush, Install Packages > Polybrush) for sculpting, vertex colours, texture blending and
    // prefab scattering on map meshes:
    //  - Texture blending: Polybrush's Texture Blend brush reads a shader's layers from its importer userData (an
    //    AttributeLayoutContainer as JSON); the stand-in "Tarkov/Custom/Vert Paint Shader Solid" carries it in its
    //    committed .meta: vertex colour R/G/B -> _MainTex0 (base) / _MainTex1 / _MainTex2, as the game's shader blends.
    //  - Build: a painted or sculpted object keeps Polybrush's PolybrushMesh component, whose mesh (PolyMesh.m_Mesh, a
    //    plain new Mesh on the MeshFilter, or MeshRenderer.additionalVertexStreams) is saved into the scene, but the
    //    component itself would be a missing script in the game, and Polybrush has no build step that strips it. So the
    //    built scene's copies lose every Polybrush component here ([PostProcessScene], which BuildAssetBundles runs for
    //    scenes too). PolybrushMesh.OnDestroy (editor, not playing) puts the source mesh back by rewriting that same
    //    Mesh, so the painted meshes are copied first and reassigned after.
    public static class TarkovPolybrush
    {
        private const string Assembly = "Unity.Polybrush";

        public static bool Available => Type.GetType("UnityEngine.Polybrush.PolybrushMesh, " + Assembly) != null;

        [MenuItem("Tarkov SDK/Install Packages/Polybrush (1.2.1)", priority = 222)]
        private static void Install() => EditorApplication.delayCall += () => TarkovPackages.Install("com.unity.polybrush", "1.2.1", "Polybrush", Available);

        [PostProcessScene(1)]
        private static void StripFromBuiltScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            var components = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(c => c != null && !EditorUtility.IsPersistent(c) && c.GetType().Assembly.GetName().Name == Assembly).ToList();
            foreach (var component in components)
            {
                if (component == null)
                    continue;
                var go = component.gameObject;
                var filter = go.GetComponent<MeshFilter>();
                var skinned = go.GetComponent<SkinnedMeshRenderer>();
                var renderer = go.GetComponent<MeshRenderer>();
                var mesh = Keep(filter != null ? filter.sharedMesh : null);
                var skin = Keep(skinned != null ? skinned.sharedMesh : null);
                var streams = Keep(renderer != null ? renderer.additionalVertexStreams : null);
                UnityEngine.Object.DestroyImmediate(component);
                // everything back as it was painted, empty streams included
                if (filter != null)
                    filter.sharedMesh = mesh;
                if (skinned != null)
                    skinned.sharedMesh = skin;
                if (renderer != null)
                    renderer.additionalVertexStreams = streams;
            }
        }

        // a mesh asset is kept by reference; a scene mesh (Polybrush's own) is copied, since OnDestroy rewrites it
        private static Mesh Keep(Mesh mesh)
        {
            if (mesh == null || EditorUtility.IsPersistent(mesh))
                return mesh;
            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.name = mesh.name;
            return copy;
        }
    }
}
