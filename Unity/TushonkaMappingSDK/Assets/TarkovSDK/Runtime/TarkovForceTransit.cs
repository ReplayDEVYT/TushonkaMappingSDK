using EFT.Interactive;
using UnityEngine;

namespace TarkovSDK
{
    // A forced transit (CustomMapLoader's own feature; Tarkov has none): the transit happens at once, without
    // the transit menu, its confirmation or its countdown, when a player walks into the transit's zone and/or
    // when a trigger id fires. Editor-only data on an EditorOnly child of the transit: Build Map exports it to
    // sdk_export.json, and the client plugin forces the transit in the raid.
    [AddComponentMenu("Tarkov SDK/Force Transit")]
    public class TarkovForceTransit : MonoBehaviour
    {
        [Tooltip("The transit to force.")]
        public TransitPoint Transit;

        [Tooltip("Force it as soon as a player walks into the transit's zone.")]
        public bool OnEnter = true;

        [Tooltip("Also force it for the player who fires this trigger id (a zone or a switch; connect one in Triggers & Sounds). Empty: no trigger.")]
        public string TriggerId = "";

        [Tooltip("Ignore the transit's required items (its access keys). Off: a player without them gets the game's 'access not granted' message.")]
        public bool IgnoreRequirements;

        [Tooltip("Only its trigger sends players on: the transit is exported inactive, so the game turns its zone off and never shows it " +
                 "in the raid's transit list or timers. Needs a Trigger Id; On Enter doesn't apply.")]
        public bool Hidden;
    }
}
