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

using System;
using RimWorld;
using UnityEngine;
using Verse;
// ReSharper disable All

namespace BetterRimworlds.CryoRegenesis;

public static class RegenesisThoughts
{
    private const int YearsPerStack = 15;

    /// Grant stackable body-positivity memories after a CryoRegenesis eject that
    /// actually de-aged the pawn. Stage, description years, and duration use
    /// lifetime years erased from the True Age tracker (must be updated first).
    /// Stack count scales with how many years this session removed.
    /// <param name="sessionYearsRemoved">Whole years of bio-age removed this eject (gate + multi-stack).</param>
    public static void AddBodyPositivityThought(Pawn pawn, int sessionYearsRemoved)
    {
        if (pawn?.needs?.mood?.thoughts?.memories == null)
        {
            return;
        }

        if (sessionYearsRemoved <= 0)
        {
            return;
        }

        TrueAgeTracker tracker = pawn.health?.hediffSet?
            .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
        if (tracker == null)
        {
            return;
        }

        int totalYearsErased = Mathf.RoundToInt(tracker.GetCryoRegenesisRemovedAgeYears());
        if (totalYearsErased <= 0)
        {
            return;
        }

        ThoughtDef thoughtDef = DefDatabase<ThoughtDef>.GetNamed("CryoRegenesis_BodyPositivity");
        if (thoughtDef == null)
        {
            return;
        }

        int stacksToAdd = Math.Max(1, (int)Math.Ceiling(sessionYearsRemoved / (double)YearsPerStack));
        if (thoughtDef.stackLimit > 0)
        {
            stacksToAdd = Math.Min(stacksToAdd, thoughtDef.stackLimit);
        }

        Thought_RegenesisBodyPositivity lastThought = null;
        for (int i = 0; i < stacksToAdd; i++)
        {
            // Set years before Init so CalculateDuration sees the True Age total.
            var thought = (Thought_RegenesisBodyPositivity)Activator.CreateInstance(thoughtDef.thoughtClass);
            thought.def = thoughtDef;
            thought.SetYearsReversed(totalYearsErased);
            thought.Init();

            pawn.needs.mood.thoughts.memories.TryGainMemory(thought);
            lastThought = thought;
        }

        if (CryoRegenesis.Settings.debugMode)
        {
            Log.Warning(
                $"Body positivity: session {sessionYearsRemoved}y | lifetime erased {totalYearsErased}y | " +
                $"stacks +{stacksToAdd} | stage {lastThought?.CurStageIndex}");
        }

        if (lastThought == null)
        {
            return;
        }

        // Use the thought's actual stage for the prisoner check
        #if !RIMWORLD12 && !RIMWORLD13
        if (lastThought.CurStageIndex >= 2)
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
