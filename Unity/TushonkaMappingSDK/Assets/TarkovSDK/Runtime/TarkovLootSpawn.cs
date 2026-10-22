using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // A loose loot spawn: one of its items may appear here each raid. Editor-only data: the object is
    // tagged EditorOnly and never ships in the map bundle. Build Map exports it to sdk_export.json,
    // and CustomMapLoader turns it into the map's loose loot, which SPT's loot generator spawns.
    [AddComponentMenu("Tarkov SDK/Loot Spawn")]
    public class TarkovLootSpawn : MonoBehaviour
    {
        [Tooltip("Chance (0-1) that this spot is picked for loot in a raid.")]
        [Range(0f, 1f)]
        public float Probability = 0.3f;

        [Tooltip("Always spawn one of the items here, ignoring Probability.")]
        public bool AlwaysSpawn;

        [Tooltip("Let the item fall onto the surface below.")]
        public bool UseGravity = true;

        [Tooltip("Spawn the item at a random rotation.")]
        public bool RandomRotation = true;

        [Tooltip("Items that can spawn here; one is picked by weight.")]
        public List<Item> Items = new List<Item>();

        [Serializable]
        public class Item
        {
            [Tooltip("Item template id (24 hex characters).")]
            public string Template = "";
            [Tooltip("Shown in the editor only.")]
            public string Name = "";
            [Min(1)]
            public int Weight = 1;
            [Min(1)]
            public int StackCount = 1;
        }
    }
}
