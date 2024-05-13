/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * It has been mostly copied from https://github.com/emipa606/DeadCryptosleep/
 *
 * This file is licensed under the MIT License.
 */

using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace FrontierDevelopments.DeadCryptosleep;

[HarmonyPatch(typeof(ReverseDesignatorDatabase), "InitDesignators")]
internal static class Patch_ReverseDesignatorDatabase
{
    [HarmonyPostfix]
    private static void AddDesignator(List<Designator> ___desList)
    {
        var designator = new Designator_HaulCryoRegenesis();
        if (Current.Game.Rules.DesignatorAllowed(designator))
        {
            ___desList.Add(designator);
        }
    }
}
