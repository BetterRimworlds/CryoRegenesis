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
