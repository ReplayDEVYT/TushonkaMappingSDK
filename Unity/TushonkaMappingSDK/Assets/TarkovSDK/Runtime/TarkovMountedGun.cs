using UnityEngine;

namespace TarkovSDK
{
    // A mounted machine gun (the game's NSV "Utes" on its tripod, as on Woods). The game ships it as a world prefab in
    // the weapon's own bundle, so the SDK can't show it in the editor: this marks where it stands (on the ground, facing
    // the way it fires). Build Map exports it to sdk_export.json; the server adds the gun to the raid's loot (as retail
    // Woods' static weapons) and the client plugin places the game's prefab here before the loot spawns.
    // Editor-only data on an EditorOnly object.
    [AddComponentMenu("Tarkov SDK/Mounted Gun")]
    public class TarkovMountedGun : MonoBehaviour
    {
        public enum GunKind
        {
            [Tooltip("NSV \"Utes\" 12.7x108 heavy machine gun, fired sitting behind it.")]
            Utes,
        }

        public GunKind Gun = GunKind.Utes;

        [Tooltip("The gun's static weapon id; made unique when the map is built if empty.")]
        public string Id = "";
    }
}
