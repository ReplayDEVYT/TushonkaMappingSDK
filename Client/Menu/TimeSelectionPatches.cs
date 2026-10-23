using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT.UI.Matchmaker;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;

namespace mizmii.CustomMapLoader.Menu
{
    // Time selection (the "SELECT TIME PHASE" bar under the map select screen): the game offers it
    // for the locations in LocationSettings.Location.NightTimeAllowedLocations, its fixed list of
    // retail maps minus Labs. Everywhere else the raid starts at the current time (CURR); the second
    // choice (PAST) starts it 12 hours later (BaseLocalGame.InitWeather), on any map.
    //
    //  - NightTimeAllowedLocations decides whether the bar shows (OnLocationSelected) and whether the
    //    choice survives to the raid (TarkovApplication resets it to CURR for other maps)
    //  - SelectionLocationScreenController.UpdateSelectedDateTime resets it the same way, checking
    //    the AvailableMaps list directly
    //
    // Maps whose menu.json has TimeSelection join both.
    internal class TimeSelectionLocationsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.PropertyGetter(typeof(LocationSettings.Location), nameof(LocationSettings.Location.NightTimeAllowedLocations));

        [PatchPostfix]
        private static void Postfix(ref IEnumerable<string> __result)
        {
            if (MenuConfig.TimeSelectionMaps.Count > 0)
                __result = __result.Concat(MenuConfig.TimeSelectionMaps).ToList();
        }
    }

    internal class KeepSelectedTimePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen.SelectionLocationScreenController), "UpdateSelectedDateTime");

        [PatchPrefix]
        private static bool Prefix(MatchMakerSelectionLocationScreen.SelectionLocationScreenController __instance) =>
            !MenuConfig.HasTimeSelection(__instance.RaidSettings?.SelectedLocation?.Id);
    }
}
