using UnityEngine;

namespace TarkovSDK
{
    // Makes a Unity object appear (or go) when a player fires a trigger id (CustomMapLoader's own): a copy of a prefab
    // or scene object at this object's children (or the object itself), or a scene object switched on or off where it
    // is. Build Map puts a prefab asset into the scene, switched off, so it ships in the map's bundle; the client plugin
    // clones or switches it. For things that look and sound (props, lights, particles, fire, smoke, audio, physics
    // props): game objects that need the raid's registration (doors, containers, switches, extracts, loot) don't work
    // in a copy. Editor-only data on an EditorOnly object; every player sees it (it runs on each machine in Fika co-op).
    [AddComponentMenu("Tarkov SDK/Spawn Prefab")]
    public class TarkovSpawnPrefab : MonoBehaviour
    {
        public enum SpawnAction
        {
            [Tooltip("A new copy of Prefab at each spawn point (children of this object, or this object).")]
            Spawn,
            [Tooltip("Switch the Prefab scene object on, where it is (it's saved switched off).")]
            Show,
            [Tooltip("Switch the Prefab scene object off.")]
            Hide,
        }

        [Tooltip("Do it when a player fires this trigger id (a zone or a switch; connect one in Triggers & Sounds).")]
        public string TriggerId = "";

        [Tooltip("Only once per raid.")]
        public bool Once = true;

        [Tooltip("The chance it happens each time the trigger fires.")]
        [Range(0f, 1f)]
        public float Chance = 1f;

        public SpawnAction Action = SpawnAction.Spawn;

        [Tooltip("A prefab asset or an object in the scene. Show and Hide need a scene object.")]
        public GameObject Prefab;

        [Tooltip("Seconds from the trigger.")]
        [Min(0f)]
        public float Delay;

        [Tooltip("Spawn: remove the copies after this many seconds (0: they stay).")]
        [Min(0f)]
        public float Lifetime;
    }
}
