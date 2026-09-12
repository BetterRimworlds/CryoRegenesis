/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using System;
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Hunted Assassin mark helpers: apply, query, and Don't-Kill-It inheritance.
/// When a Knight or higher kills the Marked One, the tracker nanites jump to the killer.
public static class HuntedAssassinUtility
{
    /// 60 days — must match HuntedAssassin.xml disappearsAfterTicks.
    public const int DefaultMarkTicks = 60 * GenDate.TicksPerDay;

    public static HediffDef MarkDef =>
        HuntedAssassinDefOf.HuntedAssassin
        ?? DefDatabase<HediffDef>.GetNamedSilentFail("HuntedAssassin");

    public static bool HasMark(Pawn pawn)
    {
        HediffDef def = MarkDef;
        return def != null
            && pawn?.health?.hediffSet != null
            && pawn.health.hediffSet.HasHediff(def);
    }

    /// Empire royal title of Knight / Dame or higher (any faction member, not only colonists).
    public static bool IsEmpireKnightOrHigher(Pawn pawn)
    {
        if (pawn?.royalty == null || pawn.Destroyed || pawn.Dead)
        {
            return false;
        }

        Faction empire = EmpireFactionOrNull();
        RoyalTitleDef knight = DefDatabase<RoyalTitleDef>.GetNamedSilentFail("Knight");
        if (empire == null || knight == null)
        {
            return false;
        }

        RoyalTitleDef title = pawn.royalty.GetCurrentTitle(empire);
        return title != null && title.seniority >= knight.seniority;
    }

    private static Faction EmpireFactionOrNull()
    {
        FactionDef empireDef = DefDatabase<FactionDef>.GetNamedSilentFail("Empire");
        if (empireDef == null || Find.FactionManager == null)
        {
            return null;
        }

        return Find.FactionManager.FirstFactionOfDef(empireDef);
    }

    public static int? GetRemainingMarkTicks(Pawn pawn)
    {
        HediffDef def = MarkDef;
        if (def == null || pawn?.health?.hediffSet == null)
        {
            return null;
        }

        Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(def);
        HediffComp_Disappears disappears = hediff?.TryGetComp<HediffComp_Disappears>();
        if (disappears == null)
        {
            return null;
        }

        return disappears.ticksToDisappear;
    }

    /// Implant the mark. When <paramref name="freshCountdown"/> is true (inheritance),
    /// the host always gets a full sixty-day clock — each new claimant restarts the hunt.
    public static void ApplyMark(Pawn pawn, bool freshCountdown = false)
    {
        if (pawn?.health?.hediffSet == null || pawn.Dead || pawn.Destroyed)
        {
            return;
        }

        HediffDef def = MarkDef;
        if (def == null)
        {
            Log.Warning("[CryoRegenesis] HuntedAssassin HediffDef not found.");
            return;
        }

        Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(def);
        if (existing == null)
        {
            existing = pawn.health.AddHediff(def);
        }

        if (freshCountdown && existing != null)
        {
            HediffComp_Disappears disappears = existing.TryGetComp<HediffComp_Disappears>();
            if (disappears != null)
            {
                disappears.ticksToDisappear = DefaultMarkTicks;
            }
        }
    }

    /// Don't Kill It + Year of the Four Emperors: kill the Marked One while Knight+,
    /// inherit the nanites, and start a fresh sixty-day succession crisis clock.
    /// Returns true when the mark jumped to the killer.
    public static bool TryInheritFromKill(Pawn victim, Pawn killer)
    {
        if (victim == null || killer == null || killer == victim)
        {
            return false;
        }

        if (killer.Destroyed || killer.Dead)
        {
            return false;
        }

        if (!HasMark(victim) || !IsEmpireKnightOrHigher(killer))
        {
            return false;
        }

        // Fresh sixty days for every new host — each kill restarts the crisis, not the leftover clock.
        ApplyMark(killer, freshCountdown: true);

        Log.Message(
            "[CryoRegenesis] HuntedAssassin mark inherited: "
            + (victim.Name?.ToStringShort ?? victim.LabelShort)
            + " → "
            + (killer.Name?.ToStringShort ?? killer.LabelShort)
            + " (fresh " + DefaultMarkTicks + " ticks).");

        string victimName = victim.Name?.ToStringShort ?? victim.LabelShort;
        string killerName = killer.Name?.ToStringShort ?? killer.LabelShort;
        Find.LetterStack.ReceiveLetter(
            "Keep What You Kill",
            killerName + " has slain the Marked One (" + victimName + ").\n\n"
            + "The Hunted Assassin subspace trackers leapt to their new host — "
            + "as with every such mark, you keep what you kill.\n\n"
            + "A fresh sixty-day hunt begins. Like Rome in crisis: each new claimant "
            + "does not inherit the old countdown — they start their own race for the throne.",
            LetterDefOf.ThreatSmall,
            new LookTargets(killer));

        RoyaltyRegenesisQuestSystem.CurrentSystem?.NotifyHuntedAssassinInherited(victim, killer);
        return true;
    }
}
