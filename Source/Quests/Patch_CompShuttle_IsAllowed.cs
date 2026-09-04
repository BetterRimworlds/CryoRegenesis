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

/// Regen pickup shuttles set acceptColonists so colony pawns can embark, and
/// this patch also allows home-faction contract guests. acceptColonists would
/// otherwise let every free colonist board — restrict those to non-ex
/// DirectRelations of active regen clients (and the clients themselves).
/// During the Emperor contract any number of colonists may leave, but only while the
/// Imperial shuttle is open to them — the Emperor alive and aboard (or sealed in a
/// powered-off casket) and the rest of the party finished.
/// Assassination evacuation opens the door to free colonists and colony pets.
[HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.IsAllowed))]
internal static class Patch_CompShuttle_IsAllowed
{
    [HarmonyPostfix]
    private static void Postfix(CompShuttle __instance, Thing t, ref bool __result)
    {
        if (__instance?.parent == null)
        {
            return;
        }

        Pawn pawn = t as Pawn;
        if (pawn == null)
        {
            return;
        }

        RoyaltyRegenesisQuestSystem system = RoyaltyRegenesisQuestSystem.CurrentSystem;
        if (system == null || !system.IsRegenPickupShuttle(__instance.parent))
        {
            return;
        }

        // Keep What You Kill assassination: assassin, free colonists, and pets may board.
        if (system.IsEmperorAssassinationEvacuationActive()
            && system.MayBoardAssassinationEvacuationShuttle(pawn))
        {
            __result = true;
            return;
        }

        // Contract guests stay in their home faction, so acceptColonists does not
        // cover them. Required pawns still board via vanilla IsRequired; this lets
        // unfinished clients and escorts embark without joining the player faction.
        if (RoyaltyRegenesisQuestSystem.MayBoardRegenPickupShuttle(pawn))
        {
            __result = true;
            return;
        }

        if (!__result)
        {
            return;
        }

        if (!pawn.IsColonist)
        {
            return;
        }

        // requiredPawns / allowed companions (incl. any free colonist on Emperor pickup).
        if (__instance.IsRequired(t) || RoyaltyRegenesisQuestSystem.MayBoardRegenPickupShuttle(pawn))
        {
            return;
        }

        __result = false;
    }
}
