using System.Collections.Generic;
using System.Linq;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Indoor ambience as on retail maps: each spatial audio room's RoomAmbientData.RoomTone, a loop the game's
    // RoomToneController plays (2D, through the AmbientIn mixer) while the player is in the room. Going into a
    // room with another tone crossfades; a room with the same tone (or none) carries the playing one on.
    // Customs: amb_residential at 0.4 in nearly every room, amb_bunker at 0.4 in its bunkers, which are
    // IndoorIsolated rooms with Isolated set and wall occlusion 0.8; indoor rooms let 0 outdoor ambience in
    // (only the openings do, by the player's distance to them).
    //
    // The SDK keeps no setting of its own: the scene's indoor (or bunker) tone is the one most of those rooms
    // have, and changing it changes the rooms that have it or none, so rooms given their own tone keep it.
    public static class TarkovRoomTones
    {
        public const float DefaultVolume = 0.4f;
        public const float BunkerWallOcclusion = 0.8f;

        public static bool IsBunker(SpatialAudioRoom room) => room._type == Audio.SpatialSystem.EAudioRoomTypeMask.IndoorIsolated;

        // ------------------------------------------------------------ bunkers from the indoor triggers

        // the map's bunkers are the IndoorTriggers with Is Bunker (the game's own bunker muffling); an audio room
        // is a bunker when most of its area is inside them. Bunker rooms follow the triggers both ways.
        public static int SyncBunkers(Scene scene, SpatialAudioSystem system)
        {
            if (system == null)
                return 0;
            var triggers = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<EFT.EnvironmentEffect.IndoorTrigger>(true))
                                .Where(t => t.IsBunker).ToList();
            int changed = 0;
            foreach (var room in Indoor(system))
            {
                bool bunker = triggers.Count > 0 && InBunker(room, triggers);
                if (bunker == IsBunker(room))
                    continue;
                if (bunker)
                    MakeBunker(room);
                else
                    MakeIndoor(room);
                changed++;
            }
            return changed;
        }

        private static bool InBunker(SpatialAudioRoom room, List<EFT.EnvironmentEffect.IndoorTrigger> triggers)
        {
            float inside = 0f, total = 0f;
            foreach (var area in (room.Areas ?? new List<AudioTriggerArea>()).Where(a => a != null))
            {
                var s = area.transform.lossyScale;
                float volume = Mathf.Abs(s.x * s.y * s.z);
                total += volume;
                if (triggers.Any(t => Contains(t, area.transform.position)))
                    inside += volume;
            }
            return total > 0f && inside >= total * 0.5f;
        }

        // the trigger is its BoxCollider, or the unit box of its transform as the SDK generates them
        private static bool Contains(EFT.EnvironmentEffect.IndoorTrigger trigger, Vector3 point)
        {
            var box = trigger.GetComponent<BoxCollider>();
            var local = trigger.transform.InverseTransformPoint(point) - (box != null ? box.center : Vector3.zero);
            var half = (box != null ? box.size : Vector3.one) * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }

        // a room leaving a bunker: a plain indoor room with the indoor tone
        private static void MakeIndoor(SpatialAudioRoom room)
        {
            var system = room.GetComponentInParent<SpatialAudioSystem>(true);
            var bunkerTone = system != null ? Current(system, bunker: true).Clip : null;
            var tone = system != null ? Current(system, bunker: false) : (null, DefaultVolume);
            Undo.RecordObject(room, "Indoor room");
            room._type = Audio.SpatialSystem.EAudioRoomTypeMask.IndoorGeneric;
            room.Isolated = false;
            room.WallOcclusion = 0.6f;
            room.AmbientData ??= new RoomAmbientData();
            if (room.AmbientData.RoomTone == null || room.AmbientData.RoomTone == bunkerTone)
            {
                room.AmbientData.RoomTone = tone.Clip;
                room.AmbientData.RoomToneVolume = tone.Volume;
            }
            EditorUtility.SetDirty(room);
        }

        public static List<SpatialAudioRoom> Rooms(SpatialAudioSystem system, bool bunker) =>
            system.GetComponentsInChildren<SpatialAudioRoom>(true).Where(r => !TarkovSpatialAudio.IsOutdoor(r) && IsBunker(r) == bunker).ToList();

        private static List<SpatialAudioRoom> Indoor(SpatialAudioSystem system) =>
            system.GetComponentsInChildren<SpatialAudioRoom>(true).Where(r => !TarkovSpatialAudio.IsOutdoor(r)).ToList();

        // the tone most of the rooms have, with the volume most of those have; without such rooms, the one
        // last picked for the scene
        public static (AudioClip Clip, float Volume) Current(SpatialAudioSystem system, bool bunker)
        {
            var toned = Rooms(system, bunker).Where(r => r.AmbientData?.RoomTone != null).ToList();
            if (toned.Count == 0)
                return Saved(system, bunker);
            var clip = toned.GroupBy(r => r.AmbientData.RoomTone).OrderByDescending(g => g.Count()).First().Key;
            var volume = toned.Where(r => r.AmbientData.RoomTone == clip).GroupBy(r => Mathf.Round(r.AmbientData.RoomToneVolume * 100f))
                              .OrderByDescending(g => g.Count()).First().First().AmbientData.RoomToneVolume;
            return (clip, volume);
        }

        // the rooms with the current tone or none get this one; returns how many
        public static int SetTone(SpatialAudioSystem system, bool bunker, AudioClip clip, float volume)
        {
            var old = Current(system, bunker).Clip;
            Save(system, bunker, clip, volume);
            var rooms = Rooms(system, bunker).Where(r => r.AmbientData == null || r.AmbientData.RoomTone == null || r.AmbientData.RoomTone == old).ToList();
            if (rooms.Count == 0)
                return 0;
            Undo.RecordObjects(rooms.ToArray(), "Room tone");
            foreach (var room in rooms)
            {
                room.AmbientData ??= new RoomAmbientData();
                room.AmbientData.RoomTone = clip;
                room.AmbientData.RoomToneVolume = volume;
                EditorUtility.SetDirty(room);
            }
            return rooms.Count;
        }

        private static (AudioClip Clip, float Volume) Saved(SpatialAudioSystem system, bool bunker)
        {
            var state = TarkovAudioBakeState.Load(system.gameObject.scene);
            var guid = bunker ? state.BunkerTone : state.IndoorTone;
            var path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            var clip = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            return (clip, bunker ? state.BunkerToneVolume : state.IndoorToneVolume);
        }

        private static void Save(SpatialAudioSystem system, bool bunker, AudioClip clip, float volume)
        {
            var scene = system.gameObject.scene;
            var state = TarkovAudioBakeState.Load(scene);
            var guid = clip != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string g, out long _) ? g : null;
            if (bunker)
            {
                state.BunkerTone = guid;
                state.BunkerToneVolume = volume;
            }
            else
            {
                state.IndoorTone = guid;
                state.IndoorToneVolume = volume;
            }
            state.Save(scene);
        }

        // how loud the outdoor ambience is in most indoor rooms, away from their openings
        public static float OutdoorLevel(SpatialAudioSystem system)
        {
            var rooms = Indoor(system).Where(r => r.AmbientData != null).ToList();
            return rooms.Count == 0 ? 0f
                : rooms.GroupBy(r => Mathf.Round(r.AmbientData.OutdoorAmbientVolume * 100f)).OrderByDescending(g => g.Count()).First().First().AmbientData.OutdoorAmbientVolume;
        }

        public static int SetOutdoorLevel(SpatialAudioSystem system, float value)
        {
            var old = OutdoorLevel(system);
            var rooms = Indoor(system).Where(r => r.AmbientData == null || Mathf.Abs(r.AmbientData.OutdoorAmbientVolume - old) < 0.005f).ToList();
            if (rooms.Count == 0)
                return 0;
            Undo.RecordObjects(rooms.ToArray(), "Outdoor ambience indoors");
            foreach (var room in rooms)
            {
                room.AmbientData ??= new RoomAmbientData();
                room.AmbientData.OutdoorAmbientVolume = value;
                EditorUtility.SetDirty(room);
            }
            return rooms.Count;
        }

        // Customs' bunker (an IndoorIsolated room): isolated (sound from rooms it doesn't connect to is cut by the route), thick walls,
        // no outdoor ambience, the scene's bunker tone
        public static void MakeBunker(SpatialAudioRoom room)
        {
            var system = room.GetComponentInParent<SpatialAudioSystem>(true);
            var tone = system != null ? Current(system, bunker: true) : (null, DefaultVolume);
            Undo.RecordObject(room, "Bunker room");
            room._type = Audio.SpatialSystem.EAudioRoomTypeMask.IndoorIsolated;
            room.Outdoor = false;
            room.Isolated = true;
            room.WallOcclusion = Mathf.Max(room.WallOcclusion, BunkerWallOcclusion);
            room.AmbientData ??= new RoomAmbientData();
            room.AmbientData.OutdoorAmbientVolume = 0f;
            if (tone.Clip != null)
            {
                room.AmbientData.RoomTone = tone.Clip;
                room.AmbientData.RoomToneVolume = tone.Volume;
            }
            EditorUtility.SetDirty(room);
        }

        // a room's type picked in the SDK; bunkers come from the indoor triggers, not from here
        public static void SetType(SpatialAudioRoom room, Audio.SpatialSystem.EAudioRoomTypeMask type)
        {
            if (type == Audio.SpatialSystem.EAudioRoomTypeMask.IndoorIsolated || IsBunker(room))
            {
                if (type != room._type)
                    Debug.LogWarning($"[Tarkov SDK] '{room.name}': bunker rooms follow the indoor triggers. Tick Is Bunker on the IndoorTrigger covering it " +
                                     "(or untick it to make it a plain room).", room);
                return;
            }
            Undo.RecordObject(room, "Audio room type");
            room._type = type;
            room.Outdoor = TarkovSpatialAudio.IsOutdoor(room);
            EditorUtility.SetDirty(room);
        }

        // ------------------------------------------------------------ regenerating

        public class Snapshot
        {
            public (AudioClip Clip, float Volume) Indoor, Bunker;
            public float OutdoorLevel;
        }

        public static Snapshot Capture(SpatialAudioSystem system) => system == null ? null : new Snapshot
        {
            Indoor = Current(system, bunker: false),
            Bunker = Current(system, bunker: true),
            OutdoorLevel = Indoor(system).Count > 0 ? OutdoorLevel(system) : 0f,
        };

        // new rooms (no tone yet) get what the old ones had
        public static void Apply(SpatialAudioSystem system, Snapshot snapshot)
        {
            if (system == null || snapshot == null)
                return;
            foreach (var room in Indoor(system).Where(r => r.AmbientData != null && r.AmbientData.RoomTone == null))
            {
                var tone = IsBunker(room) ? snapshot.Bunker : snapshot.Indoor;
                room.AmbientData.RoomTone = tone.Clip;
                room.AmbientData.RoomToneVolume = tone.Volume;
                room.AmbientData.OutdoorAmbientVolume = IsBunker(room) ? 0f : snapshot.OutdoorLevel;
                EditorUtility.SetDirty(room);
            }
        }

        // ------------------------------------------------------------ the Ambient Sounds window's indoor bed

        // "[Indoor] Indoor" (Map > Ambient Sounds...): with spatial audio it plays in every room on top of the
        // room tones, so it should become the room tone
        public static AudioSource IndoorBed(Scene scene) =>
            scene.GetRootGameObjects()
                 .Where(g => g.name == TarkovAmbienceWindow.RootName || g.GetComponent<AmbientAudioSystem>() != null)
                 .SelectMany(g => g.GetComponentsInChildren<AudioSource>(true))
                 .FirstOrDefault(s => s.name.Contains("[Indoor]") && !s.name.Contains("[Rain"));

        public static void UseBedAsTone(Scene scene, SpatialAudioSystem system)
        {
            var bed = IndoorBed(scene);
            if (bed == null || system == null)
                return;
            int rooms = SetTone(system, bunker: false, bed.clip, bed.volume);
            if (Current(system, bunker: true).Clip == null)
                rooms += SetTone(system, bunker: true, bed.clip, bed.volume);
            Debug.Log($"[Tarkov SDK] Room tones: '{(bed.clip != null ? bed.clip.name : "none")}' at {bed.volume:0.##} on {rooms} room(s); the indoor bed is removed.");
            Undo.DestroyObjectImmediate(bed.gameObject);
        }
    }
}
