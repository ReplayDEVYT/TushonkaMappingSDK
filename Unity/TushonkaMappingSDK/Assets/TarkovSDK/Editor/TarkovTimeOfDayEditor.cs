using EFT.Weather;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Inspector for the simple lighting rig. In the raid the client plugin keeps the rig's clock on
    // the raid's time and places the sun for it (CustomMapLoader's SimpleTimeOfDay); the preview here
    // uses the same sun model, so the Scene view shows what an hour will look like.
    [CustomEditor(typeof(TODSkySimple))]
    public class TarkovTimeOfDayEditor : UnityEditor.Editor
    {

        public override void OnInspectorGUI()
        {
            var sky = (TODSkySimple)target;
            var time = sky.GetComponent<TOD_Time>();

            EditorGUILayout.LabelField("Time of Day", EditorStyles.boldLabel);
            if (time != null)
            {
                EditorGUI.BeginChangeCheck();
                bool freeze = EditorGUILayout.Toggle(new GUIContent("Freeze Time",
                    "Off: the sun follows the raid's clock. On: the time stays at the raid's start time for the whole raid."), time.LockCurrentTime);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(time, "Freeze time");
                    time.LockCurrentTime = freeze;
                    EditorUtility.SetDirty(time);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No TOD_Time on this object; the time cannot follow the raid.", MessageType.Warning);
            }

            DrawPreviewControls();
            EditorGUILayout.HelpBox("The raid's time sets the hour. Latitude and the LevelSettings north direction set the sun's path.", MessageType.None);

            EditorGUILayout.Space();
            DrawDefaultInspector();
        }

        // the Scene view lighting preview (TarkovLightingPreview), shared with the Time of Day window
        public static void DrawPreviewControls()
        {
            bool enabled = EditorGUILayout.Toggle(new GUIContent("Preview Lighting",
                "Light the Scene view the way the raid will: the sun for the preview hour and the game's ambient light."), TarkovLightingPreview.Enabled);
            if (enabled != TarkovLightingPreview.Enabled)
                TarkovLightingPreview.Enabled = enabled;
            using (new EditorGUI.DisabledScope(!enabled))
            using (new EditorGUILayout.HorizontalScope())
            {
                bool mapHour = TarkovLightingPreview.Hour < 0f;
                float hour = EditorGUILayout.Slider(new GUIContent("Preview Hour", "Only for the Scene view; the raid sets the real hour."),
                    mapHour ? 12f : TarkovLightingPreview.Hour, 0f, 24f);
                if (!mapHour && !Mathf.Approximately(hour, TarkovLightingPreview.Hour) || mapHour && !Mathf.Approximately(hour, 12f))
                    TarkovLightingPreview.Hour = hour;
                if (GUILayout.Button("Noon", GUILayout.Width(48f)))
                    TarkovLightingPreview.Hour = 12f;
                if (GUILayout.Button("Night", GUILayout.Width(48f)))
                    TarkovLightingPreview.Hour = 0f;
            }
        }

        // same model as CustomMapLoader's SimpleTimeOfDay.SunDirection
        public static Vector3 SunDirection(TOD_CycleParameters cycle, float latitude, float northDegrees)
        {
            int dayOfYear = 172;
            if (cycle.Month >= 1 && cycle.Month <= 12 && cycle.Day >= 1)
                dayOfYear = Mathf.Clamp(new System.DateTime(2001, cycle.Month, 1).DayOfYear + cycle.Day - 1, 1, 365);
            float declination = 23.44f * Mathf.Deg2Rad * Mathf.Sin(2f * Mathf.PI * (284 + dayOfYear) / 365f);
            float hourAngle = (cycle.Hour - 12f) * 15f * Mathf.Deg2Rad;
            float lat = latitude * Mathf.Deg2Rad;
            float altitude = Mathf.Asin(Mathf.Sin(lat) * Mathf.Sin(declination) + Mathf.Cos(lat) * Mathf.Cos(declination) * Mathf.Cos(hourAngle));
            float azimuth = Mathf.Atan2(Mathf.Sin(hourAngle), Mathf.Cos(hourAngle) * Mathf.Sin(lat) - Mathf.Tan(declination) * Mathf.Cos(lat)) + Mathf.PI;
            var local = new Vector3(Mathf.Cos(altitude) * Mathf.Sin(azimuth), Mathf.Sin(altitude), Mathf.Cos(altitude) * Mathf.Cos(azimuth));
            return Quaternion.Euler(0f, northDegrees, 0f) * local;
        }
    }
}
