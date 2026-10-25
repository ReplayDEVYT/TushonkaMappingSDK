using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // The Server Data Editor's Raid and Airdrops tabs: base.json values the game reads outside the map select
    // basics, with retail's as the reference (Customs / Woods / Factory in brackets).
    public partial class TarkovServerDataWindow
    {
        private static readonly string[] RuleNames = { "Normal", "AvoidOwnPmc", "AvoidAllPmc" };
        private static readonly string[] RuleLabels = { "Normal", "Warn: no own-side PMCs (Factory)", "Warn: no PMCs at all (Customs)" };

        private void DrawRaid()
        {
            EditorGUILayout.LabelField("Location panel", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("What the map select screen shows for the map (LocationInfoPanel).", EditorStyles.wordWrappedMiniLabel);
            FloatField("Area (km²)", "Area", "Shown as the map's size. Retail leaves it 0 on most maps; Factory 0.9.");
            IntField("Average Raid Time (min)", "AveragePlayTime", "Shown as the play time. Customs 25, Woods 25, Factory 15, Streets 50.");
            using (new EditorGUILayout.HorizontalScope())
            {
                IntField("Players Min", "MinPlayers", "Shown as the player count, Min-Max. Customs 10-12, Factory 7-8.");
                IntField("Max", "MaxPlayers");
            }
            IntField("Average Player Level", "AveragePlayerLevel", "Compared with the player's level for the difficulty hint. Customs 20, Woods 15, Factory 1, Labs 45.");
            var rules = _base.Value<string>("Rules") ?? "Normal";
            int rule = Math.Max(0, Array.IndexOf(RuleNames, rules));
            int newRule = EditorGUILayout.Popup(new GUIContent("Warning", "Rules: the warning the panel shows. Customs AvoidAllPmc, Factory AvoidOwnPmc, other maps Normal."),
                                                rule, RuleLabels);
            if (newRule != rule)
                _base["Rules"] = RuleNames[newRule];

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Groups", EditorStyles.boldLabel);
            BoolField("Co-op", "EnableCoop", "EnableCoop: groups can enter together. Every retail map has it on.");
            IntField("Max Group Count", "MaxCoopGroup", "MaxCoopGroup: how many groups the matchmaker allows. Customs 12, Woods 14, Factory 6.");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Spawning", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                IntField("Spawn Distance Min (m)", "MinDistToFreePoint", "MinDistToFreePoint / MaxDistToFreePoint: how far from other players a player spawn is picked. Customs 60-200, Factory 10-900.");
                IntField("Max", "MaxDistToFreePoint");
            }
            IntField("Min Distance To Exit (m)", "MinDistToExitPoint", "MinDistToExitPoint. Retail 30 (Streets, Ground Zero 10).");
            TextField("Disabled Scav Exits", "DisabledScavExits", "DisabledScavExits: names of scav extracts that are off, separated by commas (Factory: Cellars,Gate 0).");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Loot amount", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("SPT's loot generator scales the map's loose loot by these.", EditorStyles.wordWrappedMiniLabel);
            FloatField("Loose Loot", "GlobalLootChanceModifier", "GlobalLootChanceModifier. Customs 0.4, Woods 0.4, Factory 0.27, Lighthouse 0.12.");
            FloatField("Loose Loot (PvE)", "GlobalLootChanceModifierPvE", "GlobalLootChanceModifierPvE: used in SPT's PvE raids. Customs 0.9, Woods 0.95, Factory 0.3.");
            FloatField("Containers", "GlobalContainerChanceModifier", "GlobalContainerChanceModifier. Every retail map uses 1.");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Other", EditorStyles.boldLabel);
            BoolField("Occlusion Culling", "OcculsionCullingEnabled", "OcculsionCullingEnabled: use the scene's baked occlusion culling. Leave off unless you baked occlusion data in Unity.");
        }

        private void DrawAirdropsTab()
        {
            var points = UnityEngine.Object.FindObjectsOfType<EFT.Airdrop.AirdropPoint>(true).Length;
            EditorGUILayout.LabelField("Airdrop points", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"{points} in the open scene. A red flare calls a crate to the nearest one; plane drops pick one at random.",
                                       EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Generate Airdrop Points", "Every 20 m on open, flat ground in the active scene (Generation > Misc > Airdrop Points).")))
                    EditorApplication.delayCall += () => TarkovAirdrops.Generate(SceneManager.GetActiveScene(), 20f);
                if (GUILayout.Button("Select Them") && points > 0)
                    Selection.objects = UnityEngine.Object.FindObjectsOfType<EFT.Airdrop.AirdropPoint>(true).Select(p => (UnityEngine.Object)p.gameObject).ToArray();
            }
            DrawAirdrops();
            var list = EnsureArray("AirdropParameters");
            if (list.Count > 0 && list[0] is JObject p)
            {
                EditorGUILayout.Space();
                int distance = p.Value<int?>("AirdropPointDeactivateDistance") ?? 0;
                int next = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Spacing Between Drops (m)",
                    "AirdropPointDeactivateDistance: after a drop, points this close to it aren't used again until every point has been used. Customs 50."), distance));
                if (next != distance)
                    p["AirdropPointDeactivateDistance"] = next;
            }
            if (list.Count > 1)
                EditorGUILayout.HelpBox($"base.json has {list.Count} airdrop phases; these settings are the first one's. The game moves to the next phase once a phase's " +
                                        "No Drops After time has passed. Edit the others in the JSON tab.", MessageType.None);
        }

        private void FloatField(string label, string key, string tooltip = null)
        {
            float current = (float)(_base.Value<double?>(key) ?? 0);
            float value = EditorGUILayout.FloatField(new GUIContent(label, tooltip), current);
            if (!Mathf.Approximately(value, current))
                _base[key] = Math.Round(value, 4);
        }

        private void TextField(string label, string key, string tooltip = null)
        {
            var current = _base.Value<string>(key) ?? "";
            var value = EditorGUILayout.TextField(new GUIContent(label, tooltip), current);
            if (value != current)
                _base[key] = value;
        }
    }
}
