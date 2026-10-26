using System;
using System.Collections.Generic;
using System.Linq;
using Audio.AmbientSubsystem;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // The map's own AudioSources and Meta XR Audio. The game's spatializer is Meta's: it places a sound with
    // HRTF and gives it the room acoustics (reverb, early reflections from the baked map), but only for a
    // source with Spatialize on. Unity plays the others with plain panning and no room sound, so they stand
    // out next to the game's own sounds. A 2D source (an ambience bed) with Spatialize on is the opposite
    // mistake: Meta turns it into a mono point at the object.
    //
    //   3D sources  Spatialize on, plus Meta's MetaXRAudioSource (acoustics on), which the game's copy
    //               applies once at Awake, and which sets the source's own reverb send in its inspector
    //   2D sources  Spatialize off, and a MetaXRAudioSource on them doesn't spatialize (its Awake would
    //               turn Spatialize back on)
    //
    // A source the game wraps in its own BetterSource (a lamp's loop) keeps these: the game only switches
    // spatialization on its own pooled sources. The ambient players set their spatial blend at Awake from
    // their own field, so that one counts for them.
    public static class TarkovAudioSources
    {
        public static Type MetaSourceType => Type.GetType("MetaXRAudioSource, Meta.XR.Audio");

        public static IEnumerable<AudioSource> SceneSources(Scene scene) =>
            scene.GetRootGameObjects()
                 .Where(g => !g.CompareTag("EditorOnly"))
                 .SelectMany(g => g.GetComponentsInChildren<AudioSource>(true))
                 // the previews' temporary sources
                 .Where(s => s.hideFlags == HideFlags.None && !s.CompareTag("EditorOnly"));

        public static bool Is3D(AudioSource source)
        {
            var player = source.GetComponent<BaseAmbientSoundPlayer>();
            var blend = player != null ? new SerializedObject(player).FindProperty("_spatialBlend")?.floatValue ?? source.spatialBlend : source.spatialBlend;
            return blend >= 0.5f;
        }

        // what keeps the source from working with Meta in the raid, or null
        public static string Problem(AudioSource source)
        {
            var meta = MetaSourceType != null ? source.GetComponent(MetaSourceType) : null;
            var metaSpatial = meta != null ? new SerializedObject(meta).FindProperty("enableSpatialization") : null;
            if (Is3D(source))
            {
                if (!source.spatialize)
                    return "3D without Spatialize: no Meta placement or room acoustics";
                if (MetaSourceType != null && meta == null)
                    return "no Meta XR Audio Source";
                if (metaSpatial != null && !metaSpatial.boolValue)
                    return "Meta XR Audio Source has spatialization off (it turns Spatialize off in the raid)";
                return null;
            }
            if (source.spatialize)
                return "2D with Spatialize on: Meta plays it as a point";
            if (metaSpatial != null && metaSpatial.boolValue)
                return "2D with a spatializing Meta XR Audio Source (it turns Spatialize on in the raid)";
            return null;
        }

        public static List<AudioSource> Broken(Scene scene) => SceneSources(scene).Where(s => Problem(s) != null).ToList();

        // sets a source up for Meta as above; returns whether anything changed
        public static bool SetUp(AudioSource source, bool undo = true)
        {
            bool spatial = Is3D(source);
            bool changed = false;
            if (source.spatialize != spatial)
            {
                if (undo)
                    Undo.RecordObject(source, "Set up audio source for Meta XR");
                source.spatialize = spatial;
                source.spatializePostEffects = false;
                EditorUtility.SetDirty(source);
                changed = true;
            }
            var type = MetaSourceType;
            if (type == null)
                return changed;
            var meta = source.GetComponent(type);
            if (meta == null && spatial)
            {
                meta = undo ? Undo.AddComponent(source.gameObject, type) : source.gameObject.AddComponent(type);
                changed = true;
            }
            if (meta != null)
            {
                var so = new SerializedObject(meta);
                var enable = so.FindProperty("enableSpatialization");
                if (enable != null && enable.boolValue != spatial)
                {
                    enable.boolValue = spatial;
                    changed = true;
                }
                if (undo)
                    so.ApplyModifiedProperties();
                else
                    so.ApplyModifiedPropertiesWithoutUndo();
            }
            return changed;
        }

        [MenuItem("Tarkov SDK/Quick Fixes/Fix Audio Sources (Meta XR)", priority = 204)]
        private static void FixMenu() => EditorApplication.delayCall += () =>
        {
            var scene = SceneManager.GetActiveScene();
            int count = Fix(scene);
            if (count > 0)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[Tarkov SDK] Audio sources: {count} set up for Meta XR Audio in {scene.name}.");
        };

        public static int Fix(Scene scene)
        {
            int fixedCount = 0;
            foreach (var source in Broken(scene))
                if (SetUp(source))
                {
                    fixedCount++;
                    Debug.Log($"[Tarkov SDK] Audio source '{Path(source.transform)}' set up for Meta XR Audio ({(Is3D(source) ? "3D" : "2D")})", source);
                }
            return fixedCount;
        }

        public static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }
}
