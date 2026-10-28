using System;
using System.IO;
using System.Linq;
using EFT;
using EFT.Ballistics;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Animated objects (doors, gates, hatches, lift platforms): the sliding door's setup made general, from the
    // game's own components. An Animator with Closed (default) and Open states switched by an IsOpened bool, a
    // HandlerAnimator that sets it when its trigger ids fire, AnimatorSync (the game syncs the state), and
    // AutoDoorSound for the clips' OnStartOpen / OnStartClose events. Made with its own switch; Triggers & Sounds
    // connects any other zone or switch. The clips are the mapper's own, or made from Move By / Rotate By.
    public static class TarkovAnimatedObjects
    {
        private const string Menu = "GameObject/Tarkov SDK/";
        public const string Parameter = "IsOpened";

        [MenuItem(Menu + "Interactables/Animated Object", priority = 36)]
        private static void MenuAnimatedObject(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(p).Animator.gameObject);

        // withSwitch: its own switch opens and closes it; without, connect a zone or switch in Triggers & Sounds
        public static TarkovAnimatedObject Create(Transform parent, bool withSwitch = true)
        {
            string id = TarkovFacilitySystems.ShortId();
            var root = TarkovFacilitySystems.Child(parent, "AnimatedObject", Vector3.zero);
            var moving = TarkovFacilitySystems.Panel(root.transform, "Moving", Vector3.zero, new Vector3(1.2f, 2.1f, 0.06f), MaterialType.MetalThin);
            // a door hinged on its left edge by default
            foreach (Transform child in moving.transform)
                child.localPosition += new Vector3(0.6f, 0f, 0f);

            var animator = root.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var handler = root.AddComponent<HandlerAnimator>();
            handler._target = animator;
            handler._animatorActions = new HandlerAnimator.AnimatorAction[0];
            var sync = root.AddComponent<AnimatorSync>();
            sync._animator = animator;
            var prefab = root.AddComponent<TriggersPrefab>();
            prefab._processed = true;
            prefab._postfix = id;
            var sound = root.AddComponent<AutoDoorSound>();
            sound._volume = 0.6f;

            var settings = TarkovMapEvents.EditorOnlyChild(root.transform, "SDK_Animation").AddComponent<TarkovAnimatedObject>();
            settings.Animator = animator;
            settings.Moving = moving.transform;

            // its own switch: opening it opens the object, closing it closes it
            if (withSwitch)
            {
                var sw = TarkovPrefabTools.CreateSwitch(root.transform, null);
                sw.name = "Switch";
                sw.transform.localPosition = new Vector3(-0.4f, 1.2f, -0.1f);
                sw.ContextMenuTip = "Open";
                Connect(settings, TarkovTriggerWiringWindow.StateId(sw, EDoorState.Open), true);
                Connect(settings, TarkovTriggerWiringWindow.StateId(sw, EDoorState.Shut), false);
            }

            Rebuild(settings);
            return settings;
        }

        // the object opens (or closes) when the trigger id fires
        public static void Connect(TarkovAnimatedObject settings, string triggerId, bool open)
        {
            var handler = settings.Animator.GetComponent<HandlerAnimator>() ?? Undo.AddComponent<HandlerAnimator>(settings.Animator.gameObject);
            Undo.RecordObject(handler, "Connect animation");
            handler._target = settings.Animator;
            handler._animatorActions = (handler._animatorActions ?? new HandlerAnimator.AnimatorAction[0])
                .Where(a => a.TriggerId != triggerId)
                .Append(new HandlerAnimator.AnimatorAction { TriggerId = triggerId, BoolAction = { Name = Parameter, Value = open } }).ToArray();
            EditorUtility.SetDirty(handler);
        }

        // (Re)makes the Animator Controller and clips next to the scene (<scene>_Animations/), from the moving part's
        // current (closed) pose. Custom clips are used as they are.
        public static void Rebuild(TarkovAnimatedObject settings)
        {
            if (settings.Animator == null)
                return;
            var root = settings.Animator.gameObject;
            var folder = Folder(root);
            string name = $"{root.name}_{Math.Abs(root.GetInstanceID())}";
            var existing = settings.Animator.runtimeAnimatorController as AnimatorController;
            var path = existing != null && AssetDatabase.GetAssetPath(existing).StartsWith(folder, StringComparison.Ordinal)
                ? AssetDatabase.GetAssetPath(existing) : AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.controller");
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter(Parameter, AnimatorControllerParameterType.Bool);
            var open = settings.OpenClip;
            var close = settings.CloseClip;
            if (open == null)
            {
                open = MakeClip(settings, opening: true);
                AssetDatabase.AddObjectToAsset(open, controller);
            }
            if (close == null)
            {
                close = settings.OpenClip == null ? MakeClip(settings, opening: false) : null;
                if (close != null)
                    AssetDatabase.AddObjectToAsset(close, controller);
            }

            var machine = controller.layers[0].stateMachine;
            var closedState = machine.AddState("Closed");
            var openState = machine.AddState("Open");
            openState.motion = open;
            if (close != null)
                closedState.motion = close;
            else
            {
                // a custom opening clip played backwards
                closedState.motion = open;
                closedState.speed = -1f;
            }
            machine.defaultState = closedState;
            var toOpen = closedState.AddTransition(openState);
            toOpen.hasExitTime = false;
            toOpen.duration = 0f;
            toOpen.AddCondition(AnimatorConditionMode.If, 0f, Parameter);
            var toClose = openState.AddTransition(closedState);
            toClose.hasExitTime = false;
            toClose.duration = 0f;
            toClose.AddCondition(AnimatorConditionMode.IfNot, 0f, Parameter);
            AssetDatabase.SaveAssets();

            Undo.RecordObject(settings.Animator, "Animated object");
            settings.Animator.runtimeAnimatorController = controller;
            var sound = root.GetComponent<AutoDoorSound>();
            if (sound != null)
            {
                Undo.RecordObject(sound, "Animated object sound");
                sound._openClip = settings.OpenSound;
                sound._closeClip = settings.CloseSound != null ? settings.CloseSound : settings.OpenSound;
                EditorUtility.SetDirty(sound);
            }
            EditorUtility.SetDirty(settings.Animator);
        }

        private static AnimationClip MakeClip(TarkovAnimatedObject settings, bool opening)
        {
            var clip = new AnimationClip { name = opening ? "Open" : "Close" };
            var moving = settings.Moving;
            if (moving != null && moving != settings.Animator.transform)
            {
                string path = AnimationUtility.CalculateTransformPath(moving, settings.Animator.transform);
                Vector3 shutPosition = moving.localPosition, openPosition = shutPosition + settings.MoveBy;
                Quaternion shutRotation = moving.localRotation, openRotation = shutRotation * Quaternion.Euler(settings.RotateBy);
                float time = settings.Seconds;
                Vector3 fromP = opening ? shutPosition : openPosition, toP = opening ? openPosition : shutPosition;
                Quaternion fromR = opening ? shutRotation : openRotation, toR = opening ? openRotation : shutRotation;
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", AnimationCurve.EaseInOut(0f, fromP.x, time, toP.x));
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", AnimationCurve.EaseInOut(0f, fromP.y, time, toP.y));
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", AnimationCurve.EaseInOut(0f, fromP.z, time, toP.z));
                // sampled so turns over 180 degrees keep their direction
                var rx = new AnimationCurve();
                var ry = new AnimationCurve();
                var rz = new AnimationCurve();
                var rw = new AnimationCurve();
                const int steps = 12;
                for (int i = 0; i <= steps; i++)
                {
                    float u = Mathf.SmoothStep(0f, 1f, i / (float)steps);
                    var euler = opening ? settings.RotateBy * u : settings.RotateBy * (1f - u);
                    var q = shutRotation * Quaternion.Euler(euler);
                    float t = time * i / steps;
                    rx.AddKey(t, q.x);
                    ry.AddKey(t, q.y);
                    rz.AddKey(t, q.z);
                    rw.AddKey(t, q.w);
                }
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rx);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", ry);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rz);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rw);
                clip.EnsureQuaternionContinuity();
            }
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0f, functionName = opening ? "OnStartOpen" : "OnStartClose" } });
            return clip;
        }

        private static string Folder(GameObject root)
        {
            var scenePath = root.scene.path;
            var folder = string.IsNullOrEmpty(scenePath) ? "Assets/Animations"
                : $"{Path.GetDirectoryName(scenePath).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(scenePath)}_Animations";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
            return folder;
        }

        // ------------------------------------------------------------ a clip on any Animator

        // Plays a clip on an Animator when a trigger id fires (HandlerAnimator's trigger action): the clip gets its own
        // state, reached from any state on a trigger parameter named after it, and stays on its last frame.
        public static void ConnectClip(Animator animator, AnimationClip clip, string triggerId)
        {
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller == null)
            {
                var path = AssetDatabase.GenerateUniqueAssetPath($"{Folder(animator.gameObject)}/{animator.gameObject.name}.controller");
                controller = AnimatorController.CreateAnimatorControllerAtPath(path);
                controller.layers[0].stateMachine.defaultState = controller.layers[0].stateMachine.AddState("Idle");
                Undo.RecordObject(animator, "Animator controller");
                animator.runtimeAnimatorController = controller;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                EditorUtility.SetDirty(animator);
            }
            string parameter = "Play_" + clip.name.Replace(' ', '_');
            if (controller.parameters.All(p => p.name != parameter))
                controller.AddParameter(parameter, AnimatorControllerParameterType.Trigger);
            var machine = controller.layers[0].stateMachine;
            var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.motion == clip);
            if (state == null)
            {
                state = machine.AddState(clip.name);
                state.motion = clip;
                var transition = machine.AddAnyStateTransition(state);
                transition.hasExitTime = false;
                transition.duration = 0.1f;
                transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.If, 0f, parameter);
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            var handler = animator.GetComponent<HandlerAnimator>() ?? Undo.AddComponent<HandlerAnimator>(animator.gameObject);
            Undo.RecordObject(handler, "Connect animation");
            handler._target = animator;
            handler._animatorActions = (handler._animatorActions ?? new HandlerAnimator.AnimatorAction[0])
                .Append(new HandlerAnimator.AnimatorAction { TriggerId = triggerId, AnimationTriggerAction = { TriggerName = parameter } }).ToArray();
            EditorUtility.SetDirty(handler);
            if (animator.GetComponent<AnimatorSync>() == null)
                Undo.AddComponent<AnimatorSync>(animator.gameObject)._animator = animator;
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var settings in roots.SelectMany(r => r.GetComponentsInChildren<TarkovAnimatedObject>(true)))
            {
                if (settings.Animator == null)
                {
                    report.Errors.Add($"{scene.name}: animated object '{settings.name}' has no Animator");
                    continue;
                }
                if (settings.Animator.runtimeAnimatorController == null)
                    Rebuild(settings);
                var handler = settings.Animator.GetComponent<HandlerAnimator>();
                if (handler == null || handler._animatorActions == null || handler._animatorActions.Length == 0)
                    report.Warnings.Add($"{scene.name}: animated object '{settings.Animator.name}' isn't connected to anything (connect a switch or zone in Triggers & Sounds)");
            }
        }
    }

    [CustomEditor(typeof(TarkovAnimatedObject))]
    public class TarkovAnimatedObjectEditor : UnityEditor.Editor
    {
        private float _preview;

        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            bool changed = EditorGUI.EndChangeCheck();
            var settings = (TarkovAnimatedObject)target;
            EditorGUILayout.Space();
            if (GUILayout.Button("Rebuild Animation") || changed && settings.OpenClip == null && !AnimationMode.InAnimationMode())
                EditorApplication.delayCall += () => TarkovAnimatedObjects.Rebuild(settings);

            // previews the opening clip without changing the scene
            var clip = settings.OpenClip;
            if (clip == null && settings.Animator != null && settings.Animator.runtimeAnimatorController != null)
                clip = settings.Animator.runtimeAnimatorController.animationClips.FirstOrDefault(c => c.name == "Open");
            if (clip == null || settings.Animator == null)
                return;
            EditorGUI.BeginChangeCheck();
            _preview = EditorGUILayout.Slider(new GUIContent("Preview Open", "0 closed, 1 open; only in the editor."), _preview, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                if (_preview <= 0f)
                {
                    if (AnimationMode.InAnimationMode())
                        AnimationMode.StopAnimationMode();
                }
                else
                {
                    if (!AnimationMode.InAnimationMode())
                        AnimationMode.StartAnimationMode();
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(settings.Animator.gameObject, clip, _preview * clip.length);
                    AnimationMode.EndSampling();
                    SceneView.RepaintAll();
                }
            }
        }

        private void OnDisable()
        {
            if (AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
        }
    }
}
