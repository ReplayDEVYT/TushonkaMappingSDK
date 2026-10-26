using EFT.Interactive;
using UnityEngine;

namespace TarkovSDK
{
    // Gear stripping (CustomMapLoader's own feature; Tarkov has none): takes the player's gear at raid start
    // and/or when a trigger id fires. The gear is lost, or moved into a lootable container on the map, where the
    // player can take it back. What doesn't fit stays on the player and shows an error. Editor-only data on an
    // EditorOnly object: Build Map exports it to sdk_export.json, and the client plugin strips the gear.
    [AddComponentMenu("Tarkov SDK/Gear Strip")]
    public class TarkovGearStrip : MonoBehaviour
    {
        public enum StripMode
        {
            [Tooltip("The gear is gone for good.")]
            Lose,
            [Tooltip("The gear is moved into the container.")]
            MoveToContainer,
        }

        [Tooltip("Strip the player when the raid starts.")]
        public bool OnRaidStart;

        [Tooltip("Strip the player who fires this trigger id (a zone or a switch; connect one in Triggers & Sounds). Empty: no trigger.")]
        public string TriggerId = "";

        [Tooltip("Only strip each player once per raid.")]
        public bool Once = true;

        public StripMode Mode = StripMode.MoveToContainer;

        [Tooltip("Where the gear goes (Move To Container). It always spawns. Give it a big grid: a custom container type (Tarkov SDK > Tools > Interactables > Loot Containers) of 10 x 16 holds a full kit.")]
        public LootableContainer Container;

        [Header("What is taken")]
        public bool Headwear = true;
        public bool Earpiece = true;
        public bool FaceCover = true;
        public bool Eyewear = true;
        public bool ArmBand = true;
        public bool ArmorVest = true;
        public bool TacticalVest = true;
        public bool Backpack = true;
        public bool FirstPrimaryWeapon = true;
        public bool SecondPrimaryWeapon = true;
        public bool Holster = true;
        public bool Scabbard = true;
        [Tooltip("What's in the pockets (the pockets themselves can't be taken).")]
        public bool PocketContents = true;
        [Tooltip("What's in the secure container (the container itself can't be taken).")]
        public bool SecuredContainerContents;
        [Tooltip("What's in the special slots (the three slots under the pockets).")]
        public bool SpecialSlots;

        [Header("Notification")]
        [Tooltip("Tell the player when gear is taken (the game's notification popup).")]
        public bool Notify = true;
        public TarkovNotification.Settings Notification = new TarkovNotification.Settings
        {
            Message = "Your gear has been taken.", Icon = TarkovNotification.Icon.Alert,
            Duration = TarkovNotification.Duration.Long, Sound = TarkovNotification.Sound.QuestFailed,
        };
    }
}
