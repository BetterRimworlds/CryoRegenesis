// ==== ./Source/RegenesisThoughts.cs ====
/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *   GPG Fingerprint: D8EA 6E4D 5952 159D 7759  2BB4 EEB6 CE72 F441 EC41
 *   https://github.com/BetterRimworlds/CryoRegenesis
 *
 * This file is licensed under the MIT License.
 */

using RimWorld;
using Verse;
// ReSharper disable All

namespace BetterRimworlds.CryoRegenesis;

public static class RegenesisThoughts
{
    public static void AddBodyPositivityThought(Pawn pawn, int origAge, int newAge)
    {
        int yearsRegressed = origAge - newAge;
        if (yearsRegressed <= 0) return;

        // Get the def
        var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("CryoRegenesis_BodyPositivity");

        // Create the thought instance manually so we can initialize it
        var thought = ThoughtMaker.MakeThought(thoughtDef) as Thought_RegenesisBodyPositivity;
        if (thought == null) return;

        // Set the years BEFORE adding it to the pawn
        thought.SetYearsReversed(yearsRegressed);

        // Add the initialized thought instance (not the def)
        pawn.needs.mood?.thoughts.memories.TryGainMemory(thought);

        if (CryoRegenesis.Settings.debugMode)
            Log.Warning($"Old age {origAge} | New age: {newAge} | Years reversed: {yearsRegressed} | Stage: {thought.CurStageIndex}");

        // Use the thought's actual stage for the prisoner check
        #if !RIMWORLD12 && !RIMWORLD13
        if (thought.CurStageIndex >= 2)
        {
            // Regen-quest contract clients must never become voluntarily recruitable.
            TrueAgeTracker tracker = pawn.health?.hediffSet?
                .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
            bool underContract = (tracker != null && tracker.underRegenContract)
                || RoyaltyRegenesisQuestSystem.IsActiveRegenContractPawn(pawn);

            if (!underContract && pawn.IsPrisoner && pawn.guest != null && !pawn.guest.Recruitable)
            {
                pawn.guest.Recruitable = true;
                Messages.Message("Grateful to be so much younger, " + pawn.Name + " is now recruitable.", pawn, MessageTypeDefOf.PositiveEvent);
            }
        }
        #endif
    }

    /// Get the pawn's existing TrueAgeTracker, or add and initialize one.
    ///
    /// On first use, the tracker is initialized with the pawn's
    /// pre-CryoRegenesis biological age — because before this moment,
    /// True Age = Bio Age.
    public static TrueAgeTracker GetOrAddTrueAgeTracker(Pawn pawn, long preRegenesisBioTicks)
    {
        if (pawn?.health?.hediffSet == null || pawn.RaceProps?.Humanlike != true)
        {
            return null;
        }

        HediffDef trueAgeDef = DefDatabase<HediffDef>.GetNamedSilentFail("TrueAgeTracker");
        if (trueAgeDef == null)
        {
            Log.Warning("[CryoRegenesis] TrueAgeTracker hediff def is missing; skipping true-age tracking.");
            return null;
        }

        TrueAgeTracker tracker = pawn.health.hediffSet
            .GetFirstHediffOfDef(trueAgeDef)
            as TrueAgeTracker;

        if (tracker == null)
        {
            tracker = HediffMaker.MakeHediff(trueAgeDef, pawn) as TrueAgeTracker;

            if (tracker == null)
            {
                return null;
            }

            pawn.health.AddHediff(tracker);
        }

        tracker.Initialize(preRegenesisBioTicks);

        return tracker;
    }
}
