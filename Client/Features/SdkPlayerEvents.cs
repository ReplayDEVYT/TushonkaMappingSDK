using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.AnimationSequencePlayer;
using EFT.Ballistics;
using EFT.GlobalEvents;
using EFT.HealthSystem;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace mizmii.CustomMapLoader.Features
{
    // Health changes and effects for the player who fires a trigger, through their ActiveHealthController as the game
    // applies its own (ApplyDamage destroys a part at 0 HP and kills on the head or chest, as a hit would). Repeats run
    // until their count, the player's death, or the stop trigger id (fired by the same player).
    internal static class SdkHealth
    {
        // "<index>:<profile>" of repeats to stop
        private static readonly HashSet<string> Stopped = new HashSet<string>();
        private static readonly HashSet<string> Running = new HashSet<string>();

        public static void Reset()
        {
            Stopped.Clear();
            Running.Clear();
        }

        public static void Stop(int index, Player player)
        {
            string key = index + ":" + player.ProfileId;
            if (Running.Contains(key))
                Stopped.Add(key);
        }

        public static async void Change(FeatureConfig.HealthChange change, int index, Player player)
        {
            var health = player.ActiveHealthController;
            if (health == null || !health.IsAlive || UnityEngine.Random.value > change.Chance)
                return;
            string key = index + ":" + player.ProfileId;
            if (change.Change == "Kill" || change.Repeat == 1)
            {
                Apply(change, health);
                return;
            }
            // one repeat per player at a time: walking in and out of a gas zone doesn't stack them
            if (!Running.Add(key))
            {
                Stopped.Remove(key);
                return;
            }
            Stopped.Remove(key);
            try
            {
                int count = change.Repeat <= 0 ? int.MaxValue : change.Repeat;
                for (int i = 0; i < count; i++)
                {
                    if (Stopped.Contains(key) || player == null || !health.IsAlive)
                        break;
                    Apply(change, health);
                    float until = Time.realtimeSinceStartup + Mathf.Max(0.1f, change.Interval);
                    while (Time.realtimeSinceStartup < until && !Stopped.Contains(key))
                        await Task.Yield();
                }
            }
            finally
            {
                Running.Remove(key);
                Stopped.Remove(key);
            }
        }

        private static void Apply(FeatureConfig.HealthChange change, ActiveHealthController health)
        {
            var damageType = Enum.TryParse(change.DamageType, out EDamageType parsed) ? parsed : EDamageType.Environment;
            var parts = (change.BodyParts ?? new List<string>()).Select(p => Enum.TryParse(p, out EBodyPart part) ? part : (EBodyPart?)null)
                .Where(p => p.HasValue).Select(p => p.Value).ToList();
            switch (change.Change)
            {
                case "Damage":
                    foreach (var part in parts)
                        if (health.IsAlive)
                            health.ApplyDamage(part, change.Amount, new DamageInfo { DamageType = damageType, Damage = change.Amount });
                    break;
                case "Heal":
                    foreach (var part in parts)
                        health.ChangeHealth(part, Mathf.Abs(change.Amount), new DamageInfo { DamageType = EDamageType.Medicine });
                    break;
                case "Restore":
                    if (!health.IsAlive)
                        break;
                    foreach (var part in parts)
                        health.FullRestoreBodyPart(part);
                    break;
                case "Kill":
                    health.Kill(damageType);
                    break;
                case "Energy":
                    health.ChangeEnergy(change.Amount);
                    break;
                case "Hydration":
                    health.ChangeHydration(change.Amount);
                    break;
                default:
                    Plugin.LogSource.LogWarning($"Map features: unknown health change '{change.Change}'");
                    break;
            }
        }

        public static void Effects(FeatureConfig.EffectLink link, Player player)
        {
            var health = player.ActiveHealthController;
            if (health == null || !health.IsAlive || link.Effects == null || UnityEngine.Random.value > link.Chance)
                return;
            foreach (var e in link.Effects)
            {
                var part = Enum.TryParse(e.BodyPart, out EBodyPart parsed) ? parsed : EBodyPart.Chest;
                switch (e.Effect)
                {
                    case "LightBleeding": health.DoBleed(false, part); break;
                    case "HeavyBleeding": health.DoBleed(true, part); break;
                    case "Fracture": health.DoFracture(part); break;
                    case "Pain": health.DoPain(part, e.Delay, e.Duration, 1f, e.Strength); break;
                    case "Contusion": health.DoContusion(e.Duration, e.Strength); break;
                    case "Stun": health.DoStun(e.Duration, e.Strength); break;
                    case "Disorientation": health.DoDisorientation(e.Duration); break;
                    case "SandingScreen": health.DoSandingScreen(e.Duration); break;
                    case "Intoxication": health.DoIntoxication(); break;
                    case "LethalIntoxication": health.DoLethalIntoxication(e.Delay, e.Duration); break;
                    case "RadExposure": health.DoRadExposure(); break;
                    case "StaminaZero": health.AddStaminaZeroffect(e.Duration); break;
                    case "Fatigue": health.AddFatigue(); break;
                    case "Painkiller": health.DoPainKiller(); break;
                    case "Buff": health.DoExternalBuff(e.Buff); break;
                    case "RemoveNegativeEffects": health.RemoveNegativeEffects(part); break;
                    case "StopBleeding": health.RemoveBleedingEffects(part); break;
                    default: Plugin.LogSource.LogWarning($"Map features: unknown effect '{e.Effect}'"); break;
                }
            }
        }
    }

    // Captions in the game's own caption view. SubtitlesView (the Lightkeeper's dialogue captions) only exists on the
    // trader dialog screen, so a copy of it goes on an overlay canvas of our own at the bottom of the screen; lines go to
    // it through the game's SubtitlesEvent, on a source of our own so the dialog screen's view ignores them. Its lines
    // are localisation keys, and a key the locale doesn't have shows as itself, so plain text works.
    internal static class SdkCaptions
    {
        private const ESubtitlesSource Source = (ESubtitlesSource)100;
        private static GameObject _canvas;
        private static SubtitlesView _view;

        public static void Show(FeatureConfig.CaptionLink caption)
        {
            var lines = (caption.Lines ?? new List<FeatureConfig.CaptionLine>())
                .Select(l => new SubtitleParams { Key = l.Text, Start = l.Start, End = l.End }).ToList();
            if (lines.Count == 0)
                return;
            if (View() == null)
            {
                Plugin.LogSource.LogWarning("Map features: the game's caption view isn't loaded; showing the captions as a notification");
                RaidFeatures.Notify(new FeatureConfig.Notification { Message = string.Join("\n", lines.Select(l => l.Key)), Icon = "Note", Duration = "Long", Sound = "None" });
                return;
            }
            GlobalEventsController.Instance.CreateCommonEvent<SubtitlesEvent>().Invoke(Source, lines);
        }

        private static SubtitlesView View()
        {
            if (_view != null)
                return _view;
            var source = MonoBehaviourSingleton<CommonUI>.Instantiated ? MonoBehaviourSingleton<CommonUI>.Instance.TraderDialogScreen?._subtitlesView : null;
            if (source == null)
                return null;
            _canvas = new GameObject("SDK Captions");
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            var copy = Object.Instantiate(source.gameObject, _canvas.transform, false);
            copy.name = "Captions";
            // bottom centre, above the hotbar
            var rect = (RectTransform)copy.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 150f);
            rect.sizeDelta = new Vector2(CaptionFitter.MaxWidth, 120f);
            _view = copy.GetComponent<SubtitlesView>();
            copy.AddComponent<CaptionFitter>().Init(rect, _view._textField);
            _view.Show(Source);
            return _view;
        }

        // The dialog screen lays its caption box out for a wide panel, so on its own it's a fixed box with the text in
        // its top-left corner. This sizes the box to the text (up to MaxWidth, wrapping beyond it) whenever the text
        // changes, keeping the text's own padding, with the lines centred.
        private sealed class CaptionFitter : MonoBehaviour
        {
            public const float MaxWidth = 1400f;
            private RectTransform _rect;
            private TMP_Text _text;
            private Vector2 _padding;
            private string _last;

            public void Init(RectTransform rect, TMP_Text text)
            {
                _rect = rect;
                _text = text;
                var textRect = text.rectTransform;
                bool stretched = textRect.parent == rect && textRect.anchorMin == Vector2.zero && textRect.anchorMax == Vector2.one;
                // even padding from the smaller of the text's own insets: the dialog screen's bottom inset is larger
                // (room under its text), which left a gap below one-line captions
                var inset = stretched
                    ? new Vector2(Mathf.Min(textRect.offsetMin.x, -textRect.offsetMax.x), Mathf.Min(textRect.offsetMin.y, -textRect.offsetMax.y))
                    : new Vector2(18f, 10f);
                inset = new Vector2(Mathf.Clamp(inset.x, 8f, 24f), Mathf.Clamp(inset.y, 6f, 16f));
                var min = inset;
                var max = inset;
                text.margin = Vector4.zero;
                // the text fills the box inside the padding, so sizing the box sizes the text
                if (textRect.parent == rect)
                {
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = min;
                    textRect.offsetMax = -max;
                }
                _padding = min + max;
                text.alignment = TextAlignmentOptions.Center;
                text.enableWordWrapping = true;
            }

            private void LateUpdate()
            {
                if (_text == null || _text.text == _last)
                    return;
                // SubtitlesView ends every line with a newline (AppendLine), which would add an empty line below
                string trimmed = _text.text.TrimEnd('\n', '\r');
                if (trimmed != _text.text)
                    _text.text = trimmed;
                _last = trimmed;
                if (string.IsNullOrEmpty(trimmed))
                    return;
                float inner = MaxWidth - _padding.x;
                var preferred = _text.GetPreferredValues(trimmed, inner, 0f);
                float width = Mathf.Min(preferred.x, inner);
                // measured again at the final width, so wrapped lines get their height
                float height = _text.GetPreferredValues(trimmed, width, 0f).y;
                _rect.sizeDelta = new Vector2(width + _padding.x, height + _padding.y);
                _settle = 4;
            }

            private int _settle;

            private void Update()
            {
                if (_settle <= 0 || _text == null || string.IsNullOrEmpty(_last))
                    return;
                _settle--;
                FitToGlyphs();
            }

            // The preferred height holds more than the letters (the font's line height, and the dialog screen's text
            // settings), which left a gap under the caption; so the box is fitted to where the letters really are:
            // the text moved so its top line is the padding below the box's top, and the box's height made the
            // letters' height plus the padding twice. Repeated for a few frames, as moving one can move the other.
            private void FitToGlyphs()
            {
                _text.ForceMeshUpdate();
                var bounds = _text.textBounds;
                if (bounds.size.y <= 0f)
                    return;
                var textRect = _text.rectTransform;
                float top = _rect.InverseTransformPoint(textRect.TransformPoint(new Vector3(bounds.center.x, bounds.max.y))).y;
                float bottom = _rect.InverseTransformPoint(textRect.TransformPoint(new Vector3(bounds.center.x, bounds.min.y))).y;
                var box = _rect.rect;
                float pad = _padding.y / 2f;
                float shift = box.yMax - pad - top;
                if (Mathf.Abs(shift) > 0.5f)
                    textRect.anchoredPosition += new Vector2(0f, shift);
                float wanted = top - bottom + 2f * pad;
                if (Mathf.Abs(wanted - box.height) > 0.5f)
                    _rect.sizeDelta = new Vector2(_rect.sizeDelta.x, wanted);
            }
        }

        public static void Reset()
        {
            if (_view != null)
                _view.Close();
            if (_canvas != null)
                Object.Destroy(_canvas);
            _canvas = null;
            _view = null;
        }
    }
}
