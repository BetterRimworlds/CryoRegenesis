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
using System.Text;
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
    public long desiredAgeTicks = 0;

    /// True while this pawn is under an active Royalty Regenesis return contract.
    /// Contract clients must not become voluntarily recruitable.
    public bool underRegenContract = false;

    /// Biological age (ticks) when the current regen contract began.
    /// Used to compute regression progress toward <see cref="desiredAgeTicks"/>.
    public long contractStartAgeTicks = 0;

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref this.aliveYearsInitialized, "aliveYearsInitialized", false);
        Scribe_Values.Look(ref this.consciousAliveTicks, "consciousAliveTicks", 0L);
        Scribe_Values.Look(ref this.cryoRegenesisRemovedAgeTicks, "cryoRegenesisRemovedAgeTicks", 0L);
        Scribe_Values.Look(ref this.desiredAgeTicks, "desiredAgeTicks", 0L);
        Scribe_Values.Look(ref this.underRegenContract, "underRegenContract", false);
        Scribe_Values.Look(ref this.contractStartAgeTicks, "contractStartAgeTicks", 0L);
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

    private bool UnderActiveContract =>
        this.underRegenContract && this.desiredAgeTicks > 0 && this.pawn?.ageTracker != null;

    public float GetContractTargetAgeYears()
    {
        return AgeTicksToYears(this.desiredAgeTicks);
    }

    /// 0–100% of the contracted regression completed (100 once at/below the target age).
    /// Falls back to 0 when no contract start age was recorded (pre-existing saves).
    public float GetContractProgressPercent()
    {
        if (!this.UnderActiveContract)
        {
            return 0f;
        }

        long currentTicks = this.pawn.ageTracker.AgeBiologicalTicks;
        if (currentTicks <= this.desiredAgeTicks)
        {
            return 100f;
        }

        if (this.contractStartAgeTicks <= this.desiredAgeTicks)
        {
            return 0f;
        }

        float progress = (this.contractStartAgeTicks - currentTicks)
            / (float)(this.contractStartAgeTicks - this.desiredAgeTicks) * 100f;
        return Math.Max(0f, Math.Min(100f, progress));
    }

    /// Only shown on the Health tab while under an active Regenesis contract, so the
    /// player can see the contracted target age and how close the pawn is to it.
    public override bool Visible => this.UnderActiveContract;

    public override string LabelBase => this.UnderActiveContract ? "Regenesis contract" : base.LabelBase;

    public override string LabelInBrackets
    {
        get
        {
            if (!this.UnderActiveContract)
            {
                return base.LabelInBrackets;
            }

            return "target age " + this.GetContractTargetAgeYears().ToString("0.#")
                + ", " + this.GetContractProgressPercent().ToString("0") + "% there";
        }
    }

    public override string TipStringExtra
    {
        get
        {
            string baseTip = base.TipStringExtra;
            if (!this.UnderActiveContract)
            {
                return baseTip;
            }

            StringBuilder sb = new StringBuilder();
            if (!baseTip.NullOrEmpty())
            {
                sb.AppendLine(baseTip.TrimEnd());
            }

            float currentYears = this.pawn.ageTracker.AgeBiologicalYearsFloat;
            float targetYears = this.GetContractTargetAgeYears();
            sb.AppendLine("Current biological age: " + currentYears.ToString("0.00"));
            sb.AppendLine("Contracted target age: " + targetYears.ToString("0.00"));
            sb.AppendLine("Regression progress: " + this.GetContractProgressPercent().ToString("0.0") + "%");
            sb.AppendLine(currentYears > targetYears
                ? "Years left to remove: " + (currentYears - targetYears).ToString("0.00")
                : "Target age reached — ready to board the shuttle home.");
            return sb.ToString().TrimEnd();
        }
    }
}
