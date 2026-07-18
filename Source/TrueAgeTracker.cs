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

namespace BetterRimworlds.CryoRegenesis;

[DefOf]
public static class TrueAgeDefOf
{
    public static HediffDef TrueAgeTracker;

    static TrueAgeDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(TrueAgeDefOf));
    }
}

/// Invisible hediff that tracks a pawn's True Age independently of
/// vanilla biological/chronological age. Added when a pawn first uses
/// the CryoRegenesis Casket.
///
/// For pawns who have never used CryoRegenesis, True Age = Bio Age.
///
/// Ledger:
///   consciousAliveTicks           – time actually lived while awake
///   cryoRegenesisRemovedAgeTicks  – biological age removed by CryoRegenesis
///
/// True Age = consciousAliveTicks (converted to years)
///
/// No suspension tracking is needed:
///   - In cryptosleep: pawn is despawned, Tick() never fires
///   - In Stargate buffer: pawn is in a timeless state
///   - In both cases, consciousAliveTicks simply stops incrementing
public class TrueAgeTracker : HediffWithComps
{
    public bool aliveYearsInitialized = false;
    public long consciousAliveTicks = 0;
    public long cryoRegenesisRemovedAgeTicks = 0;

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref this.aliveYearsInitialized, "aliveYearsInitialized", false);
        Scribe_Values.Look(ref this.consciousAliveTicks, "consciousAliveTicks", 0L);
        Scribe_Values.Look(ref this.cryoRegenesisRemovedAgeTicks, "cryoRegenesisRemovedAgeTicks", 0L);
    }

    public override void Tick()
    {
        base.Tick();

        if (!this.aliveYearsInitialized)
        {
            return;
        }

        if (this.pawn != null && this.pawn.Dead)
        {
            return;
        }

        this.consciousAliveTicks++;
    }

    /// Initialize the ledger with the pawn's pre-CryoRegenesis biological age.
    /// Called once, when the hediff is first added.
    ///
    /// Before this moment, True Age = Bio Age, so the pawn's current
    /// biological age IS their lived time.
    public void Initialize(long preRegenesisBioTicks)
    {
        if (this.aliveYearsInitialized)
        {
            return;
        }

        this.consciousAliveTicks = preRegenesisBioTicks;
        this.aliveYearsInitialized = true;
    }

    /// Record that CryoRegenesis has removed biological age.
    /// This does NOT reduce consciousAliveTicks — the pawn still
    /// lived through that time.
    public void RecordCryoRegenesisDeAging(long removedTicks)
    {
        if (removedTicks <= 0)
        {
            return;
        }

        this.cryoRegenesisRemovedAgeTicks += removedTicks;
    }

    public long GetTrueAgeTicks()
    {
        return this.consciousAliveTicks;
    }

    public float GetTrueAgeYears()
    {
        return AgeTicksToYears(this.consciousAliveTicks);
    }

    public float GetCryoRegenesisRemovedAgeYears()
    {
        return AgeTicksToYears(this.cryoRegenesisRemovedAgeTicks);
    }

    private static float AgeTicksToYears(long ticks)
    {
        return (float)ticks / 3600000f;
    }

    public override bool Visible => false;
}
