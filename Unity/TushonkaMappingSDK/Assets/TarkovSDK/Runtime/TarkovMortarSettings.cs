using UnityEngine;

namespace TarkovSDK
{
    // The map's mortar shelling (the game's artillery: ArtilleryShellingControllerServer, as on Woods, Customs,
    // Shoreline, Reserve), what globals ArtilleryShelling.ArtilleryMapsConfigs[map] holds. Planned shelling strikes
    // random planned zones a few times a raid; called shelling hits wherever a player fires the blue signal flare
    // (the game's mortar call, 66d97834d2985e11480d5c1e), using the zone marked Called Strike Template. Zones
    // (TarkovMortarZone) and batteries (TarkovMortarBattery) sit under this object. Editor-only data on an EditorOnly object: Build Map exports it to sdk_export.json
    // "Mortar" and CustomMapLoader registers it in globals.
    [AddComponentMenu("Tarkov SDK/Mortar Settings")]
    public class TarkovMortarSettings : MonoBehaviour
    {
        [Tooltip("Shell random planned zones by itself during the raid (retail maps have it off and keep the zones for events).")]
        public bool PlannedShelling = true;

        [Tooltip("Seconds into the raid before the first planned shelling (retail 278-373).")]
        [Min(0f)]
        public float FirstShellingAfter = 300f;

        [Tooltip("How many planned shellings a raid has.")]
        [Min(1)]
        public int Shellings = 3;

        [Tooltip("Seconds between planned shellings: between these two.")]
        public Vector2 PauseBetweenShellings = new Vector2(180f, 300f);

        [Tooltip("How many planned zones each shelling hits.")]
        [Min(1)]
        public int ZonesPerShelling = 1;

        [Tooltip("Pick new zones for each shelling (off: the first shelling's zones every time).")]
        public bool NewZonesEachShelling = true;

        [Tooltip("Seconds of warning (the siren and the radio detector) before the first shell.")]
        [Min(0f)]
        public float WarningTime = 10f;

        [Tooltip("Seconds between a player firing the blue signal flare and the first shell.")]
        [Min(0f)]
        public float CalledStrikeDelay = 20f;
    }
}
