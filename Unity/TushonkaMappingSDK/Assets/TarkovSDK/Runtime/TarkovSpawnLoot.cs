using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // Spawns loot when a player fires a trigger id (CustomMapLoader's own; the map's loose loot is rolled once at raid
    // start). The items appear at this object (At This Object) or at its children, loose on the ground as if dropped;
    // weapons come built from their default preset. Editor-only data on an EditorOnly object: Build Map exports it to
    // sdk_export.json and the client plugin spawns them (on the host in Fika co-op, where the other players don't
    // see them: the game has no way to send a new loose item to them).
    [AddComponentMenu("Tarkov SDK/Spawn Loot")]
    public class TarkovSpawnLoot : MonoBehaviour
    {
        public enum PickMode
        {
            [Tooltip("Every item in the list, each at the next spawn point.")]
            All,
            [Tooltip("One item from the list, picked by weight, at each spawn point.")]
            OnePerPoint,
        }

        [Tooltip("Spawn it when a player fires this trigger id (a zone or a switch; connect one in Triggers & Sounds).")]
        public string TriggerId = "";

        [Tooltip("Only once per raid.")]
        public bool Once = true;

        [Tooltip("The chance it happens each time the trigger fires.")]
        [Range(0f, 1f)]
        public float Chance = 1f;

        [Tooltip("Spawn the items at this object itself (raise it a little off the ground). Off: at its children, one item per child in turn.")]
        public bool AtThisObject = true;

        public PickMode Pick = PickMode.All;

        [Tooltip("Let the items fall onto the surface below (off: they hang where they're put).")]
        public bool UseGravity = true;

        [Tooltip("Each item at a random rotation.")]
        public bool RandomRotation = true;

        [Tooltip("Seconds from the trigger to the spawn.")]
        [Min(0f)]
        public float Delay;

        public List<TarkovLootSpawn.Item> Items = new List<TarkovLootSpawn.Item>();
    }
}
