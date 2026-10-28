using UnityEngine;

namespace TarkovSDK
{
    // A mortar battery (the game's ArtilleryBrigade): its guns are this object's children, where the shells fly from.
    // Each zone is shelled by the battery whose second gun is nearest to it. Retail batteries are three guns 20 m apart,
    // a few hundred metres off the map's edge. The game reads the second gun unchecked and never fires the last one, so
    // a battery needs at least two (three to fire from two). Editor-only data under the map's TarkovMortarSettings.
    [AddComponentMenu("Tarkov SDK/Mortar Battery")]
    public class TarkovMortarBattery : MonoBehaviour
    {
        [Tooltip("The battery's id; every battery on the map needs its own. Build Map fills it in.")]
        public int Id;
    }
}
