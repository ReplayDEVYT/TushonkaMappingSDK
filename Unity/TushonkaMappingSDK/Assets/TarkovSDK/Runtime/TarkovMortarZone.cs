using UnityEngine;

namespace TarkovSDK
{
    // One shelling zone (the game's ArtilleryShellingZone): a grid of Points.x by Points.y target points GridStep metres
    // apart, centred on this object and turned with its Y rotation. A shelling picks Points Per Shelling of them at
    // random, then fires Rounds rounds of Shots Per Round shells, each landing within Point Radius of a picked point
    // (dropped onto the ground from 300 m up). The shells come from the nearest battery. Editor-only data under the
    // map's TarkovMortarSettings; see it for planned and called shelling.
    [AddComponentMenu("Tarkov SDK/Mortar Zone")]
    public class TarkovMortarZone : MonoBehaviour
    {
        [Tooltip("The zone's id (the game's zone ID); every zone on the map needs its own. Build Map fills it in.")]
        public int Id;

        [Tooltip("Planned shelling can pick it.")]
        public bool Planned = true;

        [Tooltip("Off: planned shelling never picks it (a trigger still can).")]
        public bool Active = true;

        [Tooltip("Called shelling (the blue signal flare) uses this zone's size and timing, centred on the player who fired it. " +
                 "The game takes the first such zone; its position doesn't matter.")]
        public bool CalledStrikeTemplate;

        [Header("Grid")]
        [Tooltip("Target points across (x) and along (y). Retail 10 x 10 to 20 x 10.")]
        public Vector2Int Points = new Vector2Int(10, 10);

        [Tooltip("Metres between target points. Retail 13 x 13.")]
        public Vector2 GridStep = new Vector2(13f, 13f);

        [Tooltip("Shells land within this many metres of their target point. Retail 15.")]
        [Min(0f)]
        public float PointRadius = 15f;

        [Header("Fire")]
        [Tooltip("How many target points a shelling picks: between these two. Retail 30-36.")]
        public Vector2Int PointsPerShelling = new Vector2Int(30, 36);

        [Tooltip("Rounds of fire per shelling. Retail 6.")]
        [Min(1)]
        public int Rounds = 6;

        [Tooltip("Shells per round. Retail 7-12.")]
        [Min(1)]
        public int ShotsPerRound = 7;

        [Tooltip("Seconds between rounds: between these two. Retail 6-10.")]
        public Vector2 PauseBetweenRounds = new Vector2(6f, 10f);

        [Tooltip("Seconds between shells: between these two. Retail 0.4-2.")]
        public Vector2 PauseBetweenShots = new Vector2(0.4f, 2f);

        [Tooltip("The fuse: metres above the ground the shell bursts, between these two. Retail 1-1.4.")]
        public Vector2 BurstHeight = new Vector2(1f, 1.4f);

        [Header("Trigger (CustomMapLoader)")]
        [Tooltip("Shell this zone when a player fires this trigger id (a zone or a switch; connect one in Triggers & Sounds). Empty: planned or called shelling only.")]
        public string TriggerId = "";

        [Tooltip("Seconds from the trigger to the first shell (the warning plays the last Warning Time seconds of it).")]
        [Min(0f)]
        public float TriggerDelay = 15f;

        [Tooltip("Only once per raid.")]
        public bool Once = true;
    }
}
