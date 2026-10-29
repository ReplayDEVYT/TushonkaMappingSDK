using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Mortar shelling, the game's artillery (ArtilleryShellingControllerServer / Client). BaseLocalGame creates both
    // controllers when globals ArtilleryShelling.ArtilleryMapsConfigs has the map's id, and everything else is data: the
    // zones (target grids), the batteries (where the shells fly from) and the timing. The shell, its whistle and its
    // explosion ("Artillery_mine") are the game's own global assets, so nothing ships with the map.
    //
    //   planned  StartPlanedShelling: after InitShellingTimer, ZonesInShelling random zones that are UsedInPlanedShelling
    //            and IsActive; ShellingCount times, PauseBetweenShellings apart
    //   called   OnFlareSuccessEvent: a flare with CallArtilleryOnMyself fired upwards; the first UseInCalledShelling zone,
    //            moved onto the player, after InitCalledShellingTime (at most MaxCalledShellingCount at once, globals)
    //   trigger  CustomMapLoader's own: the client starts a zone's event shelling when its trigger id fires
    //
    // A zone's points are FindShellingPointsByMapGeometryForZone's grid; each shot lands within PointRadius of a random
    // picked point, dropped from y = 300 onto MOVEMENT_MASK (straight down 400 m), or at the world origin if nothing is
    // there. The zone is shelled by the brigade whose ArtilleryGuns[1] is nearest (within 10 km, else brigade 0).
    public static class TarkovMortar
    {
        public const string RootName = "SDK_Mortar";
        private const string Menu = "GameObject/Tarkov SDK/World Events/";

        // retail AlarmStages (every map): distance from the nearest picked point (m) and what the detector shows
        private static readonly Vector2[] AlarmStages = { new Vector2(12f, 0.75f), new Vector2(20f, 1.5f), new Vector2(30f, 5f), new Vector2(40f, 10000f) };

        [MenuItem(Menu + "Mortar Zone", priority = 79)]
        private static void MenuZone(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var root = EnsureRoot(SceneManager.GetActiveScene());
            var zone = CreateZone(root.transform);
            if (zone.transform.parent != root.transform)
                zone.transform.SetParent(root.transform, true);
            if (!root.GetComponentsInChildren<TarkovMortarBattery>(true).Any())
                Undo.RegisterCreatedObjectUndo(CreateBattery(root.transform, zone.transform.position + new Vector3(350f, 0f, 0f)).gameObject, "Mortar battery");
            return zone.gameObject;
        });

        [MenuItem(Menu + "Mortar Battery", priority = 80)]
        private static void MenuBattery(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var root = EnsureRoot(SceneManager.GetActiveScene());
            var battery = CreateBattery(root.transform, SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero);
            return battery.gameObject;
        });

        public static TarkovMortarSettings Find(Scene scene) =>
            scene.GetRootGameObjects().Select(g => g.GetComponentInChildren<TarkovMortarSettings>(true)).FirstOrDefault(s => s != null);

        public static TarkovMortarSettings EnsureRoot(Scene scene)
        {
            var settings = Find(scene);
            if (settings != null)
                return settings;
            var go = new GameObject(RootName) { tag = "EditorOnly" };
            SceneManager.MoveGameObjectToScene(go, scene);
            Undo.RegisterCreatedObjectUndo(go, "Mortar");
            return go.AddComponent<TarkovMortarSettings>();
        }

        public static TarkovMortarZone CreateZone(Transform parent)
        {
            var go = new GameObject("MortarZone") { tag = "EditorOnly" };
            go.transform.SetParent(parent, false);
            if (SceneView.lastActiveSceneView != null)
                go.transform.position = SceneView.lastActiveSceneView.pivot;
            var zone = go.AddComponent<TarkovMortarZone>();
            zone.Id = NextId(parent.root.GetComponentsInChildren<TarkovMortarZone>(true).Where(z => z != zone).Select(z => z.Id), 1);
            return zone;
        }

        // three guns 20 m apart, as every retail battery
        public static TarkovMortarBattery CreateBattery(Transform parent, Vector3 position)
        {
            var go = new GameObject("MortarBattery") { tag = "EditorOnly" };
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var battery = go.AddComponent<TarkovMortarBattery>();
            battery.Id = NextId(parent.root.GetComponentsInChildren<TarkovMortarBattery>(true).Where(b => b != battery).Select(b => b.Id), 1);
            for (int i = 0; i < 3; i++)
            {
                var gun = new GameObject("Gun " + (i + 1)) { tag = "EditorOnly" };
                gun.transform.SetParent(go.transform, false);
                gun.transform.localPosition = new Vector3(0f, 0f, (i - 1) * 20f);
            }
            return battery;
        }

        private static int NextId(IEnumerable<int> used, int from)
        {
            var set = new HashSet<int>(used);
            int id = from;
            while (set.Contains(id))
                id++;
            return id;
        }

        public static List<Transform> Guns(TarkovMortarBattery battery) => battery.transform.Cast<Transform>().ToList();

        // ------------------------------------------------------------ the game's geometry

        // ArtilleryShellingControllerServer.FindShellingPointsByMapGeometryForZone: the target points, before the drop
        public static List<Vector3> GridPoints(TarkovMortarZone zone, out Vector2[] corners)
        {
            float width = zone.GridStep.x * zone.Points.x, length = zone.GridStep.y * zone.Points.y;
            var zoneMatrix = Matrix4x4.TRS(zone.transform.position, Quaternion.Euler(0f, zone.transform.eulerAngles.y, 0f), new Vector3(width, 0.01f, length));
            var points = new List<Vector3>();
            corners = new Vector2[4];
            for (int i = 0; i < zone.Points.y; i++)
            {
                float z = -(length / 2f + zone.GridStep.y / 2f) + zone.GridStep.y * (i + 1);
                for (int j = 0; j < zone.Points.x; j++)
                {
                    float x = -(width / 2f + zone.GridStep.x / 2f) + zone.GridStep.x * (j + 1);
                    var local = new Vector3(width > 0f ? x / width : 0f, 0f, length > 0f ? z / length : 0f);
                    var p = zoneMatrix.MultiplyPoint3x4(local);
                    points.Add(p);
                    if (i == 0 && j == 0) corners[0] = new Vector2(p.x, p.z);
                    if (i == 0 && j == zone.Points.x - 1) corners[1] = new Vector2(p.x, p.z);
                    if (i == zone.Points.y - 1 && j == zone.Points.x - 1) corners[2] = new Vector2(p.x, p.z);
                    if (i == zone.Points.y - 1 && j == 0) corners[3] = new Vector2(p.x, p.z);
                }
            }
            return points;
        }

        // GetShootFinalPointByGeometry: straight down from 300 m; null where the game would hit the world origin
        public static Vector3? Impact(Vector3 point)
        {
            point.y = 300f;
            return Physics.Raycast(point, Vector3.down, out var hit, 400f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore) ? hit.point : (Vector3?)null;
        }

        // GetNearestBrigade: by the second gun, within 10 km
        public static TarkovMortarBattery NearestBattery(TarkovMortarZone zone, IEnumerable<TarkovMortarBattery> batteries)
        {
            TarkovMortarBattery best = null;
            float bestDistance = 10000f;
            foreach (var battery in batteries)
            {
                var guns = Guns(battery);
                if (guns.Count < 2)
                    continue;
                float d = Vector3.Distance(zone.transform.position, guns[1].position);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = battery;
                }
            }
            return best;
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            var all = roots.SelectMany(r => r.GetComponentsInChildren<TarkovMortarSettings>(true)).ToList();
            var zones = roots.SelectMany(r => r.GetComponentsInChildren<TarkovMortarZone>(true)).ToList();
            var batteries = roots.SelectMany(r => r.GetComponentsInChildren<TarkovMortarBattery>(true)).ToList();
            if (all.Count == 0)
            {
                if (zones.Count > 0 || batteries.Count > 0)
                    report.Errors.Add($"{scene.name}: mortar zones or batteries without the {RootName} settings above them; add them with GameObject > Tarkov SDK > World Events > Mortar Zone");
                return;
            }
            if (all.Count > 1 || report.Mortar != null)
                report.Errors.Add($"{scene.name}: more than one {RootName} in the map; the game has one mortar setup per map");
            var settings = all[0];
            report.Mortar ??= settings;
            if (zones.Count == 0)
            {
                report.Errors.Add($"{scene.name}: {RootName} has no zones (GameObject > Tarkov SDK > World Events > Mortar Zone)");
                return;
            }
            if (batteries.Count == 0)
                report.Errors.Add($"{scene.name}: {RootName} has no batteries, so its shells have nowhere to come from (GameObject > Tarkov SDK > World Events > Mortar Battery)");

            // ids: every zone and battery its own (the game finds them by id)
            AssignIds(zones.Cast<Component>().ToList(), z => ((TarkovMortarZone)z).Id, (z, id) => ((TarkovMortarZone)z).Id = id);
            AssignIds(batteries.Cast<Component>().ToList(), b => ((TarkovMortarBattery)b).Id, (b, id) => ((TarkovMortarBattery)b).Id = id);

            foreach (var battery in batteries)
            {
                int guns = Guns(battery).Count;
                if (guns < 2)
                    report.Errors.Add($"{scene.name}/{battery.name}: mortar battery has {guns} gun(s); the game reads the second one unchecked (give it child objects as guns, three like retail)");
                else if (guns == 2)
                    report.Warnings.Add($"{scene.name}/{battery.name}: mortar battery has 2 guns; the game never fires the last one, so all its shells come from one gun");
            }
            var usable = batteries.Where(b => Guns(b).Count >= 2).ToList();
            foreach (var zone in zones)
            {
                if (zone.Points.x < 1 || zone.Points.y < 1)
                    report.Errors.Add($"{scene.name}/{zone.name}: mortar zone has no grid points");
                if (zone.PointsPerShelling.x > zone.PointsPerShelling.y)
                    report.Warnings.Add($"{scene.name}/{zone.name}: Points Per Shelling's minimum is above its maximum");
                if (usable.Count > 0 && NearestBattery(zone, usable) == null && !usable.Any(b => b.Id == 0))
                    report.Errors.Add($"{scene.name}/{zone.name}: every mortar battery is over 10 km away, so the game looks for a battery with id 0 and throws");
                if (zone.CalledStrikeTemplate)
                    continue;
                var points = GridPoints(zone, out _);
                int missed = points.Count(p => Impact(p) == null);
                if (missed > 0)
                    report.Warnings.Add($"{scene.name}/{zone.name}: {missed} of {points.Count} target point(s) have no ground under them (nothing solid between y 300 and -100); " +
                                        "the game lands those shells at the world origin");
            }
            if (settings.PlannedShelling && !zones.Any(z => z.Planned && z.Active && !z.CalledStrikeTemplate))
                report.Warnings.Add($"{scene.name}: planned mortar shelling is on but no zone is Planned and Active, so it never fires");
            if (zones.Count(z => z.CalledStrikeTemplate) > 1)
                report.Warnings.Add($"{scene.name}: {zones.Count(z => z.CalledStrikeTemplate)} mortar zones are Called Strike Templates; the game only uses the first");
            foreach (var zone in zones.Where(z => !string.IsNullOrEmpty(z.TriggerId)))
                if (zone.TriggerDelay < settings.WarningTime)
                    report.Warnings.Add($"{scene.name}/{zone.name}: its trigger delay ({zone.TriggerDelay:0} s) is shorter than the warning time ({settings.WarningTime:0} s), so the warning comes late");
            report.MortarZones.AddRange(zones);
            report.MortarBatteries.AddRange(batteries);
        }

        private static void AssignIds(List<Component> items, System.Func<Component, int> get, System.Action<Component, int> set)
        {
            // the first of each positive id keeps it; zeros and repeats get the next free one
            var used = new HashSet<int>(items.Select(get).Where(id => id > 0));
            var taken = new HashSet<int>();
            foreach (var item in items)
            {
                int id = get(item);
                if (id > 0 && taken.Add(id))
                    continue;
                id = NextId(used, 1);
                Undo.RecordObject(item, "Mortar id");
                set(item, id);
                used.Add(id);
                taken.Add(id);
                EditorUtility.SetDirty(item);
            }
        }

        public static Vector2[] RetailAlarmStages => AlarmStages;
    }

    // Tarkov SDK > Tools > World Events > Mortar Shelling: the settings, the zones and batteries, and a preview of a strike
    public class TarkovMortarWindow : EditorWindow
    {
        private Vector2 _scroll;
        // the last preview strike's impacts (null: none) and where they missed
        private static List<Vector3> _impacts;
        private static List<Vector3> _misses;
        private static double _impactsUntil;

        [MenuItem("Tarkov SDK/Tools/World Events/Mortar Shelling...", priority = 120)]
        private static void Open() => GetWindow<TarkovMortarWindow>("Mortar Shelling").Show();

        private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;

        private void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

        private void OnGUI()
        {
            var scene = SceneManager.GetActiveScene();
            var settings = TarkovMortar.Find(scene);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Mortar shelling", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Shells fall on zones (target grids, red) from batteries (guns, orange), with the game's warning siren, the radio " +
                                       "detector's alarm and its own shell, sound and explosion. Planned shelling strikes by itself; the blue signal flare " +
                                       "(the game's mortar call) calls a strike on the player; a zone's Trigger Id starts it on that zone.", EditorStyles.wordWrappedMiniLabel);
            if (settings == null)
            {
                if (GUILayout.Button("Add Mortar Shelling (a zone and a battery)"))
                    EditorApplication.delayCall += () =>
                    {
                        var root = TarkovMortar.EnsureRoot(scene);
                        var zone = TarkovMortar.CreateZone(root.transform);
                        Undo.RegisterCreatedObjectUndo(zone.gameObject, "Mortar zone");
                        Undo.RegisterCreatedObjectUndo(TarkovMortar.CreateBattery(root.transform, zone.transform.position + new Vector3(350f, 0f, 0f)).gameObject, "Mortar battery");
                        Selection.activeGameObject = zone.gameObject;
                        EditorSceneManager.MarkSceneDirty(scene);
                    };
                EditorGUILayout.EndScrollView();
                return;
            }

            var so = new SerializedObject(settings);
            EditorGUILayout.PropertyField(so.FindProperty("PlannedShelling"));
            using (new EditorGUI.DisabledScope(!settings.PlannedShelling))
            {
                EditorGUILayout.PropertyField(so.FindProperty("FirstShellingAfter"), new GUIContent("First Shelling After (s)"));
                EditorGUILayout.PropertyField(so.FindProperty("Shellings"));
                EditorGUILayout.PropertyField(so.FindProperty("PauseBetweenShellings"), new GUIContent("Pause Between (s)"));
                EditorGUILayout.PropertyField(so.FindProperty("ZonesPerShelling"));
                EditorGUILayout.PropertyField(so.FindProperty("NewZonesEachShelling"));
            }
            EditorGUILayout.PropertyField(so.FindProperty("WarningTime"), new GUIContent("Warning Time (s)"));
            EditorGUILayout.PropertyField(so.FindProperty("CalledStrikeDelay"), new GUIContent("Called Strike Delay (s)"));
            so.ApplyModifiedProperties();

            var zones = settings.GetComponentsInChildren<TarkovMortarZone>(true);
            var batteries = settings.GetComponentsInChildren<TarkovMortarBattery>(true);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Zones ({zones.Length})", EditorStyles.boldLabel);
            foreach (var zone in zones)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var kind = zone.CalledStrikeTemplate ? "called strike template" : (zone.Planned && zone.Active ? "planned" : "not planned") +
                               (string.IsNullOrEmpty(zone.TriggerId) ? "" : $", trigger '{zone.TriggerId}'");
                    EditorGUILayout.LabelField($"{zone.Id}  {zone.name}", GUILayout.Width(150f));
                    EditorGUILayout.LabelField(kind, EditorStyles.miniLabel);
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                        Selection.activeGameObject = zone.gameObject;
                    if (GUILayout.Button(new GUIContent("Preview", "Drops one round of shells in the Scene view (the red crosses), as the game picks and lands them."), GUILayout.Width(60f)))
                        PreviewStrike(zone);
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Zone"))
                    EditorApplication.delayCall += () =>
                    {
                        var zone = TarkovMortar.CreateZone(settings.transform);
                        Undo.RegisterCreatedObjectUndo(zone.gameObject, "Mortar zone");
                        Selection.activeGameObject = zone.gameObject;
                    };
                if (GUILayout.Button(new GUIContent("Add Called Strike Template", "The size and timing of strikes called by the blue signal flare.")))
                    EditorApplication.delayCall += () =>
                    {
                        var zone = TarkovMortar.CreateZone(settings.transform);
                        Undo.RegisterCreatedObjectUndo(zone.gameObject, "Mortar zone");
                        zone.name = "CalledStrike";
                        zone.CalledStrikeTemplate = true;
                        zone.Planned = false;
                        Selection.activeGameObject = zone.gameObject;
                    };
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Batteries ({batteries.Length})", EditorStyles.boldLabel);
            foreach (var battery in batteries)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{battery.Id}  {battery.name}: {TarkovMortar.Guns(battery).Count} gun(s)");
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                        Selection.activeGameObject = battery.gameObject;
                }
            if (GUILayout.Button("Add Battery"))
                EditorApplication.delayCall += () =>
                {
                    var battery = TarkovMortar.CreateBattery(settings.transform, SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero);
                    Undo.RegisterCreatedObjectUndo(battery.gameObject, "Mortar battery");
                    Selection.activeGameObject = battery.gameObject;
                };
            EditorGUILayout.LabelField("Put batteries a few hundred metres off the map's edge, like retail: the shells fly from them (you can hear them come). " +
                                       "Each zone is shelled by the battery nearest to it.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        // one round as Shot picks them: random picked points, a random spot within Point Radius, dropped from 300 m
        private static void PreviewStrike(TarkovMortarZone zone)
        {
            var points = TarkovMortar.GridPoints(zone, out _);
            if (points.Count == 0)
                return;
            int count = Mathf.Clamp(Random.Range(zone.PointsPerShelling.x, zone.PointsPerShelling.y), 0, points.Count);
            var picked = points.OrderBy(_ => Random.value).Take(Mathf.Max(1, count)).ToList();
            _impacts = new List<Vector3>();
            _misses = new List<Vector3>();
            for (int i = 0; i < zone.ShotsPerRound * zone.Rounds; i++)
            {
                var circle = Random.insideUnitCircle * zone.PointRadius;
                var target = picked[Random.Range(0, picked.Count)] + new Vector3(circle.x, 0f, circle.y);
                var impact = TarkovMortar.Impact(target);
                if (impact != null)
                    _impacts.Add(impact.Value);
                else
                    _misses.Add(target);
            }
            _impactsUntil = EditorApplication.timeSinceStartup + 30.0;
            SceneView.RepaintAll();
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (_impacts == null || EditorApplication.timeSinceStartup > _impactsUntil)
                return;
            Handles.color = new Color(1f, 0.2f, 0.1f);
            foreach (var p in _impacts)
            {
                Handles.DrawLine(p + new Vector3(-1.5f, 0.2f, -1.5f), p + new Vector3(1.5f, 0.2f, 1.5f));
                Handles.DrawLine(p + new Vector3(-1.5f, 0.2f, 1.5f), p + new Vector3(1.5f, 0.2f, -1.5f));
            }
            Handles.color = Color.magenta;
            foreach (var p in _misses)
                Handles.Label(p, "no ground: lands at 0,0,0");
        }
    }

    public static class TarkovMortarGizmos
    {
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawZone(TarkovMortarZone zone, GizmoType type)
        {
            bool selected = (type & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0;
            var points = TarkovMortar.GridPoints(zone, out var corners);
            var color = zone.CalledStrikeTemplate ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.25f, 0.15f);
            Handles.color = new Color(color.r, color.g, color.b, selected ? 1f : 0.5f);
            float y = zone.transform.position.y + 0.3f;
            var outline = corners.Select(c => new Vector3(c.x, y, c.y)).ToList();
            outline.Add(outline[0]);
            Handles.DrawAAPolyLine(selected ? 4f : 2f, outline.ToArray());
            Handles.Label(zone.transform.position + Vector3.up * 4f, $"Mortar zone {zone.Id}" + (zone.CalledStrikeTemplate ? " (called strike template: centred on the player)" : ""));
            if (!selected)
                return;
            // each target point and how far its shells spread
            Handles.color = new Color(color.r, color.g, color.b, 0.35f);
            foreach (var p in points)
            {
                var ground = TarkovMortar.Impact(p) ?? p;
                Handles.DrawWireDisc(ground + Vector3.up * 0.2f, Vector3.up, zone.PointRadius);
                Handles.DotHandleCap(0, ground + Vector3.up * 0.2f, Quaternion.identity, HandleUtility.GetHandleSize(ground) * 0.03f, EventType.Repaint);
            }
            // the battery that shells it
            var batteries = Object.FindObjectsOfType<TarkovMortarBattery>(true).Where(b => b.gameObject.scene == zone.gameObject.scene);
            var battery = TarkovMortar.NearestBattery(zone, batteries);
            if (battery != null)
            {
                Handles.color = new Color(1f, 0.6f, 0.2f, 0.8f);
                Handles.DrawDottedLine(TarkovMortar.Guns(battery)[1].position, zone.transform.position, 6f);
            }
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawBattery(TarkovMortarBattery battery, GizmoType type)
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f, (type & GizmoType.Selected) != 0 ? 1f : 0.6f);
            var guns = TarkovMortar.Guns(battery);
            foreach (var gun in guns)
            {
                Gizmos.DrawWireSphere(gun.position, 1.5f);
                Gizmos.DrawLine(gun.position, gun.position + Vector3.up * 4f);
            }
            Handles.Label(battery.transform.position + Vector3.up * 6f, $"Mortar battery {battery.Id} ({guns.Count} gun(s))");
        }
    }
}
