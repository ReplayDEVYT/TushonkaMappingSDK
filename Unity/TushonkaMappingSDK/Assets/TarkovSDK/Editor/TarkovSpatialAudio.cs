using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using Audio.SpatialSystem.Utils;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // The game's spatial audio (Audio.SpatialSystem), laid out as retail sound scenes (Labyrinth_Sound):
    //
    //   SpatialAudioSystem   GuidComponent + SpatialAudioCrossSceneGroup + SpatialAudioSystem. The game
    //                        initialises the system while the raid loads (TarkovApplication.LoadMapAndData);
    //                        its rooms are only the ones under a cross-scene group (AudioRoomStorage).
    //     Rooms              SpatialAudioRooms, each with AudioTriggerArea boxes (a 1 m BoxCollider scaled to
    //                        the area) on the Triggers layer. A position in several rooms is in the smallest,
    //                        so one large outdoor room covers the map around the buildings.
    //     Portals            SpatialAudioPortals between two rooms: a door (Opening, DoorID = the door's Id),
    //                        a window (Window, DoorID = the window's Id) or an opening always open (Static),
    //                        a 1 x 1 x 0 box scaled to the portal, its forward along the portal's normal.
    //                        One between an indoor and an outdoor room is ToOutdoor: the outdoor ambience
    //                        comes in through it (OutdoorPortalsDataContainer, EnvironmentSoundBlendSystem).
    //
    // Sound between rooms follows the baked routes (TarkovAudioBake: the .audiobakedata file named by the
    // SpatialAudioLocationInfo, read from the map's audio folder by CustomMapLoader), occluded by closed
    // portals and the rooms' walls. A room's type picks its reverb, delay and effect sends from the ambient
    // system's mixer effects (MixerEffectsDataSO, by EAudioRoomTypeMask).
    public static class TarkovSpatialAudio
    {
        public const string RootName = "SpatialAudioSystem";
        public const string RoomsName = "Rooms";
        public const string PortalsName = "Portals";
        public const string GeneratedName = "Generated";
        public const string OutdoorRoomName = "Outdoor";
        // CustomMapLoader looks for the bake file by name in the map's audio folder; a path no retail map uses
        public const string BakePathPrefix = "CustomMapLoader/";

        public static int TriggersLayer => LayerMask.NameToLayer("Triggers");

        public static SpatialAudioSystem Find(Scene scene) =>
            scene.GetRootGameObjects().Select(g => g.GetComponentInChildren<SpatialAudioSystem>(true)).FirstOrDefault(s => s != null);

        public static SpatialAudioSystem FindAny() => Object.FindObjectsOfType<SpatialAudioSystem>(true).FirstOrDefault();

        public static Transform Rooms(SpatialAudioSystem system) => Folder(system.transform, RoomsName);
        public static Transform Portals(SpatialAudioSystem system) => Folder(system.transform, PortalsName);

        public static Transform Folder(Transform parent, string name)
        {
            var child = parent.Find(name);
            if (child != null)
                return child;
            child = new GameObject(name).transform;
            child.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(child.gameObject, "Create " + name);
            return child;
        }

        // the scene's folder for its audio assets and baked files
        public static string AudioFolder(Scene scene)
        {
            var folder = Path.Combine(Path.GetDirectoryName(scene.path) ?? "Assets", "Audio").Replace('\\', '/');
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static string BakeFileName(Scene scene) => scene.name + ".audiobakedata";

        public static string BakeFilePath(Scene scene) => $"{AudioFolder(scene)}/{BakeFileName(scene)}";

        // ------------------------------------------------------------ system

        public static SpatialAudioSystem Create(Scene scene)
        {
            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            var guid = root.AddComponent<GuidComponent>();
            guid.serializedGuid = Guid.NewGuid().ToByteArray();
            var group = root.AddComponent<SpatialAudioCrossSceneGroup>();
            group.crossSceneGroup = new GuidReference { serializedGuid = guid.serializedGuid.ToArray() };
            group.AudioRooms = new List<SpatialAudioRoom>();
            group.AudioPortals = new List<BaseSpatialAudioPortal>();
            var system = root.AddComponent<SpatialAudioSystem>();
            // retail's pool sizes (30 / 30 / 30 / 5) are the class defaults
            system.poolsConfig = new SpatialAudioPoolsConfig();
            system.OcclusionSettings = OcclusionSettings(scene);
            system._locationInfo = LocationInfo(scene);
            Folder(root.transform, RoomsName);
            Folder(root.transform, PortalsName);
            Undo.RegisterCreatedObjectUndo(root, "Create spatial audio");
            return system;
        }

        // the backend's location occlusion settings replace these values when the raid loads
        // (SpatialAudioSystem.method_0), so the class defaults are only a fallback
        public static AudioOcclusionSettings OcclusionSettings(Scene scene)
        {
            var path = $"{AudioFolder(scene)}/{scene.name}_AudioOcclusion.asset";
            var settings = AssetDatabase.LoadAssetAtPath<AudioOcclusionSettings>(path);
            if (settings != null)
                return settings;
            settings = ScriptableObject.CreateInstance<AudioOcclusionSettings>();
            settings.commonSettings ??= new CommonOcclusionSettings();
            settings.propagationSettings ??= new PropagationSettings();
            settings.reflectionSettings ??= new ReflectionSettings();
            settings.diffractionSettings ??= new DiffractionSettings();
            settings.transmissionSettings ??= new TransmissionSettings();
            AssetDatabase.CreateAsset(settings, path);
            return settings;
        }

        public static SpatialAudioLocationInfo LocationInfo(Scene scene)
        {
            var path = $"{AudioFolder(scene)}/{scene.name}_SpatialAudioInfo.asset";
            var info = AssetDatabase.LoadAssetAtPath<SpatialAudioLocationInfo>(path);
            if (info != null)
                return info;
            info = ScriptableObject.CreateInstance<SpatialAudioLocationInfo>();
            info.relativeBakeDataPath = BakePathPrefix + BakeFileName(scene);
            info.bakeSettings = new LocationBakeSettings();
            AssetDatabase.CreateAsset(info, path);
            return info;
        }

        // ------------------------------------------------------------ rooms

        public static bool IsOutdoor(SpatialAudioRoom room) => (room._type & EAudioRoomTypeMask.Outdoor) != 0;

        public static SpatialAudioRoom CreateRoom(Transform parent, string name, IEnumerable<Bounds> boxes, bool outdoor,
                                                  EAudioRoomTypeMask type = EAudioRoomTypeMask.IndoorGeneric)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var room = go.AddComponent<SpatialAudioRoom>();
            room.Outdoor = outdoor;
            room._type = outdoor ? EAudioRoomTypeMask.OutdoorGeneric : type;
            room.WallOcclusion = outdoor ? 0.5f : 0.6f;
            room.Areas = new List<AudioTriggerArea>();
            room.roomConnections = new List<SpatialAudioRoom.RoomConnection>();
            room.OnlyWired = true;
            room.AmbientData = new RoomAmbientData();
            if (outdoor)
            {
                room.AmbientData.OutdoorAmbientVolume = 1f;
                room.AmbientData.PrecipitationVolume = 1f;
            }
            else
            {
                // retail indoor rooms: no outdoor ambience away from the openings, room tones at 0.4
                room.AmbientData.OutdoorAmbientVolume = 0f;
                room.AmbientData.RoomToneVolume = TarkovRoomTones.DefaultVolume;
            }
            foreach (var box in boxes)
                AddArea(room, box);
            return room;
        }

        // retail's area: a 1 m trigger box on its own child, scaled to the area
        public static AudioTriggerArea AddArea(SpatialAudioRoom room, Bounds box)
        {
            var go = new GameObject("AudioTriggerArea") { layer = TriggersLayer };
            go.transform.SetParent(room.transform, false);
            go.transform.SetPositionAndRotation(box.center, Quaternion.identity);
            var parentScale = room.transform.lossyScale;
            go.transform.localScale = new Vector3(box.size.x / parentScale.x, box.size.y / parentScale.y, box.size.z / parentScale.z);
            var collider = go.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = Vector3.one;
            collider.center = Vector3.zero;
            var area = go.AddComponent<AudioTriggerArea>();
            area.areaCollider = collider;
            area._validatePlayerCenterInside = true;
            room.Areas ??= new List<AudioTriggerArea>();
            room.Areas.Add(area);
            return area;
        }

        public static Bounds RoomBounds(SpatialAudioRoom room)
        {
            var areas = (room.Areas ?? new List<AudioTriggerArea>()).Where(a => a != null).ToList();
            if (areas.Count == 0)
                return new Bounds(room.transform.position, Vector3.zero);
            var bounds = TarkovScenePreparer.TransformBounds(areas[0].transform);
            foreach (var area in areas.Skip(1))
                bounds.Encapsulate(TarkovScenePreparer.TransformBounds(area.transform));
            return bounds;
        }

        // ------------------------------------------------------------ portals

        public static SpatialAudioPortal CreatePortal(Transform parent, string name, Vector3 center, Quaternion rotation, Vector2 size,
                                                      BaseSpatialAudioPortal.PortalType type, SpatialAudioRoom front, SpatialAudioRoom back,
                                                      bool closed = false)
        {
            var go = new GameObject(name) { layer = TriggersLayer };
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(center, rotation);
            // GetPortalSize reads the box's size times the local scale: keep its parents unscaled
            go.transform.localScale = new Vector3(Mathf.Max(0.1f, size.x), Mathf.Max(0.1f, size.y), 0f);
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(1f, 1f, 0f);
            var portal = go.AddComponent<SpatialAudioPortal>();
            portal.portalCollider = box;
            portal.portalName = name;
            portal.portalType = type;
            bool closable = type != BaseSpatialAudioPortal.PortalType.Static;
            portal.state = closable && closed ? BaseSpatialAudioPortal.PortalState.Closed : BaseSpatialAudioPortal.PortalState.Open;
            // how much a closed one lets through: a door a little, a window more
            portal._transmission = type == BaseSpatialAudioPortal.PortalType.Window ? 0.35f : type == BaseSpatialAudioPortal.PortalType.Opening ? 0.15f : 0f;
            portal.Depth = 3f;
            portal.traversalMaxCost = 0f;
            portal.openEnvelope = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            portal.closeEnvelope = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            portal.openFadeTime = 0.3f;
            portal.closeFadeTime = 0.3f;
            portal.DoorID = "";
            portal.ToOutdoor = IsOutdoor(front) != IsOutdoor(back);
            portal._connectedRooms = new List<SpatialAudioRoom> { front, back };
            Connect(front, back, portal);
            return portal;
        }

        // both rooms list the other through the portal, one connection per portal, as retail
        public static void Connect(SpatialAudioRoom a, SpatialAudioRoom b, BaseSpatialAudioPortal portal)
        {
            a.roomConnections ??= new List<SpatialAudioRoom.RoomConnection>();
            b.roomConnections ??= new List<SpatialAudioRoom.RoomConnection>();
            if (!a.roomConnections.Any(c => c.connectedRoom == b && c.connectingPortals != null && c.connectingPortals.Contains(portal)))
                a.roomConnections.Add(new SpatialAudioRoom.RoomConnection { connectedRoom = b, connectingPortals = new[] { portal } });
            if (!b.roomConnections.Any(c => c.connectedRoom == a && c.connectingPortals != null && c.connectingPortals.Contains(portal)))
                b.roomConnections.Add(new SpatialAudioRoom.RoomConnection { connectedRoom = a, connectingPortals = new[] { portal } });
            EditorUtility.SetDirty(a);
            EditorUtility.SetDirty(b);
        }

        public static void Disconnect(BaseSpatialAudioPortal portal)
        {
            foreach (var room in Object.FindObjectsOfType<SpatialAudioRoom>(true))
                if (room.roomConnections != null && room.roomConnections.RemoveAll(c => c.connectingPortals != null && c.connectingPortals.Contains(portal)) > 0)
                    EditorUtility.SetDirty(room);
        }

        public static Vector3 PortalCenter(BaseSpatialAudioPortal portal) => portal.transform.position;

        // ------------------------------------------------------------ build

        // Ids, the cross-scene group's lists, door and window links and the portals' rooms, as BSG's tools
        // leave them; then the route bake. Returns the bake file to ship, or null.
        public static string Prepare(Scene scene, TarkovScenePreparer.Report report, bool forceRoutes = false)
        {
            var system = Find(scene);
            if (system == null)
                return null;
            // before the routes hash: bunkers are a room type
            TarkovRoomTones.SyncBunkers(scene, system);
            var rooms = system.GetComponentsInChildren<SpatialAudioRoom>(true).Where(r => r.gameObject.activeInHierarchy).ToList();
            var portals = system.GetComponentsInChildren<BaseSpatialAudioPortal>(true).Where(p => p.gameObject.activeInHierarchy).ToList();
            if (rooms.Count == 0)
            {
                report.Warnings.Add($"{scene.name}: the spatial audio system has no rooms (generate them in Tarkov SDK > Generation > Audio > Spatial Audio...); sounds play unoccluded");
                return null;
            }
            if (rooms.Count > short.MaxValue || portals.Count > short.MaxValue)
            {
                report.Errors.Add($"{scene.name}: too many audio rooms or portals ({rooms.Count}, {portals.Count})");
                return null;
            }

            if (system._locationInfo == null)
                system._locationInfo = LocationInfo(scene);
            if (system.OcclusionSettings == null)
                system.OcclusionSettings = OcclusionSettings(scene);
            system.poolsConfig ??= new SpatialAudioPoolsConfig();
            system._locationInfo.relativeBakeDataPath = BakePathPrefix + BakeFileName(scene);
            system._locationInfo.bakeSettings ??= new LocationBakeSettings();
            EditorUtility.SetDirty(system);

            var group = system.GetComponent<SpatialAudioCrossSceneGroup>();
            if (group == null)
            {
                report.Errors.Add($"{scene.name}/{system.name}: no SpatialAudioCrossSceneGroup next to the SpatialAudioSystem; the game finds no rooms without it");
                return null;
            }
            group.AudioRooms = rooms;
            group.AudioPortals = portals;
            var guid = system.GetComponent<GuidComponent>();
            if (guid != null && (guid.serializedGuid == null || guid.serializedGuid.Length != 16))
                guid.serializedGuid = Guid.NewGuid().ToByteArray();
            if (guid != null)
                group.crossSceneGroup = new GuidReference { serializedGuid = guid.serializedGuid.ToArray() };
            EditorUtility.SetDirty(group);

            int triggers = TriggersLayer;
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                room._iD = (short)(i + 1);
                room.Areas = (room.Areas ?? new List<AudioTriggerArea>()).Where(a => a != null).ToList();
                foreach (var area in room.GetComponentsInChildren<AudioTriggerArea>(true))
                    if (!room.Areas.Contains(area) && area.GetComponentInParent<SpatialAudioRoom>(true) == room)
                        room.Areas.Add(area);
                if (room.Areas.Count == 0)
                    report.Errors.Add($"{scene.name}/{room.name}: audio room has no AudioTriggerArea; the game can't tell when a player is in it");
                foreach (var area in room.Areas)
                {
                    area.gameObject.layer = triggers;
                    area.areaCollider ??= area.GetComponent<BoxCollider>();
                    if (area.areaCollider == null)
                        report.Errors.Add($"{scene.name}/{room.name}/{area.name}: audio area without a BoxCollider");
                    else
                        area.areaCollider.isTrigger = true;
                    EditorUtility.SetDirty(area);
                }
                room.roomConnections = (room.roomConnections ?? new List<SpatialAudioRoom.RoomConnection>())
                    .Where(c => c != null && c.connectedRoom != null && c.connectingPortals != null && c.connectingPortals.Length > 0)
                    .Select(c => { c.connectingPortals = c.connectingPortals.Where(p => p != null).ToArray(); return c; })
                    .Where(c => c.connectingPortals.Length > 0).ToList();
                room.AmbientData ??= new RoomAmbientData();
                room.Outdoor = IsOutdoor(room);
                room._bounds = RoomBounds(room);
                room._roomSize = room._bounds.size.x * room._bounds.size.y * room._bounds.size.z;
                EditorUtility.SetDirty(room);
            }
            if (!rooms.Any(IsOutdoor))
                report.Warnings.Add($"{scene.name}: no outdoor audio room; outside every room the game's ambience treats the player as nowhere (generate the rooms to get one)");

            // the game loads every portal in the scene, and only finds rooms under the cross-scene group
            foreach (var stray in Object.FindObjectsOfType<BaseSpatialAudioPortal>(true).Where(p => p.gameObject.scene == scene && !p.transform.IsChildOf(system.transform)))
                report.Errors.Add($"{scene.name}/{stray.name}: audio portal outside the SpatialAudioSystem object; move it under {system.name}/{PortalsName}");
            foreach (var stray in Object.FindObjectsOfType<SpatialAudioRoom>(true).Where(r => r.gameObject.scene == scene && !r.transform.IsChildOf(system.transform)))
                report.Warnings.Add($"{scene.name}/{stray.name}: audio room outside the SpatialAudioSystem object is ignored by the game; move it under {system.name}/{RoomsName}");

            var doors = Object.FindObjectsOfType<Door>(true);
            var windows = Object.FindObjectsOfType<WindowBreaker>(true);
            for (int i = 0; i < portals.Count; i++)
            {
                var portal = portals[i];
                portal._iD = (short)(i + 1);
                portal.gameObject.layer = triggers;
                portal.portalCollider ??= portal.GetComponent<BoxCollider>();
                if (portal.portalCollider == null)
                    report.Errors.Add($"{scene.name}/{portal.name}: audio portal without a BoxCollider");
                var connected = rooms.Where(r => r.roomConnections.Any(c => c.connectingPortals.Contains(portal))).ToList();
                // the game reads both rooms of every portal unchecked while the raid loads (CreatePortalData)
                if (connected.Count != 2)
                    report.Errors.Add($"{scene.name}/{portal.name}: audio portal joins {connected.Count} room(s) instead of 2; the raid fails to load. " +
                                      "Delete it or connect it (generate the rooms again)");
                // front first: keep the order it was made with
                portal._connectedRooms = (portal._connectedRooms ?? new List<SpatialAudioRoom>()).Where(connected.Contains).ToList();
                foreach (var room in connected)
                    if (!portal._connectedRooms.Contains(room) && portal._connectedRooms.Count < 2)
                        portal._connectedRooms.Add(room);
                portal.ToOutdoor = portal._connectedRooms.Count == 2 && IsOutdoor(portal._connectedRooms[0]) != IsOutdoor(portal._connectedRooms[1]);
                if (portal is SpatialAudioPortal linked)
                    LinkOpening(scene, linked, doors, windows, report);
                EditorUtility.SetDirty(portal);
            }

            var path = BakeFilePath(scene);
            var state = TarkovAudioBakeState.Load(scene);
            var hash = TarkovAudioBakeState.RoutesHash(system);
            bool exists = File.Exists(path);
            if (exists && !forceRoutes && state.RoutesOnBuild != TarkovAudioBakeState.RouteMode.Always
                && (state.RoutesOnBuild == TarkovAudioBakeState.RouteMode.Never || state.Routes == hash))
            {
                if (state.Routes != hash)
                    report.Warnings.Add($"{scene.name}: audio rooms or portals changed but sound routes are set to never rebake on build; " +
                                        "bake them in Tarkov SDK > Generation > Audio > Spatial Audio... or the raid may fail to load them");
                return path;
            }
            var result = TarkovAudioBake.Bake(system, path);
            state.Routes = hash;
            state.Save(scene);
            EditorUtility.SetDirty(system._locationInfo);
            if (result.Pairs == 0)
                report.Warnings.Add($"{scene.name}: no audio rooms are connected by portals; sound between rooms is only heard through walls");
            return path;
        }

        // the game opens and closes a portal with the door (or breaks it with the window) whose Id is its
        // DoorID and that is within 15 m (IsPositionIdentical); the nearest one within 2 m is taken
        private static void LinkOpening(Scene scene, SpatialAudioPortal portal, Door[] doors, WindowBreaker[] windows, TarkovScenePreparer.Report report)
        {
            var center = PortalCenter(portal);
            if (portal.portalType == BaseSpatialAudioPortal.PortalType.Opening)
            {
                var door = doors.OrderBy(d => Vector3.Distance(DoorCenter(d), center)).FirstOrDefault();
                if (door != null && Vector3.Distance(DoorCenter(door), center) < 2f && !string.IsNullOrEmpty(door.Id))
                {
                    portal.DoorID = door.Id;
                    portal.state = door._doorState == EDoorState.Open ? BaseSpatialAudioPortal.PortalState.Open : BaseSpatialAudioPortal.PortalState.Closed;
                }
                else
                {
                    report.Warnings.Add($"{scene.name}/{portal.name}: door portal with no door within 2 m; it stays {(portal.state == BaseSpatialAudioPortal.PortalState.Open ? "open" : "closed")}");
                    portal.DoorID = "";
                }
            }
            else if (portal.portalType == BaseSpatialAudioPortal.PortalType.Window)
            {
                var window = windows.OrderBy(w => Vector3.Distance(w.transform.position, center)).FirstOrDefault();
                if (window != null && Vector3.Distance(WindowCenter(window), center) < 2f && !string.IsNullOrEmpty(window.Id))
                {
                    portal.DoorID = window.Id;
                    portal.state = BaseSpatialAudioPortal.PortalState.Closed;
                }
                else
                {
                    report.Warnings.Add($"{scene.name}/{portal.name}: window portal with no breakable window within 2 m; it stays closed");
                    portal.DoorID = "";
                }
            }
        }

        public static Vector3 DoorCenter(Door door)
        {
            var renderers = door.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return door.transform.position;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1))
                bounds.Encapsulate(r.bounds);
            return bounds.center;
        }

        public static Vector3 WindowCenter(WindowBreaker window)
        {
            var renderer = window.GetComponent<Renderer>();
            return renderer != null ? renderer.bounds.center : window.transform.position;
        }
    }
}
