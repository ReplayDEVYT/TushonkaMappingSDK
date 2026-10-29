using UnityEngine;

namespace TarkovSDK
{
    // A player-sized figure for judging scale while building: drawn in the Scene view only (Tarkov SDK > Tools >
    // Geometry > Player Scale), at the game's sizes for the pose: its CharacterController (1.6 m standing, 1.2 m at full
    // crouch, 0.366 m radius) and the character's 1.8 m height. Selected, it also marks what the player can step up,
    // vault and climb. Editor-only data on an EditorOnly object.
    [AddComponentMenu("Tarkov SDK/Player Scale")]
    public class TarkovPlayerScale : MonoBehaviour
    {
        public enum Poses
        {
            Standing,
            Crouching,
            Prone,
        }

        [Tooltip("The pose the figure shows.")]
        public Poses Pose = Poses.Standing;

        [Tooltip("When selected: the heights the player can step up (0.2 m), vault (0.25 to 1.1 m) and climb (to 1.31 m), and the CharacterController.")]
        public bool ShowLimits = true;
    }
}
