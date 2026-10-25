using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Audio.SpatialSystem;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // What the last audio bakes were made from, so a bake can skip what hasn't changed:
    //
    //   routes      the rooms, their areas and types, the portals, their rooms and the bake settings
    //   geometries  per acoustic geometry: every mesh it takes (its transform, the mesh and its model's
    //               import), and each mesh's acoustic material
    //   map         all geometries plus the acoustic map's own settings; Draft when it was a quick bake
    //
    // Kept in the scene's Audio folder as <scene>_AudioBake.json. Meta's own hash only covers transforms
    // and isn't saved, so it can't tell an edited mesh or material.
    public class TarkovAudioBakeState
    {
        public enum RouteMode { Auto, Always, Never }

        public string Routes;
        public RouteMode RoutesOnBuild = RouteMode.Auto;
        public Dictionary<string, string> Geometries = new Dictionary<string, string>();
        public string Map;
        public bool MapDraft;

        // the scene's room tones (TarkovRoomTones), as clip asset GUIDs: kept here so a tone can be picked
        // before any room of its kind exists, and new rooms get it
        public string IndoorTone, BunkerTone;
        public float IndoorToneVolume = TarkovRoomTones.DefaultVolume, BunkerToneVolume = TarkovRoomTones.DefaultVolume;

        private static string PathFor(Scene scene) => $"{TarkovSpatialAudio.AudioFolder(scene)}/{scene.name}_AudioBake.json";

        public static TarkovAudioBakeState Load(Scene scene)
        {
            var path = PathFor(scene);
            try
            {
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<TarkovAudioBakeState>(File.ReadAllText(path)) ?? new TarkovAudioBakeState();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tarkov SDK] Could not read {path} ({e.Message}); the next audio bakes redo everything.");
            }
            return new TarkovAudioBakeState();
        }

        public void Save(Scene scene)
        {
            File.WriteAllText(PathFor(scene), JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        // ------------------------------------------------------------ routes

        public static string RoutesHash(SpatialAudioSystem system)
        {
            var hash = new Hash128();
            var settings = system._locationInfo?.bakeSettings;
            if (settings != null)
                foreach (var v in new[] { settings.maxDepthIndoorToIndoor, settings.maxDepthIndoorToOutdoor, settings.maxDepthOutdoorToOutdoor,
                                          settings.maxDistanceIndoorToIndoor, settings.maxDistanceIndoorToOutdoor, settings.maxDistanceOutdoorToOutdoor })
                    hash.Append(v);
            foreach (var room in system.GetComponentsInChildren<SpatialAudioRoom>(true).Where(r => r.gameObject.activeInHierarchy))
            {
                hash.Append(room._iD);
                hash.Append((int)room._type);
                foreach (var area in room.Areas ?? new List<AudioTriggerArea>())
                    if (area != null)
                        Append(ref hash, area.transform.localToWorldMatrix);
                foreach (var connection in room.roomConnections ?? new List<SpatialAudioRoom.RoomConnection>())
                    if (connection?.connectedRoom != null)
                    {
                        hash.Append(connection.connectedRoom._iD);
                        foreach (var portal in connection.connectingPortals ?? new BaseSpatialAudioPortal[0])
                            if (portal != null)
                                hash.Append(portal._iD);
                    }
            }
            foreach (var portal in system.GetComponentsInChildren<BaseSpatialAudioPortal>(true).Where(p => p.gameObject.activeInHierarchy))
            {
                hash.Append(portal._iD);
                Append(ref hash, portal.transform.localToWorldMatrix);
            }
            return hash.ToString();
        }

        // ------------------------------------------------------------ acoustics

        // what one geometry file is made from: the meshes that count (TarkovAcoustics.Select), or the terrain
        public static string SourceHash(TarkovAcoustics.Source source)
        {
            var hash = new Hash128();
            hash.Append(source.Name ?? "");
            if (source.Terrain != null)
            {
                Append(ref hash, source.Terrain.transform.localToWorldMatrix);
                AppendAsset(ref hash, source.Terrain.terrainData);
                var own = TarkovAcoustics.AcousticMaterialType != null ? source.Terrain.GetComponent(TarkovAcoustics.AcousticMaterialType) : null;
                AppendAsset(ref hash, own != null ? new SerializedObject(own).FindProperty("properties")?.objectReferenceValue : null);
                return hash.ToString();
            }
            foreach (var (filter, properties) in source.Meshes)
            {
                var mesh = filter.sharedMesh;
                Append(ref hash, filter.transform.localToWorldMatrix);
                hash.Append(mesh.name);
                hash.Append(mesh.vertexCount);
                Append(ref hash, mesh.bounds);
                // a re-imported or edited model changes this, even with the same vertex count
                AppendAsset(ref hash, mesh);
                AppendAsset(ref hash, properties);
            }
            return hash.ToString();
        }

        private static void AppendAsset(ref Hash128 hash, Object asset)
        {
            var path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            hash.Append(string.IsNullOrEmpty(path) ? "-" : AssetDatabase.GetAssetDependencyHash(path).ToString());
        }

        public static string MapHash(Component map, IEnumerable<string> geometryHashes)
        {
            var hash = new Hash128();
            foreach (var h in geometryHashes.OrderBy(h => h, StringComparer.Ordinal))
                hash.Append(h);
            var so = new SerializedObject(map);
            foreach (var name in new[] { "ReflectionCount", "MinSpacing", "MaxSpacing", "HeadHeight", "MaxHeight", "Flags", "relativeFilePath" })
            {
                var p = so.FindProperty(name);
                if (p != null)
                    hash.Append(p.propertyType == SerializedPropertyType.String ? p.stringValue
                                : p.propertyType == SerializedPropertyType.Float ? p.floatValue.ToString("R") : p.longValue.ToString());
            }
            Append(ref hash, map.transform.localToWorldMatrix);
            return hash.ToString();
        }

        private static void Append(ref Hash128 hash, Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++)
                hash.Append(Mathf.Round(m[i] * 1000f));
        }

        private static void Append(ref Hash128 hash, Bounds b)
        {
            Append(ref hash, Matrix4x4.TRS(b.center, Quaternion.identity, b.size));
        }
    }
}
