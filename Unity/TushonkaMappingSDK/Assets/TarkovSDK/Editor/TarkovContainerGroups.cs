using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Container groups, as retail maps have them (527 LootableContainersGroups on the five big maps): the containers
    // under a group spawn between its Min and Max of them per raid, picked by their own spawn chance, so "3 of these 8
    // crates" or "one of these two safes". The game's LootableContainersGroup (Id, Min, Max; its editor-only OnValidate
    // copies the Id into each container's LootableContainersGroupId) is what retail data's containersGroups come from;
    // here Build Map does that, exports the groups, and the server mod picks the members each raid (SPT only randomises
    // the retail maps it knows).
    public static class TarkovContainerGroups
    {
        [MenuItem("GameObject/Tarkov SDK/Interactables/Container Group", priority = 32)]
        private static void MenuGroup(MenuCommand cmd)
        {
            // the selected containers go into the new group
            var containers = Selection.gameObjects.Select(g => g.GetComponent<LootableContainer>()).Where(c => c != null).ToList();
            var parent = (cmd.context as GameObject)?.transform;
            if (parent != null && parent.GetComponent<LootableContainer>() != null)
                parent = parent.parent;
            var group = CreateGroup(parent);
            Undo.RegisterCreatedObjectUndo(group.gameObject, "Create container group");
            if (containers.Count > 0)
            {
                var center = containers.Aggregate(Vector3.zero, (s, c) => s + c.transform.position) / containers.Count;
                group.transform.position = center;
                foreach (var container in containers)
                    Undo.SetTransformParent(container.transform, group.transform, "Group containers");
                group.Max = Mathf.Max(1, containers.Count - 1);
            }
            else if (parent == null && SceneView.lastActiveSceneView != null)
                group.transform.position = SceneView.lastActiveSceneView.pivot;
            Selection.activeGameObject = group.gameObject;
        }

        public static LootableContainersGroup CreateGroup(Transform parent)
        {
            var go = new GameObject("ContainerGroup");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var group = go.AddComponent<LootableContainersGroup>();
            group.Min = 1;
            group.Max = 2;
            return group;
        }

        // the group a container is in: the nearest one above it
        public static LootableContainersGroup GroupOf(LootableContainer container) =>
            container != null ? container.GetComponentInParent<LootableContainersGroup>(true) : null;

        public static List<LootableContainer> Members(LootableContainersGroup group) =>
            group.GetComponentsInChildren<LootableContainer>(true).Where(c => GroupOf(c) == group).ToList();

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            var seen = new HashSet<string>(report.ContainerGroups.Select(g => g.Id));
            foreach (var group in roots.SelectMany(r => r.GetComponentsInChildren<LootableContainersGroup>(true)))
            {
                if (string.IsNullOrWhiteSpace(group.Id) || !seen.Add(group.Id))
                {
                    group.Id = $"{scene.name}_{group.name.Replace(' ', '_')}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
                    seen.Add(group.Id);
                }
                var members = Members(group);
                if (members.Count == 0)
                {
                    report.Warnings.Add($"{scene.name}: container group '{group.name}' has no containers under it");
                    continue;
                }
                if (group.Min < 0)
                    group.Min = 0;
                if (group.Max < group.Min)
                {
                    report.Warnings.Add($"{scene.name}: container group '{group.name}' had Max {group.Max} below Min {group.Min}; Max is now {group.Min}");
                    group.Max = group.Min;
                }
                if (group.Max > members.Count)
                    report.Warnings.Add($"{scene.name}: container group '{group.name}' spawns up to {group.Max} but has {members.Count} container(s), so some raids get all of them");
                if (group.Min == 0 && group.Max == 0)
                    report.Warnings.Add($"{scene.name}: container group '{group.name}' has Max 0, so its containers never spawn");
                foreach (var container in members)
                {
                    container.LootableContainersGroupId = group.Id;
                    EditorUtility.SetDirty(container);
                }
                EditorUtility.SetDirty(group);
                report.ContainerGroups.Add(group);
            }
            // a container left outside every group keeps no stale group id (from a rip, or a group since deleted)
            foreach (var container in roots.SelectMany(r => r.GetComponentsInChildren<LootableContainer>(true)))
                if (GroupOf(container) == null && !string.IsNullOrEmpty(container.LootableContainersGroupId))
                {
                    container.LootableContainersGroupId = "";
                    EditorUtility.SetDirty(container);
                }
        }

        // ------------------------------------------------------------ gizmo

        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawGroup(LootableContainersGroup group, GizmoType type)
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.8f);
            foreach (var container in Members(group))
                Gizmos.DrawLine(group.transform.position, container.transform.position + Vector3.up * 0.5f);
            Gizmos.DrawWireSphere(group.transform.position, 0.3f);
        }
    }

    [CustomEditor(typeof(LootableContainersGroup))]
    internal class TarkovContainerGroupEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var group = (LootableContainersGroup)target;
            var members = TarkovContainerGroups.Members(group);
            EditorGUILayout.HelpBox($"Each raid spawns between Min and Max of the {members.Count} container(s) under this object, picked by their Spawn Chance " +
                                    "(a container's chance is its weight in the pick). The rest aren't in that raid. Id is made when the map is built.", MessageType.None);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Id"));
            var min = serializedObject.FindProperty("Min");
            var max = serializedObject.FindProperty("Max");
            EditorGUILayout.IntSlider(min, 0, Mathf.Max(1, members.Count));
            EditorGUILayout.IntSlider(max, 0, Mathf.Max(1, members.Count));
            if (max.intValue < min.intValue)
                max.intValue = min.intValue;
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Containers", EditorStyles.boldLabel);
            foreach (var container in members)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(container.name, $"chance {container.SpawnChance}%");
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                        Selection.activeObject = container.gameObject;
                }
            if (members.Count == 0)
                EditorGUILayout.LabelField("None: put Lootable Containers under this object.", EditorStyles.miniLabel);
            var selected = Selection.gameObjects.Select(g => g.GetComponent<LootableContainer>()).Where(c => c != null && !members.Contains(c)).ToList();
            if (selected.Count > 0 && GUILayout.Button($"Move {selected.Count} Selected Container(s) Into This Group"))
                foreach (var container in selected)
                    Undo.SetTransformParent(container.transform, group.transform, "Group containers");

            if (members.Count == 0 || !PreviewGui.Begin("container group"))
                return;
            if (PreviewGui.Button("Roll A Raid", "Picks the containers one raid would have, as the server does, and hides the rest. Press again for another raid."))
                TarkovPreview.Get<ContainerGroupSim>(group).Roll();
            PreviewGui.End(group);
        }
    }
}
