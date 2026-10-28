using UnityEngine;

namespace TarkovSDK
{
    // Overhead wires (power or phone lines, cables between buildings), as retail maps' WireGenerator makes them: a
    // plain mesh of sagging tubes between points, drawn by the game like any prop. The points are this object's
    // children, in order; each span hangs between two neighbours. The SDK rebuilds the mesh (on its parent, "Wires")
    // when these settings or the points change, and can stand a pole under every point.
    // Editor-only data on an EditorOnly object.
    [AddComponentMenu("Tarkov SDK/Wire")]
    public class TarkovWire : MonoBehaviour
    {
        [Tooltip("Wires side by side on each span.")]
        [Range(1, 8)]
        public int WireCount = 3;

        [Tooltip("Metres between neighbouring wires, across the span.")]
        [Min(0f)]
        public float Spacing = 0.5f;

        [Tooltip("How far a wire hangs below its ends at mid-span, per 10 m of span.")]
        [Min(0f)]
        public float SagPer10m = 0.35f;

        [Tooltip("Each wire's own random extra sag, as a fraction of its sag, so they don't hang in lockstep.")]
        [Range(0f, 0.5f)]
        public float SagVariation = 0.15f;

        [Tooltip("How much the wires swing in the wind (1: the SDK's default, longer spans swing more). " +
                 "Only with a material on p0/Bumped Specular Wires Wind, as the SDK's own wire material is.")]
        [Range(0f, 3f)]
        public float Sway = 1f;

        [Tooltip("Wire radius in metres (retail wires are about 0.02).")]
        [Min(0.002f)]
        public float Radius = 0.02f;

        [Tooltip("Sides of each wire's tube.")]
        [Range(3, 8)]
        public int Sides = 4;

        [Tooltip("Mesh segments per metre along a span.")]
        [Range(0.2f, 4f)]
        public float SegmentsPerMeter = 1f;

        [Tooltip("Close the line back from the last point to the first.")]
        public bool Loop;

        [Tooltip("Stand a wooden pole with a crossarm under every point.")]
        public bool Poles = true;

        [Tooltip("Pole radius in metres.")]
        [Min(0.05f)]
        public float PoleRadius = 0.14f;

        public Material WireMaterial;
        public Material PoleMaterial;
    }
}
