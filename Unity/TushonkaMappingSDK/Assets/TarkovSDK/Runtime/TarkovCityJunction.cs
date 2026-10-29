using UnityEngine;

namespace TarkovSDK
{
    // A road junction for hand-drawn roads (City Builder > Roads): a square of road with a sidewalk corner on each
    // side and up to four arms, north (+Z), east (+X), south (-Z) and west (-X) of its object. An open arm is where a
    // road joins (with a zebra crossing); a closed one is sidewalk. Four arms make a crossroads, three a T junction,
    // two at a right angle a corner. Built on its parent ("Junction"), flat at its height.
    // Editor-only data on an EditorOnly object.
    [AddComponentMenu("Tarkov SDK/City Junction")]
    public class TarkovCityJunction : MonoBehaviour
    {
        [Tooltip("Its materials. Empty: the SDK's plain materials.")]
        public TarkovCityKit Kit;

        [Tooltip("Road width in metres; the roads joined to it take it.")]
        [Min(2f)]
        public float Width = 8f;

        [Tooltip("Sidewalk width in metres; the roads joined to it take it.")]
        [Min(0.5f)]
        public float SidewalkWidth = 3f;

        [Range(0f, 0.3f)]
        public float CurbHeight = 0.15f;

        [Tooltip("The kerbstones' width in metres: a strip with a bevelled road edge, its top 2 cm above the sidewalk.")]
        [Range(0.05f, 0.5f)]
        public float CurbWidth = 0.2f;

        [Tooltip("Arms: north (+Z), east (+X), south (-Z), west (-X).")]
        public bool North = true, East = true, South = true, West = true;

        [Tooltip("Zebra crossings on the open arms.")]
        public bool Crossings = true;

        [HideInInspector] public string Id;
        [HideInInspector] public int BuiltHash;

        public bool Open(int arm) => arm == 0 ? North : arm == 1 ? East : arm == 2 ? South : West;

        public void SetOpen(int arm, bool open)
        {
            if (arm == 0) North = open;
            else if (arm == 1) East = open;
            else if (arm == 2) South = open;
            else West = open;
        }
    }
}
