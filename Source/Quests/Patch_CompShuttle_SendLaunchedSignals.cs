/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// When a regen pickup launches, free colonists are still in the transporter.
/// Snapshot them here so the Imperial Court endgame still fires if they boarded
/// and the ship left in the same tick as the last GameComponent refresh.
///
/// Signature differs by version:
///   1.2:     SendLaunchedSignals(List&lt;CompTransporter&gt;)
///   1.3+:    SendLaunchedSignals()
[HarmonyPatch]
internal static class Patch_CompShuttle_SendLaunchedSignals
{
    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        // Prefer the parameterless form (1.3+), then the 1.2 List form.
        MethodInfo method = AccessTools.Method(typeof(CompShuttle), "SendLaunchedSignals", Type.EmptyTypes);
        if (method != null)
        {
            return method;
        }

        method = AccessTools.Method(
            typeof(CompShuttle),
            "SendLaunchedSignals",
            new[] { typeof(List<CompTransporter>) });
        if (method != null)
        {
            return method;
        }

        // Last resort: any method with that name (Harmony resolves overloads).
        return AccessTools.Method(typeof(CompShuttle), "SendLaunchedSignals");
    }

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

        // Snapshot contract passengers before vanilla destroys or hands off cargo.
        system.SnapshotPickupLaunchClients(__instance.Transporter);

        if (!system.IsEmperorRegenContractActive()
            && !system.IsEmperorAssassinationEvacuationActive())
        {
            return;
        }

        system.NotifyEmperorPickupLaunching(__instance);
    }
}
