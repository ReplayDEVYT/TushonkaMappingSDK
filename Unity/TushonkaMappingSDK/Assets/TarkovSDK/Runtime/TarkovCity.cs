using UnityEngine;

namespace TarkovSDK
{
    // A city's layout, for the City Builder (Tarkov SDK > Tools > Geometry > City Builder...): a grid of blocks with
    // roads between them, some blocks merged or left as parks, all drawn from the seed. Generate builds it under this
    // object's parent ("City"): the ground (roads, sidewalks, kerbs, block and park ground, markings) as static meshes
    // with colliders, and the kit's buildings, lamps, props, cars and trees as prefab instances. The city is flat, on
    // the City object's plane, centred on it.
    // Editor-only data on an EditorOnly object.
    [AddComponentMenu("Tarkov SDK/City")]
    public class TarkovCity : MonoBehaviour
    {
        public TarkovCityKit Kit;

        [Tooltip("Another number lays the city out (and fills it) another way with the same settings.")]
        public int Seed = 1;

        [Header("Layout")]
        [Tooltip("The city's size in metres, x by z. Retail Streets of Tarkov is about 540 x 680 m where players spawn.")]
        public Vector2 Size = new Vector2(240f, 240f);

        [Tooltip("The smallest block, in metres along a street.")]
        [Min(10f)]
        public float BlockMin = 40f;

        [Tooltip("The largest block, in metres along a street.")]
        [Min(10f)]
        public float BlockMax = 70f;

        [Tooltip("Road width in metres, kerb to kerb (two lanes are about 7 m).")]
        [Min(3f)]
        public float RoadWidth = 8f;

        [Tooltip("Every this many roads is an avenue (0: none).")]
        [Min(0)]
        public int AvenueEvery = 3;

        [Tooltip("Avenue width in metres, kerb to kerb.")]
        [Min(3f)]
        public float AvenueWidth = 14f;

        [Tooltip("Roads round the city's edge too; without them the outer blocks reach the edge.")]
        public bool EdgeRoads = true;

        [Tooltip("The chance a block is joined with its neighbour over the road between them (bigger blocks, T junctions).")]
        [Range(0f, 1f)]
        public float MergeChance = 0.15f;

        [Tooltip("The chance a block is a park (trees and park props, no buildings).")]
        [Range(0f, 1f)]
        public float ParkChance = 0.1f;

        [Header("Streets")]
        [Tooltip("Sidewalk width in metres.")]
        [Min(0f)]
        public float SidewalkWidth = 3f;

        [Tooltip("Kerb height in metres. The player steps up to 0.2 m without vaulting.")]
        [Range(0f, 0.3f)]
        public float CurbHeight = 0.15f;

        [Tooltip("The kerbstones' width in metres: a strip with a bevelled road edge, its top 2 cm above the sidewalk.")]
        [Range(0.05f, 0.5f)]
        public float CurbWidth = 0.2f;

        [Tooltip("Centre lines on the roads.")]
        public bool LaneLines = true;

        [Tooltip("Pedestrian crossings at the junctions.")]
        public bool Crossings = true;

        [Header("Buildings")]
        [Tooltip("The chance each stretch of street gets a building (the rest stays an empty lot).")]
        [Range(0f, 1f)]
        public float BuildingChance = 0.9f;

        [Tooltip("Metres between neighbouring buildings.")]
        [Min(0f)]
        public float BuildingGap = 0.5f;

        [Tooltip("Placeholder buildings' storeys (3 m each), when the kit has no buildings.")]
        public Vector2Int PlaceholderFloors = new Vector2Int(2, 6);

        [Header("Street furniture")]
        [Tooltip("Metres between street lamps along a block (0: none).")]
        [Min(0f)]
        public float LampSpacing = 24f;

        [Tooltip("Metres between street trees along a block (0: none).")]
        [Min(0f)]
        public float TreeSpacing = 0f;

        [Tooltip("Street props per 100 m of sidewalk.")]
        [Min(0f)]
        public float PropsPer100m = 4f;

        [Tooltip("The chance each parking space along a kerb has a car in it.")]
        [Range(0f, 1f)]
        public float ParkedCarChance = 0.3f;

        [Tooltip("Metres between trees in parks.")]
        [Min(2f)]
        public float ParkTreeSpacing = 8f;

        [Tooltip("Park props per 1000 m² of park.")]
        [Min(0f)]
        public float ParkPropsPer1000m2 = 2f;

        // names this city's mesh assets
        [HideInInspector]
        public string Id;
    }
}
