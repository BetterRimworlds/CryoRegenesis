/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using HarmonyLib;
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// When a regen pickup launches, free colonists are still in the transporter.
/// Snapshot them here so the Imperial Court endgame still fires if they boarded
/// and the ship left in the same tick as the last GameComponent refresh.
[HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.SendLaunchedSignals))]
internal static class Patch_CompShuttle_SendLaunchedSignals
{
    [HarmonyPrefix]
    private static void Prefix(CompShuttle __instance)
    {
        if (__instance?.parent == null)
        {
            return;
        }

        RoyaltyRegenesisQuestSystem system = RoyaltyRegenesisQuestSystem.CurrentSystem;
        if (system == null || !system.IsRegenPickupShuttle(__instance.parent))
        {
            return;
        }

        if (!system.IsEmperorRegenContractActive()
            && !system.IsEmperorAssassinationEvacuationActive())
        {
            return;
        }

        system.NotifyEmperorPickupLaunching(__instance);
    }
}
