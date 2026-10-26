using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace mizmii.CustomMapLoader.Menu
{
    // Map packs as pages of the map select screen (MatchMakerSelectionLocationScreen).
    //
    // Page 0 is Tarkov's: the retail locations and every custom map without a pack. Each installed
    // pack adds a page with its own background in place of the Tarkov map picture (Map/Image, the
    // parent of the location buttons and paths, which are anchored in its 0..1 space, so IconX/IconY
    // place a map's button on its pack's picture just as on Tarkov's). The game's own Map button,
    // cloned into the bottom button row on each side, turns the pages; the page's name is a third
    // line under the screen's caption.
    //
    //  - ShowInternal (the screen's build) shows only the current page's locations (DisplayLocation)
    //    and only the paths between them; the page opens on the selected location's page
    //  - turning the page re-runs the screen's Show, which disposes and rebuilds its buttons
    //  - the marathon event mode has its own locations and keeps Tarkov's page
    internal static class MapPages
    {
        public static int Current;
        public static bool Active;
        public static bool Turning;

        public static bool OnPage(LocationSettings.Location location) =>
            location != null && MenuConfig.PageOf(location.Id) == Current;
    }

    internal class MapSelectShowPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<MatchMakerSelectionLocationScreen, IEftSession> Session =
            AccessTools.FieldRefAccess<MatchMakerSelectionLocationScreen, IEftSession>("_session");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), nameof(MatchMakerSelectionLocationScreen.ShowInternal));

        [PatchPrefix]
        private static void Prefix(MatchMakerSelectionLocationScreen __instance, RaidSettings raidSettings, out LocationSettings.LocationPath[] __state)
        {
            __state = null;
            try
            {
                bool eventMode = (raidSettings.transitionType & ELocationTransition.Event) != 0;
                MapPages.Active = !eventMode && MenuConfig.PageCount > 1;
                if (!MapPages.Active)
                {
                    MapPages.Current = 0;
                    return;
                }
                if (!MapPages.Turning && raidSettings.SelectedLocation != null)
                    MapPages.Current = MenuConfig.PageOf(raidSettings.SelectedLocation.Id);
                MapPages.Current = Mathf.Clamp(MapPages.Current, 0, MenuConfig.PageCount - 1);

                // paths join locations by _Id; keep the ones with both ends on this page
                var settings = Session(__instance)?.LocationSettings;
                if (settings?.paths == null)
                    return;
                __state = settings.paths;
                settings.paths = settings.paths.Where(p =>
                    settings.locations.TryGetValue(p.Source, out var source) && MapPages.OnPage(source) &&
                    settings.locations.TryGetValue(p.Destination, out var destination) && MapPages.OnPage(destination)).ToArray();
            }
            catch (Exception e)
            {
                MapPages.Active = false;
                Plugin.LogSource.LogError($"Map pages failed: {e}");
            }
        }

        // a finalizer runs even when the screen's build throws, so the full path list always comes back
        [PatchFinalizer]
        private static void Finalizer(MatchMakerSelectionLocationScreen __instance, LocationSettings.LocationPath[] __state)
        {
            if (__state != null)
            {
                var settings = Session(__instance)?.LocationSettings;
                if (settings != null)
                    settings.paths = __state;
            }
            try
            {
                var pager = __instance.GetComponent<MapPager>();
                if (pager == null)
                    pager = __instance.gameObject.AddComponent<MapPager>();
                pager.Refresh(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Map pages failed: {e}");
            }
        }
    }

    internal class DisplayLocationPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), nameof(MatchMakerSelectionLocationScreen.DisplayLocation));

        [PatchPostfix]
        private static void Postfix(LocationSettings.Location location, ref bool __result)
        {
            if (__result && MapPages.Active)
                __result = MapPages.OnPage(location);
        }
    }

    // A custom map's location icon (menu.json Icon, else its pack's) in place of the white dot the
    // game shows on an available location's button (LocationButton._availableIcon). The dot's sprite is
    // 49 x 49 with the dot itself in the middle 31 px (the rest is transparent glow), so a picture on that
    // Image would fill all 49 px and stretch to square; the icon gets its own child Image instead, fitted
    // into the dot's visible size with its aspect kept, and the dot is hidden while it shows.
    internal class LocationIconPatch : ModulePatch
    {
        private const string IconName = "CustomMapLoader Icon";
        private const float IconSize = 31f;
        // the icon each button last asked for, so a late picture never lands on a reused button
        private static readonly Dictionary<Image, string> Wanted = new Dictionary<Image, string>();

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocationButton), nameof(LocationButton.Show));

        [PatchPostfix]
        private static void Postfix(LocationButton __instance, LocationSettings.Location location)
        {
            var dot = __instance._availableIcon != null ? __instance._availableIcon.GetComponent<Image>() : null;
            if (dot == null)
                return;
            var icon = MenuConfig.IconOf(location?.Id);
            Wanted[dot] = icon;
            Show(dot, null);
            if (icon == null)
                return;
            MenuConfig.GetSprite(icon, sprite =>
            {
                if (dot != null && Wanted.TryGetValue(dot, out var wanted) && wanted == icon)
                    Show(dot, sprite);
            });
        }

        // the icon over the dot, or the dot back when sprite is null
        private static void Show(Image dot, Sprite sprite)
        {
            var existing = dot.transform.Find(IconName);
            var image = existing != null ? existing.GetComponent<Image>() : null;
            if (sprite == null)
            {
                dot.enabled = true;
                if (image != null)
                    image.gameObject.SetActive(false);
                return;
            }
            if (image == null)
            {
                var go = new GameObject(IconName, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(dot.transform, false);
                var rect = (RectTransform)go.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(IconSize, IconSize);
                image = go.GetComponent<Image>();
                image.preserveAspect = true;
                image.raycastTarget = false;
            }
            image.sprite = sprite;
            image.gameObject.SetActive(true);
            dot.enabled = false;
        }
    }

    // The page buttons, the page's name and the background, kept on the screen's object
    internal class MapPager : MonoBehaviour
    {
        private static readonly FieldInfo ScreenControllerField = AccessTools.Field(typeof(MatchMakerSelectionLocationScreen), "ScreenController");
        private static readonly AccessTools.FieldRef<MatchMakerSelectionLocationScreen, RaidSettings> RaidSettingsOf =
            AccessTools.FieldRefAccess<MatchMakerSelectionLocationScreen, RaidSettings>("_raidSettings");

        private MatchMakerSelectionLocationScreen _screen;
        private Image _map;
        private AspectRatioFitter _fitter;
        private Sprite _tarkovMap;
        private float _tarkovAspect = 1f;
        private DefaultUIButton _previous, _next;
        private TextMeshProUGUI _title;
        private string _background;

        public void Refresh(MatchMakerSelectionLocationScreen screen)
        {
            // built the first time there are pages to turn
            if (_screen == null && (!MapPages.Active || !Build(screen)))
                return;
            bool active = MapPages.Active;
            _previous.gameObject.SetActive(active);
            _next.gameObject.SetActive(active);
            _title.gameObject.SetActive(active);

            var pack = active ? MenuConfig.PackOfPage(MapPages.Current) : null;
            _title.text = (pack?.Name ?? "Tarkov").ToUpperInvariant() + $"  {MapPages.Current + 1}/{MenuConfig.PageCount}";
            ShowBackground(pack?.Background);
        }

        private bool Build(MatchMakerSelectionLocationScreen screen)
        {
            // the picture: the nearest parent of the location buttons with an AspectRatioFitter
            for (var t = screen._locationButtonsContainer; t != null && t != screen.transform; t = t.parent)
                if (t.TryGetComponent(out _fitter) && t.TryGetComponent(out _map))
                    break;
            if (_map == null || _fitter == null)
            {
                Plugin.LogSource.LogWarning("Map pages: the map select screen's map picture was not found");
                return false;
            }
            _screen = screen;
            _tarkovMap = _map.sprite;
            _tarkovAspect = _fitter.aspectRatio;

            // in the bottom row with the game's own buttons (MAP at -300, NEXT/BACK in the middle, READY
            // at +300), further out on each side; the space above the map is off-screen at 1080p
            _previous = PageButton("MapPagePrevious", "PREVIOUS", -1);
            _next = PageButton("MapPageNext", "NEXT", 1);

            // the page's name as a third line under SELECT LOCATION and the game mode, in their layout group
            _title = Instantiate(screen._subCaption, screen._subCaption.transform.parent, false);
            _title.name = "MapPageTitle";
            _title.transform.SetAsLastSibling();
            _title.fontSize = 22f;
            _title.alignment = TextAlignmentOptions.Center;
            _title.enableWordWrapping = false;
            return true;
        }

        private DefaultUIButton PageButton(string name, string text, int direction)
        {
            var template = _screen._pocketMapButton;
            var button = Instantiate(template, template.transform.parent, false);
            button.name = name;
            button.gameObject.SetActive(true);
            button.OnClick.RemoveAllListeners();
            button.OnClick.AddListener(() => Turn(direction));
            button.SetRawText(text, button.HeaderSize);
            // the inner edge 470 from the centre, growing outwards as the label sizes the button
            var rect = (RectTransform)button.transform;
            var origin = ((RectTransform)template.transform).anchoredPosition;
            rect.pivot = new Vector2(direction < 0 ? 1f : 0f, rect.pivot.y);
            rect.anchoredPosition = new Vector2(470f * direction, origin.y);
            return button;
        }

        private void Turn(int direction)
        {
            try
            {
                int count = MenuConfig.PageCount;
                MapPages.Current = ((MapPages.Current + direction) % count + count) % count;
                // a selection left on another page would stay picked out of sight
                var raidSettings = RaidSettingsOf(_screen);
                if (raidSettings?.SelectedLocation != null && !MapPages.OnPage(raidSettings.SelectedLocation))
                    raidSettings.SelectedLocation = null;
                var controller = (MatchMakerSelectionLocationScreen.SelectionLocationScreenController)ScreenControllerField.GetValue(_screen);
                MapPages.Turning = true;
                _screen.Show(controller);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Turning the map page failed: {e}");
            }
            finally
            {
                MapPages.Turning = false;
            }
        }

        private void ShowBackground(string background)
        {
            _background = background;
            if (background == null)
            {
                _map.sprite = _tarkovMap;
                _fitter.aspectRatio = _tarkovAspect;
                return;
            }
            MenuConfig.GetSprite(background, sprite =>
            {
                // the page may have turned while the picture loaded
                if (this == null || _background != background)
                    return;
                if (sprite == null)
                {
                    _map.sprite = _tarkovMap;
                    _fitter.aspectRatio = _tarkovAspect;
                    return;
                }
                _map.sprite = sprite;
                _fitter.aspectRatio = sprite.rect.width / sprite.rect.height;
            });
        }
    }
}
