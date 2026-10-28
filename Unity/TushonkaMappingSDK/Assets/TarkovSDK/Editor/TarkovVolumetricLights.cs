using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Volumetric light: shafts of light through windows and dusty halls, glowing haze round lamps. The game's
    // VolumetricLight on a Light renders it in the raid through the camera's VolumetricLightRenderer with
    // "Sandbox/VolumetricLight" (always loaded), when the player has Volumetric Lighting on in the graphics settings and
    // the light is brighter than 0.001. Retail values (Shoreline level13, 5219 lights across the five big maps): 8
    // samples, extinction 0.01, skybox extinction 0.9, Mie G 0.1, max ray 400 m; scattering 0.4 on spot lights (shadowed
    // for shafts), 0.9 to 1 on point lights. TarkovVolumetricPreview draws it in Scene views, without the shadows.
    public static class TarkovVolumetricLights
    {
        private const string Menu = "GameObject/Tarkov SDK/Lights/";

        [MenuItem(Menu + "Volumetric Light Shaft", priority = 56)]
        private static void MenuShaft(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateShaft(p).gameObject);

        [MenuItem("Tarkov SDK/Tools/Lighting/Add Volumetric Light To Selected Lights", priority = 132)]
        private static void MenuAdd()
        {
            var lights = Selection.gameObjects.SelectMany(g => g.GetComponentsInChildren<Light>(true)).Distinct().ToList();
            Undo.SetCurrentGroupName("Add volumetric light");
            int added = 0;
            foreach (var light in lights)
                if (light.GetComponent<VolumetricLight>() == null && light.type != LightType.Area && light.type != LightType.Disc)
                {
                    Apply(Undo.AddComponent<VolumetricLight>(light.gameObject), light.type);
                    added++;
                }
            Debug.Log($"[Tarkov SDK] Volumetric light added to {added} light(s); the Scene view previews it, and the raid draws it with Volumetric Lighting on in the game's graphics settings");
        }

        [MenuItem("Tarkov SDK/Tools/Lighting/Add Volumetric Light To Selected Lights", true)]
        private static bool CanAdd() => Selection.gameObjects.Any(g => g.GetComponentInChildren<Light>(true) != null);

        // a soft-shadowed warm spot pointing down and forward, as sun through a window
        public static Light CreateShaft(Transform parent)
        {
            var go = new GameObject("VolumetricShaft");
            if (parent != null)
                go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(55f, 0f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 40f;
            light.range = 12f;
            light.intensity = 1.2f;
            light.color = new Color(1f, 0.93f, 0.8f);
            // the shaft is cut by the light's shadow map; without shadows it's a plain cone of haze
            light.shadows = LightShadows.Soft;
            Apply(go.AddComponent<VolumetricLight>(), LightType.Spot);
            return light;
        }

        public static void Apply(VolumetricLight volumetric, LightType type)
        {
            volumetric.SampleCount = 8;
            volumetric.ScatteringCoef = type == LightType.Point ? 0.9f : 0.4f;
            volumetric.ExtinctionCoef = 0.01f;
            volumetric.SkyboxExtinctionCoef = 0.9f;
            volumetric.MieG = 0.1f;
            volumetric.HeightFog = false;
            volumetric.Noise = false;
            volumetric.MaxRayLength = 400f;
            EditorUtility.SetDirty(volumetric);
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var volumetric in roots.SelectMany(r => r.GetComponentsInChildren<VolumetricLight>(true)))
            {
                var light = volumetric.GetComponent<Light>();
                if (light == null)
                    report.Errors.Add($"{scene.name}: volumetric light '{volumetric.name}' has no Light on its object (the game requires one)");
                else if (light.type == LightType.Area || light.type == LightType.Disc)
                    report.Warnings.Add($"{scene.name}: volumetric light '{volumetric.name}' is on a baked area light, which it can't draw");
                else if (light.intensity < 0.001f && volumetric.GetComponentInParent<LampController>() == null)
                    report.Warnings.Add($"{scene.name}: volumetric light '{volumetric.name}': its light's intensity is 0, so it never draws");
            }
        }

        // ------------------------------------------------------------ gizmo

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawVolumetric(VolumetricLight volumetric, GizmoType type)
        {
            var light = volumetric.GetComponent<Light>();
            if (light == null)
                return;
            bool selected = (type & GizmoType.Selected) != 0;
            var color = light.color;
            color.a = selected ? 0.35f : 0.12f;
            Handles.color = color;
            var t = volumetric.transform;
            if (light.type == LightType.Spot)
            {
                float radius = Mathf.Tan(light.spotAngle * 0.5f * Mathf.Deg2Rad) * light.range;
                var end = t.position + t.forward * light.range;
                Handles.DrawWireDisc(end, t.forward, radius);
                for (int i = 0; i < 8; i++)
                {
                    var dir = Quaternion.AngleAxis(i * 45f, t.forward) * t.up;
                    Handles.DrawLine(t.position, end + dir * radius);
                }
            }
            else if (light.type == LightType.Point && selected)
            {
                Handles.DrawWireDisc(t.position, Vector3.up, light.range);
                Handles.DrawWireDisc(t.position, Vector3.right, light.range);
                Handles.DrawWireDisc(t.position, Vector3.forward, light.range);
            }
        }
    }

    [CustomEditor(typeof(VolumetricLight))]
    [CanEditMultipleObjects]
    internal class TarkovVolumetricLightEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("The game's volumetric light: it draws in the raid with Volumetric Lighting on in the game's graphics settings. " +
                                    "Scattering is how bright the haze is; spot lights with soft shadows make light shafts.", MessageType.None);
            DrawDefaultInspector();
            TarkovVolumetricPreview.Enabled = EditorGUILayout.ToggleLeft(new GUIContent("Preview In Scene View",
                "Draws the haze in Scene views with the game's scattering, extinction and Mie phase. Shadows aren't traced, so shafts aren't cut by walls and window frames as in the raid."),
                TarkovVolumetricPreview.Enabled);
            if (GUILayout.Button("Retail Values"))
                foreach (var t in targets.Cast<VolumetricLight>())
                {
                    Undo.RecordObject(t, "Retail volumetric values");
                    var light = t.GetComponent<Light>();
                    TarkovVolumetricLights.Apply(t, light != null ? light.type : LightType.Spot);
                }
        }
    }
}
