using UnityEngine;

namespace TarkovSDK
{
    // A point of a hand-drawn road (TarkovCityRoad): how the road's curve passes through it. Edited with the handles in
    // the Scene view and the City Builder's curve editor (Roads tab).
    // Editor-only data on an EditorOnly object.
    [AddComponentMenu("Tarkov SDK/City Road Point")]
    public class TarkovCityRoadPoint : MonoBehaviour
    {
        public enum Modes
        {
            [Tooltip("The curve passes smoothly through, its handles worked out from the neighbouring points.")]
            Smooth,
            [Tooltip("Your handles, kept in line (a smooth bend whose sharpness on each side you set).")]
            Aligned,
            [Tooltip("Your handles, each its own way (a kink).")]
            Free,
            [Tooltip("Straight to the neighbouring points: a sharp corner.")]
            Corner,
        }

        public Modes Mode;

        [Tooltip("The handle towards the previous point, from this one (in the road's space).")]
        public Vector3 In = new Vector3(0f, 0f, -5f);

        [Tooltip("The handle towards the next point, from this one (in the road's space).")]
        public Vector3 Out = new Vector3(0f, 0f, 5f);

        [Tooltip("Metres the road is raised here (lowered below zero), above where it would lie: the ground with Follow Ground, else this point. " +
                 "Blends smoothly to the neighbouring points' values.")]
        public float Raise;
    }
}
