using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Keeps the project's tags, layers, physics collision matrix and navmesh areas/agents identical
    // to the game's. Layer indices and nav agent type ids are baked into scenes and navmesh data, so
    // a mismatch silently breaks collisions, bullets and bot pathing in game.
    //
    // Values were read from EscapeFromTarkov_Data/globalgamemanagers (SPT 4.1.6, Unity 2022.3.43f1).
    [InitializeOnLoad]
    public static class TarkovProjectSettings
    {
        public static readonly string[] Tags =
        {
            "piercesHeavy", "piercesAll", "piercesLight", "piercesMedium", "ObjectToSlide", "SpawnPoint", "AimPoint",
            "lootSpawnPoint", "CoverPoint", "patrolPoint", "DecalIgnore", "CullingTriggerExclude", "OpticCamera", "GPUI",
            "ForClientOnly", "DynamicCollider", "NotEmpty", "Vehicle", "SmokeGrenade", "PortalsCollection",
        };

        public static readonly string[] Layers =
        {
            "Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI", "", "", "Player", "DoorLowPolyCollider",
            "PlayerCollisionTest", "Terrain", "HighPolyCollider", "Triggers", "DisablerCullingObject", "Loot",
            "HitCollider", "PlayerRenderers", "LowPolyCollider", "Weapon Preview", "Shells", "CullingMask", "Interactive",
            "Deadbody", "RainDrops", "Menu Environment", "Foliage", "PlayerSpiritAura", "Sky", "LevelBorder",
            "TransparentCollider", "Grass",
        };

        // one row per layer: bit N set = this layer collides with layer N
        private static readonly uint[] CollisionMatrix =
        {
            0x400099C9, 0x000000C8, 0x000000C8, 0xFFFFFFFF, 0x000000C8, 0x000000C8, 0xFFFFFFFF, 0xFFFFFFFF,
            0x20040BC9, 0x001005C8, 0x00040AC8, 0x0094A5C9, 0x0090B0C9, 0x404118C8, 0x000000C8, 0x400018C9,
            0x000020C8, 0x000000C8, 0x00140DC8, 0x000000C8, 0x40A41AC8, 0x001000C8, 0x100020C8, 0x409018C8,
            0x000000C8, 0x000000C8, 0x000000C8, 0x000000C8, 0x004000C8, 0x000001C8, 0x0090A0C9, 0x000000C8,
        };

        private static readonly (string Name, float Cost)[] NavAreas =
        {
            ("Walkable", 1f), ("Not Walkable", 1f), ("Jump", 2f), ("Sitdown", 1f), ("Danger", 2f),
        };

        // bots path with agent type 0 ("Humanoid"); the others exist so ids line up with the game
        private static readonly (int Id, string Name, float Radius, float Height, float Slope, float Climb)[] NavAgents =
        {
            (0, "Humanoid", 0.3f, 1.7f, 48f, 0.38f),
            (-1372625422, "Big", 0.66f, 1.91f, 28.9f, 0.25f),
            (-334000983, "Small", 0.31f, 1.78f, 48f, 0.33f),
            (1479372276, "HumanoidDormitory", 0.3f, 1.78f, 43f, 0.25f),
            (-1923039037, "HumanoidWoodCutter", 0.329f, 1.78f, 48f, 0.25f),
        };

        static TarkovProjectSettings()
        {
            // defer: asset database queries are not safe during domain reload
            EditorApplication.delayCall += () => Apply(logIfUnchanged: false);
        }

        [MenuItem("Tarkov SDK/Quick Fixes/Apply Tarkov Tags, Layers and Physics", priority = 202)]
        public static void ApplyFromMenu() => Apply(logIfUnchanged: true);

        public static void Apply(bool logIfUnchanged)
        {
            var changes = new List<string>();
            ApplyTagsAndLayers(changes);
            ApplyCollisionMatrix(changes);
            ApplyNavMesh(changes);

            if (changes.Count > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log("[Tarkov SDK] Project settings synced with the game: " + string.Join(", ", changes));
            }
            else if (logIfUnchanged)
            {
                Debug.Log("[Tarkov SDK] Project settings already match the game.");
            }
        }

        private static void ApplyTagsAndLayers(List<string> changes)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
                return;
            var so = new SerializedObject(assets[0]);

            var tags = so.FindProperty("tags");
            bool tagsChanged = tags.arraySize != Tags.Length;
            for (int i = 0; !tagsChanged && i < Tags.Length; i++)
                tagsChanged = tags.GetArrayElementAtIndex(i).stringValue != Tags[i];
            if (tagsChanged)
            {
                tags.arraySize = Tags.Length;
                for (int i = 0; i < Tags.Length; i++)
                    tags.GetArrayElementAtIndex(i).stringValue = Tags[i];
                changes.Add("tags");
            }

            var layers = so.FindProperty("layers");
            bool layersChanged = false;
            for (int i = 0; i < Layers.Length && i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                if (element.stringValue == Layers[i])
                    continue;
                element.stringValue = Layers[i];
                layersChanged = true;
            }
            if (layersChanged)
                changes.Add("layers");

            if (tagsChanged || layersChanged)
                so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ApplyCollisionMatrix(List<string> changes)
        {
            bool changed = false;
            for (int a = 0; a < 32; a++)
            {
                for (int b = a; b < 32; b++)
                {
                    bool collide = (CollisionMatrix[a] & (1u << b)) != 0;
                    if (Physics.GetIgnoreLayerCollision(a, b) != !collide)
                    {
                        Physics.IgnoreLayerCollision(a, b, !collide);
                        changed = true;
                    }
                }
            }
            if (changed)
                changes.Add("collision matrix");
        }

        private static void ApplyNavMesh(List<string> changes)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset");
            if (assets == null || assets.Length == 0)
                return;
            var so = new SerializedObject(assets[0]);
            bool changed = false;

            var areas = so.FindProperty("areas");
            for (int i = 0; i < NavAreas.Length && i < areas.arraySize; i++)
            {
                var element = areas.GetArrayElementAtIndex(i);
                var name = element.FindPropertyRelative("name");
                var cost = element.FindPropertyRelative("cost");
                if (name.stringValue != NavAreas[i].Name || !Mathf.Approximately(cost.floatValue, NavAreas[i].Cost))
                {
                    name.stringValue = NavAreas[i].Name;
                    cost.floatValue = NavAreas[i].Cost;
                    changed = true;
                }
            }

            var settings = so.FindProperty("m_Settings");
            var names = so.FindProperty("m_SettingNames");
            if (settings.arraySize != NavAgents.Length || names.arraySize != NavAgents.Length)
            {
                settings.arraySize = NavAgents.Length;
                names.arraySize = NavAgents.Length;
                changed = true;
            }
            for (int i = 0; i < NavAgents.Length; i++)
            {
                var agent = NavAgents[i];
                var s = settings.GetArrayElementAtIndex(i);
                changed |= SetInt(s.FindPropertyRelative("agentTypeID"), agent.Id);
                changed |= SetFloat(s.FindPropertyRelative("agentRadius"), agent.Radius);
                changed |= SetFloat(s.FindPropertyRelative("agentHeight"), agent.Height);
                changed |= SetFloat(s.FindPropertyRelative("agentSlope"), agent.Slope);
                changed |= SetFloat(s.FindPropertyRelative("agentClimb"), agent.Climb);
                var name = names.GetArrayElementAtIndex(i);
                if (name.stringValue != agent.Name)
                {
                    name.stringValue = agent.Name;
                    changed = true;
                }
            }

            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                changes.Add("navmesh areas/agents");
            }
        }

        private static bool SetInt(SerializedProperty p, int value)
        {
            if (p == null || p.intValue == value)
                return false;
            p.intValue = value;
            return true;
        }

        private static bool SetFloat(SerializedProperty p, float value)
        {
            if (p == null || Mathf.Approximately(p.floatValue, value))
                return false;
            p.floatValue = value;
            return true;
        }
    }
}
