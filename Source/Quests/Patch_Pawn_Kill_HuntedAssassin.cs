/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using HarmonyLib;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Don't Kill It (2016): kill the Marked One and the mark jumps to a worthy host.
/// Any Empire Knight (or higher) who lands the killing blow inherits HuntedAssassin
/// while the victim is still alive enough for DamageInfo / hediff readout.
[HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
internal static class Patch_Pawn_Kill_HuntedAssassin
{
    [HarmonyPrefix]
    private static void Prefix(Pawn __instance, DamageInfo? dinfo)
    {
        if (__instance == null || __instance.Dead)
        {
            return;
        }

        if (!HuntedAssassinUtility.HasMark(__instance))
        {
            return;
        }

        Pawn killer = dinfo?.Instigator as Pawn;
        if (killer == null)
        {
            return;
        }

        HuntedAssassinUtility.TryInheritFromKill(__instance, killer);
    }
}
