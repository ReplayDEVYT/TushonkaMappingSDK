using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Scene view drawing for game components. Kept in the editor assembly so the stubs stay
    // field-only.
    public static class TarkovGizmos
    {
        private static readonly Color PmcColor = new Color(0.2f, 0.8f, 1f);
        private static readonly Color ScavColor = new Color(1f, 0.85f, 0.2f);
        private static readonly Color OtherColor = new Color(0.8f, 0.8f, 0.8f);
        private static readonly Color ExfilColor = new Color(0.2f, 1f, 0.3f, 0.35f);
        private static readonly Color PatrolColor = new Color(1f, 0.4f, 0.1f);

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawSpawnPoint(SpawnPointMarker marker, GizmoType type)
        {
            var sp = marker._spawnPoint;
            var sides = sp != null ? sp.Sides : EPlayerSideMask.None;
            var color = (sides & EPlayerSideMask.Pmc) != 0 ? PmcColor : (sides & EPlayerSideMask.Savage) != 0 ? ScavColor : OtherColor;

            var t = marker.transform;
            var pos = t.position;
            Gizmos.color = color;
            Gizmos.DrawWireCube(pos + Vector3.up * 0.9f, new Vector3(0.6f, 1.8f, 0.6f));
            var forward = Quaternion.Euler(0f, t.eulerAngles.y, 0f) * Vector3.forward;
            Gizmos.DrawLine(pos + Vector3.up * 1.5f, pos + Vector3.up * 1.5f + forward * 1.2f);

            if ((type & GizmoType.Selected) != 0)
            {
                var sphere = marker.GetComponent<SphereCollider>();
                if (sphere != null)
                {
                    Gizmos.color = new Color(color.r, color.g, color.b, 0.25f);
                    Gizmos.DrawWireSphere(t.TransformPoint(sphere.center), sphere.radius * t.lossyScale.x);
                }
                if (sp != null)
                    Handles.Label(pos + Vector3.up * 2.2f, $"{marker.name}\n{sp.Sides} / {sp.Categories}\n{sp.Infiltration}");
            }
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawBotZone(BotZone zone, GizmoType type)
        {
            Handles.color = ScavColor;
            Handles.Label(zone.transform.position + Vector3.up * 3f, zone.name);
            if ((type & GizmoType.Selected) == 0 || zone.SpawnPointMarkers == null)
                return;
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.5f);
            foreach (var marker in zone.SpawnPointMarkers)
                if (marker != null)
                    Gizmos.DrawLine(zone.transform.position, marker.transform.position);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawPatrolWay(PatrolWay way, GizmoType type)
        {
            Gizmos.color = PatrolColor;
            PatrolPoint previous = null;
            PatrolPoint first = null;
            foreach (Transform child in way.transform)
            {
                var point = child.GetComponent<PatrolPoint>();
                if (point == null)
                    continue;
                Gizmos.DrawWireSphere(point.transform.position + Vector3.up * 0.2f, 0.35f);
                if (previous != null)
                    Gizmos.DrawLine(previous.transform.position + Vector3.up * 0.2f, point.transform.position + Vector3.up * 0.2f);
                first ??= point;
                previous = point;
            }
            if (first != null && previous != null && first != previous)
            {
                Gizmos.color = new Color(PatrolColor.r, PatrolColor.g, PatrolColor.b, 0.35f);
                Gizmos.DrawLine(previous.transform.position + Vector3.up * 0.2f, first.transform.position + Vector3.up * 0.2f);
            }
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawExfil(ExfiltrationPoint exfil, GizmoType type)
        {
            var box = exfil.GetComponent<BoxCollider>();
            Gizmos.color = ExfilColor;
            if (box != null)
            {
                Gizmos.matrix = exfil.transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else
            {
                Gizmos.DrawSphere(exfil.transform.position, 1f);
            }
            var label = exfil.Settings != null ? exfil.Settings.Name : exfil.name;
            Handles.Label(exfil.transform.position + Vector3.up * 3f, "EXFIL " + label);
        }

        private static readonly Color LootColor = new Color(1f, 0.75f, 0.1f);

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawLootSpawn(TarkovLootSpawn spawn, GizmoType type)
        {
            var pos = spawn.transform.position;
            Gizmos.color = spawn.Items.Count == 0 ? Color.red : spawn.AlwaysSpawn ? Color.green : LootColor;
            Gizmos.DrawWireCube(pos + Vector3.up * 0.15f, new Vector3(0.3f, 0.3f, 0.3f));
            if ((type & GizmoType.Selected) != 0)
                Handles.Label(pos + Vector3.up * 0.5f, spawn.Items.Count == 0 ? "LOOT (no items)"
                    : $"LOOT {(spawn.AlwaysSpawn ? "always" : $"{spawn.Probability:P0}")}: {string.Join(", ", spawn.Items.ConvertAll(i => string.IsNullOrEmpty(i.Name) ? i.Template : i.Name))}");
        }

        private static readonly Color TransitColor = new Color(0.7f, 0.3f, 1f, 0.35f);
        private static readonly Color IndoorColor = new Color(0.3f, 0.55f, 1f);

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawTransit(TransitPoint transit, GizmoType type)
        {
            var box = transit.GetComponent<BoxCollider>();
            Gizmos.color = TransitColor;
            if (box != null)
            {
                Gizmos.matrix = transit.transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            var p = transit.parameters;
            var label = p != null ? $"TRANSIT {p.id} -> {(string.IsNullOrEmpty(p.location) ? "?" : p.location)}" : "TRANSIT";
            Handles.Label(transit.transform.position + Vector3.up * 3f, label);
        }

        // the game treats the trigger's transform as a unit cube: scale it to cover the interior
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawIndoorTrigger(EFT.EnvironmentEffect.IndoorTrigger trigger, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.matrix = trigger.transform.localToWorldMatrix;
            Gizmos.color = new Color(IndoorColor.r, IndoorColor.g, IndoorColor.b, selected ? 0.25f : 0.08f);
            Gizmos.DrawCube(Vector3.zero, Vector3.one);
            Gizmos.color = IndoorColor;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
            if (selected)
                Handles.Label(trigger.transform.position, "INDOOR " + trigger.name);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawLocationScene(LocationScene scene, GizmoType type)
        {
            Handles.Label(scene.transform.position, "LocationScene");
        }
    }
}
