using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Quest zones, as retail maps place them (Shoreline_DesignMain): trigger boxes the game moves to the
    // Triggers layer itself, found by their Id from quest conditions.
    //
    //  - Visit Zone: an ExperienceTrigger. Entering it counts for "visit" conditions (SpecialPlaceVisited)
    //    and, while inside, for "in zone" conditions (the player's trigger zones); Experience, if above 0,
    //    is the exploration XP the first visit gives.
    //  - Place Item Zone: a PlaceItemTrigger, for "leave an item" and "place a marker" conditions; Beacon
    //    Dummy is the object shown where it's placed.
    public static class TarkovQuestZones
    {
        private const string Menu = "GameObject/Tarkov SDK/Quests/";

        [MenuItem(Menu + "Visit Zone", priority = 75)]
        private static void MenuVisit(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateVisitZone(p).gameObject);

        [MenuItem(Menu + "Place Item Zone", priority = 76)]
        private static void MenuPlace(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreatePlaceItemZone(p).gameObject);

        public static ExperienceTrigger CreateVisitZone(Transform parent)
        {
            var go = TarkovFacilitySystems.Zone(parent, "VisitZone", new Vector3(0f, 2f, 0f), new Vector3(6f, 4f, 6f));
            var zone = go.AddComponent<ExperienceTrigger>();
            zone._id = NewId(go, "visit");
            zone._experience = 0;
            return zone;
        }

        public static PlaceItemTrigger CreatePlaceItemZone(Transform parent)
        {
            var go = TarkovFacilitySystems.Zone(parent, "PlaceItemZone", new Vector3(0f, 1f, 0f), new Vector3(1.5f, 2f, 1.5f));
            var zone = go.AddComponent<PlaceItemTrigger>();
            zone._id = NewId(go, "place");
            return zone;
        }

        private static string NewId(GameObject go, string what) =>
            $"{(string.IsNullOrEmpty(go.scene.name) ? "map" : go.scene.name)}_{what}_{TarkovFacilitySystems.ShortId()}";
    }

    [CustomEditor(typeof(TriggerWithId), true)]
    public class QuestZoneEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var zone = (TriggerWithId)target;
            EditorGUILayout.Space();
            string what = zone is PlaceItemTrigger ? "\"leave an item\" or \"place a marker\" conditions"
                : zone is ExperienceTrigger ? "\"visit\" and \"be in zone\" conditions" : "\"be in zone\" conditions";
            EditorGUILayout.HelpBox($"Quests find this zone by its Id, for {what}. Use the same Id in your quest.", MessageType.None);
            if (string.IsNullOrWhiteSpace(zone._id))
                EditorGUILayout.HelpBox("No Id: no quest can use this zone.", MessageType.Warning);
            else if (Object.FindObjectsOfType<TriggerWithId>(true).Any(z => z != zone && z.GetType() == zone.GetType() && z._id == zone._id))
                EditorGUILayout.HelpBox("Another zone of this kind has the same Id.", MessageType.Warning);
            if (!(zone.GetComponent<Collider>()?.isTrigger ?? false))
                EditorGUILayout.HelpBox("It needs a trigger collider (a box with Is Trigger) on the same object.", MessageType.Error);
            if (GUILayout.Button("Copy Id"))
                EditorGUIUtility.systemCopyBuffer = zone._id;
        }
    }
}
