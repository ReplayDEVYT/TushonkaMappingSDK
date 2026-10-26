using UnityEngine;

namespace TarkovSDK
{
    // An animated object (a door, gate, hatch or lift platform that moves when used), built from the game's own
    // components as Labs' sliding doors: an Animator with Closed/Open states on an IsOpened bool, which a
    // HandlerAnimator sets when its trigger ids fire (its own switch, or any zone or switch connected in
    // Triggers & Sounds). Editor-only data on an EditorOnly child: the SDK makes the animation from it.
    [AddComponentMenu("Tarkov SDK/Animated Object")]
    public class TarkovAnimatedObject : MonoBehaviour
    {
        [Tooltip("The Animator that plays it (on the object's root).")]
        public Animator Animator;

        [Tooltip("The part that moves (its current position is the closed one). Put your model under it.")]
        public Transform Moving;

        [Tooltip("How far it moves when open, in its parent's space (metres).")]
        public Vector3 MoveBy = new Vector3(0f, 0f, 0f);

        [Tooltip("How far it turns when open, in degrees around its own axes.")]
        public Vector3 RotateBy = new Vector3(0f, 90f, 0f);

        [Tooltip("Seconds to open or close.")]
        [Min(0.05f)]
        public float Seconds = 1.5f;

        [Tooltip("Your own opening animation (on the Animator's object). Empty: made from Move By and Rotate By.")]
        public AnimationClip OpenClip;

        [Tooltip("Your own closing animation. Empty: the opening one played backwards (or made from Move By and Rotate By).")]
        public AnimationClip CloseClip;

        [Tooltip("Plays when it starts opening (and closing, unless Close Sound is set).")]
        public AudioClip OpenSound;
        public AudioClip CloseSound;
    }
}
