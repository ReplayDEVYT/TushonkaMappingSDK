using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using mizmii.CustomMapLoader.Loading;
using SPT.Reflection.Patching;

namespace mizmii.CustomMapLoader.Diagnostics
{
    // BotOwner.method_10 activates a bot's subsystems inside a try/catch that swallows the exception
    // and sets BotState = ActiveFail, leaving a bot that has a brain and a path but never moves.
    // The transpiler hands the caught exception to Report before the catch block discards it, so
    // the real cause and its stack are logged. Enabled by the "Debug/Raid diagnostics" setting.
    internal class BotActivationDiagnostics : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), "method_10");
        }

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var report = AccessTools.Method(typeof(BotActivationDiagnostics), nameof(Report));
            foreach (var instruction in instructions)
            {
                if (instruction.blocks.Any(b => b.blockType == ExceptionBlockType.BeginCatchBlock))
                {
                    // the exception is on the stack where the handler begins: copy it for Report
                    var dup = new CodeInstruction(OpCodes.Dup);
                    instruction.MoveLabelsTo(dup);
                    instruction.MoveBlocksTo(dup);
                    yield return dup;
                    yield return new CodeInstruction(OpCodes.Call, report);
                }
                yield return instruction;
            }
        }

        private static void Report(object caught)
        {
            try
            {
                if (Plugin.RaidDiagnostics == null || !Plugin.RaidDiagnostics.Value || !MapRegistry.IsCustomMapActive())
                    return;
                Plugin.LogSource.LogWarning($"Bot activation failed (BotState.ActiveFail, the bot will not move): {caught}");
            }
            catch
            {
                // diagnostics must never change the game's own handling
            }
        }
    }
}
