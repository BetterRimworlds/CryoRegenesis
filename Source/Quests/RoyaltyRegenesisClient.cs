/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

public class RoyaltyRegenesisClient : IExposable
{
    public Pawn pawn;
    public long desiredAgeTicks;
    public string role;
    public bool triggerRoyalAscent;
    public int returnByTick;
    public bool isPrisoner;
    public Faction sourceFaction;

    /// Total biological age removed by CryoRegenesis when this contract began.
    /// Lets completion distinguish actual treatment from time merely passing.
    public long contractStartRemovedAgeTicks = -1L;

    /// Records the first time this client reaches the contracted age (the casket
    /// notifies the exact tick; the periodic check forgives natural aging afterwards).
    /// Survives subsequent natural aging so pickup success is not lost.
    public bool everReachedDesiredAge;

    public void ExposeData()
    {
        Scribe_References.Look(ref this.pawn, "pawn");
        Scribe_Values.Look(ref this.desiredAgeTicks, "desiredAgeTicks", 0L);
        Scribe_Values.Look(ref this.role, "role");
        Scribe_Values.Look(ref this.triggerRoyalAscent, "triggerRoyalAscent", false);
        Scribe_Values.Look(ref this.returnByTick, "returnByTick", 0);
        Scribe_Values.Look(ref this.isPrisoner, "isPrisoner", false);
        Scribe_References.Look(ref this.sourceFaction, "sourceFaction");
        Scribe_Values.Look(ref this.contractStartRemovedAgeTicks, "contractStartRemovedAgeTicks", -1L);
        Scribe_Values.Look(ref this.everReachedDesiredAge, "everReachedDesiredAge", false);
    }
}
