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

/// Capture the Emperor's killer from DamageInfo.Instigator while Kill still has it.
/// GameComponent death handling runs later, after the DamageInfo is gone.
[HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
internal static class Patch_Pawn_Kill_EmperorAssassin
{
    [HarmonyPrefix]
    private static void Prefix(Pawn __instance, DamageInfo? dinfo)
    {
        if (__instance == null || __instance.Dead)
        {
            return;
        }

        RoyaltyRegenesisQuestSystem system = RoyaltyRegenesisQuestSystem.CurrentSystem;
        if (system == null || !system.IsEmperorRegenContractActive())
        {
            return;
        }

        if (!system.IsEmperorContractPawn(__instance))
        {
            return;
        }

        Pawn killer = dinfo?.Instigator as Pawn;
        system.NotifyEmperorKillInstigator(killer);
    }
}
