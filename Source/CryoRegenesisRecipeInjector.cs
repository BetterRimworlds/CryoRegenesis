// ==== Source/CryoRegenesisRecipeInjector.cs ====
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

[StaticConstructorOnStartup]
public static class CryoRegenesisRecipeInjector
{
    static CryoRegenesisRecipeInjector()
    {
        RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(
            "CR_AdministerCryoRegenesisSedation"
        );

        if (recipe == null)
        {
            Log.Error(
                "[CryoRegenesis] Could not find recipe " +
                "CR_AdministerCryoRegenesisSedation."
            );
            return;
        }

        if (recipe.recipeUsers == null)
            recipe.recipeUsers = new List<ThingDef>();

        foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
        {
            RaceProperties race = thingDef.race;

            if (race == null)
                continue;

            if (!race.Humanlike && !race.Animal)
                continue;

            if (!recipe.recipeUsers.Contains(thingDef))
                recipe.recipeUsers.Add(thingDef);
        }
    }
}