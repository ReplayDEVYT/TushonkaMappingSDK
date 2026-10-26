using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // A loot container type: a new item with its own grid (CustomMapLoader's server mod makes it from a retail
    // container), or a retail container type whose loot this map changes. A LootableContainer uses it by putting
    // its Template in the container's Template. Build Map exports the types the map's containers use to
    // sdk_export.json, and the server mod registers them and their loot.
    [CreateAssetMenu(fileName = "LootContainer", menuName = "Tarkov SDK/Loot Container Type", order = 100)]
    public class TarkovContainerDefinition : ScriptableObject
    {
        [Tooltip("On: a new item (Template is made for it). Off: Template is a retail container type, and only its loot on this map changes.")]
        public bool NewContainer = true;

        [Tooltip("The item template id (24 hex characters) the scene's LootableContainers put in their Template.")]
        public string Template = "";

        [Tooltip("Shown when the container is searched and in the item's name.")]
        public string DisplayName = "Container";
        public string ShortName = "Container";
        [TextArea]
        public string Description = "";

        [Tooltip("Grid width in cells.")]
        [Range(1, 20)]
        public int Width = 4;
        [Tooltip("Grid height in cells.")]
        [Range(1, 40)]
        public int Height = 4;

        [Tooltip("The retail container the new item is made from (its sounds, search time and look in menus). Wooden crate by default.")]
        public string BaseTemplate = "578f87ad245977356274f2cc";

        public enum LootSource
        {
            [Tooltip("Spawns nothing (a stash for gear stripping, or a container filled by hand).")]
            Empty,
            [Tooltip("The loot of a retail container type, from every retail map.")]
            CopyRetail,
            [Tooltip("The items listed below.")]
            Custom,
            [Tooltip("A retail container type's loot plus the items listed below.")]
            RetailPlusCustom,
        }

        [Header("Loot")]
        public LootSource Loot = LootSource.Custom;

        [Tooltip("The retail container type whose loot is used (Copy Retail, Retail Plus Custom).")]
        public string RetailLootTemplate = "578f87ad245977356274f2cc";

        [Tooltip("Fewest and most items rolled per container (Custom).")]
        [Min(0)]
        public int MinItems = 1;
        [Min(0)]
        public int MaxItems = 4;

        [Tooltip("Items that can spawn; picked by weight.")]
        public List<Item> Items = new List<Item>();

        [Serializable]
        public class Item
        {
            [Tooltip("Item template id (24 hex characters).")]
            public string Template = "";
            [Tooltip("Shown in the editor only.")]
            public string Name = "";
            [Min(1)]
            public int Weight = 10;
        }
    }
}
