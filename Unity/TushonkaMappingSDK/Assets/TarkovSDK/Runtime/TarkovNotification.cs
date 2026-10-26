using System;
using UnityEngine;

namespace TarkovSDK
{
    // A notification for the player who fires a trigger id (CustomMapLoader's own; Tarkov has no such trigger): the
    // game's own notification popup (NotificationManager.DisplayNotification with a CustomNotification) with its icon,
    // a UI sound and how long it stays. Editor-only data on an EditorOnly object: Build Map exports it to
    // sdk_export.json and the client plugin shows it. Gear strips carry the same Settings for their own notice.
    [AddComponentMenu("Tarkov SDK/Notification")]
    public class TarkovNotification : MonoBehaviour
    {
        // the game's ENotificationIconType, by name
        public enum Icon
        {
            Default, Alert, Friend, Mail, Note, Quest, Achievement, EntryPoint, RagFair, Hideout,
            WishlistQuest, WishlistHideout, WishlistTrading, WishlistEquipment, WishlistOther,
        }

        // the game's ENotificationDurationType
        public enum Duration
        {
            [Tooltip("A few seconds.")]
            Default,
            Long,
            [Tooltip("Until the player closes it.")]
            Infinite,
        }

        // the game's EUISoundType, by name, plus None
        public enum Sound
        {
            None,
            BackpackClose, BackpackOpen, ButtonBottomBarClick, ButtonClick, ButtonOver, ChatSelect, ErrorMessage, InsuranceInsured,
            InsuranceItemOnInsure, MenuCheckBox, MenuContextMenu, MenuDropdown, MenuDropdownSelect, MenuEscape, MenuInspectorWindowClose,
            MenuInspectorWindowOpen, MenuInstallMag, MenuInstallModFunc, MenuInstallModGear, MenuInstallModVital, MenuMapTag,
            MenuModdingClose, MenuModdingOpen, MenuOpenContainer, MenuStock, MenuTraderPress, QuestCompleted, QuestFailed, QuestFinished,
            QuestStarted, QuestSubTrackComplete, RepairComplete, TradeOperationComplete, MenuWeaponAssemble, MenuWeaponDisassemble,
            TacticalClothingBuy, TacticalClothingApply, GeneratorTurnOn, GeneratorTurnOff, PlayerIsDead, MalfunctionExamined,
            AchievementCompleted, DialogButtonClick, DialogButtonHover,
        }

        [Serializable]
        public class Settings
        {
            [Tooltip("The text shown.")]
            [TextArea(1, 4)]
            public string Message = "";
            [Tooltip("The icon on the left of the popup.")]
            public Icon Icon = Icon.Alert;
            [Tooltip("How long it stays up.")]
            public Duration Duration = Duration.Default;
            [Tooltip("A sound from the game's interface, played when it appears.")]
            public Sound Sound = Sound.QuestStarted;
            [Tooltip("Colour the text.")]
            public bool UseTextColor;
            public Color TextColor = Color.white;
        }

        [Tooltip("Show it to the player who fires this trigger id (a zone or a switch; connect one in Triggers & Sounds).")]
        public string TriggerId = "";

        [Tooltip("Only show it once per raid to each player.")]
        public bool Once = true;

        public Settings Notification = new Settings { Message = "Something happened." };
    }
}
