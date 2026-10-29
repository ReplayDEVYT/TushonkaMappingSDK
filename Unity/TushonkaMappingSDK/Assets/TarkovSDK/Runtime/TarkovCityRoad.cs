using UnityEngine;

namespace TarkovSDK
{
    // A road drawn by hand with the City Builder's Roads tab (Tarkov SDK > Tools > Geometry > City Builder...): a smooth
    // curve through this object's children (the points, in order), built into a road with sidewalks, kerbs and lane
    // lines on its parent ("Road"). Its ends can join a junction (TarkovCityJunction). Line This Road fills its sides
    // with the kit's buildings, lamps, trees, props and cars, by the Roadside settings.
    // Editor-only data on an EditorOnly object.
    [AddComponentMenu("Tarkov SDK/City Road")]
    public class TarkovCityRoad : MonoBehaviour
    {
        [Tooltip("Its materials (roads, sidewalks, kerbs, markings) and, for Line This Road, its prefabs. Empty: the SDK's plain materials.")]
        public TarkovCityKit Kit;

        [Tooltip("Road width in metres, kerb to kerb (two lanes are about 7 m). A road joined to a junction takes the junction's.")]
        [Min(2f)]
        public float Width = 8f;

        [Tooltip("The left sidewalk's width in metres, looking from the first point to the last (0: none).")]
        [Min(0f)]
        public float SidewalkLeft = 3f;

        [Tooltip("The right sidewalk's width in metres (0: none).")]
        [Min(0f)]
        public float SidewalkRight = 3f;

        [Tooltip("Kerb height in metres. The player steps up to 0.2 m without vaulting.")]
        [Range(0f, 0.3f)]
        public float CurbHeight = 0.15f;

        [Tooltip("The kerbstones' width in metres: a strip with a bevelled road edge, its top 2 cm above the sidewalk.")]
        [Range(0.05f, 0.5f)]
        public float CurbWidth = 0.2f;

        [Tooltip("A dashed centre line.")]
        public bool LaneLines = true;

        [Tooltip("A double solid centre line instead of a dashed one.")]
        public bool Avenue;

        [Tooltip("The road lies on the ground under it (terrain, the generated city), smoothed; off: it follows its points' heights.")]
        public bool FollowGround = true;

        [Tooltip("Metres the road sits above the ground it follows.")]
        public float GroundOffset = 0.03f;

        [Tooltip("Close the road from the last point back to the first.")]
        public bool Loop;

        [Tooltip("Metres between the road's cross-sections (smaller: smoother bends, more triangles).")]
        [Range(0.5f, 8f)]
        public float Step = 1.5f;

        [Header("Joins (set by the Roads tab)")]
        public TarkovCityJunction StartJunction;
        [Range(0, 3)]
        public int StartArm;
        public TarkovCityJunction EndJunction;
        [Range(0, 3)]
        public int EndArm;

        [Header("Roadside (Line This Road)")]
        [Tooltip("Another number fills the road another way.")]
        public int Seed = 1;
        public bool FillLeft = true;
        public bool FillRight = true;

        [Tooltip("The chance each stretch of road gets a building (the rest stays empty).")]
        [Range(0f, 1f)]
        public float BuildingChance = 0.9f;

        [Tooltip("Metres between neighbouring buildings.")]
        [Min(0f)]
        public float BuildingGap = 0.5f;

        [Tooltip("Metres between street lamps (0: none).")]
        [Min(0f)]
        public float LampSpacing = 24f;

        [Tooltip("Metres between street trees (0: none).")]
        [Min(0f)]
        public float TreeSpacing = 0f;

        [Tooltip("Street props per 100 m of sidewalk.")]
        [Min(0f)]
        public float PropsPer100m = 4f;

        [Tooltip("The chance each parking space along the kerbs has a car in it.")]
        [Range(0f, 1f)]
        public float ParkedCarChance = 0.3f;

        // names the road's mesh assets; the settings it was last built with
        [HideInInspector] public string Id;
        [HideInInspector] public int BuiltHash;
    }
}
