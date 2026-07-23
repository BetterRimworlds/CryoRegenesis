// ==== Source/RegenesisCycle.cs ====
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
using Random = System.Random;
using UnityEngine;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

public class RegenesisCycle
{
    private const long NoRegenesisInProgress = -1000;

    private readonly Random rnd = new Random();
    private IList<Hediff> hediffsToHeal = new List<Hediff>();
    private bool enteredHealthy;
    private long restoreCoolDown = NoRegenesisInProgress;
    private int targetAge;
    private string ttlToHeal;
    private int origAge;

    public bool EnteredHealthy => this.enteredHealthy;
    public bool HasCurableInjuries => this.hediffsToHeal.Any();
    public int OriginalAge => this.origAge;
    public int TargetAge => this.targetAge;
    public string TtlToHeal => this.ttlToHeal;

    public void ExposeData()
    {
        Scribe_Values.Look(ref this.origAge, "OrigAge", 50);
    }

    public void InitializeForLoadedPawn(Pawn pawn)
    {
        this.ConfigureTargetAge(pawn);
        this.enteredHealthy = this.DetermineCurableInjuries(pawn) == 0;
    }

    public void BeginNewPawn(Pawn pawn)
    {
        this.origAge = Mathf.RoundToInt(pawn.ageTracker.AgeBiologicalYearsFloat);
        this.restoreCoolDown = NoRegenesisInProgress;
        this.ttlToHeal = null;
        this.InitializeForLoadedPawn(pawn);
    }

    public void MarkTargetAgeReached(Pawn pawn)
    {
        this.restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks;
    }

    public bool IsTargetAge(Pawn pawn, int rate)
    {
        return pawn.ageTracker.AgeBiologicalTicks <= ((GenDate.TicksPerYear * this.targetAge) + rate);
    }

    public void UpdateHealingEta(Pawn pawn, int rate)
    {
        if (!this.HasCurableInjuries || this.IsTargetAge(pawn, rate) || this.restoreCoolDown == NoRegenesisInProgress)
        {
            return;
        }

        long ticksLeft = pawn.ageTracker.AgeBiologicalTicks - this.restoreCoolDown;
        double repairAge = (double)this.restoreCoolDown / GenDate.TicksPerYear;
        float totalDays = (float)ticksLeft / GenDate.TicksPerDay;
        ticksLeft.TicksToPeriod(out int years, out int quadrums, out int days, out float hours);

        string timeToWait = "";
        timeToWait += TranslatorFormattedStringExtensions.Translate(years == 1 ? "Period1Year" : "PeriodYears", (NamedArgument)years);
        timeToWait += ", " + TranslatorFormattedStringExtensions.Translate(quadrums == 1 ? "Period1Quadrum" : "PeriodQuadrums", (NamedArgument)quadrums);
        timeToWait += " (" + TranslatorFormattedStringExtensions.Translate(days == 1 ? "Period1Day" : "PeriodDays", string.Format("{0:0.00}", totalDays)) + ")";
        this.ttlToHeal = timeToWait;

        if (pawn.ageTracker.AgeBiologicalTicks % GenDate.TicksPerSeason <= rate && CryoRegenesis.Settings.debugMode)
        {
            Log.Message("(" + pawn.Name.ToStringShort + ") Time to Wait: " + timeToWait + " | Next repair at: " + repairAge);
        }
    }

    public bool IsOutOfFuelForHealing(Pawn pawn, CompRefuelable refuelable)
    {
        long ticksLeft = pawn.ageTracker.AgeBiologicalTicks - this.restoreCoolDown;
        return this.HasCurableInjuries && ticksLeft <= 0 && refuelable.FuelPercentOfMax < 0.10f;
    }

    public bool ShouldScheduleHealing(Pawn pawn)
    {
        long ticksLeft = pawn.ageTracker.AgeBiologicalTicks - this.restoreCoolDown;
        return this.HasCurableInjuries && (ticksLeft <= 0 || this.restoreCoolDown == NoRegenesisInProgress);
    }

    public void ScheduleNextHealing(Pawn pawn)
    {
        int ticksToWait = this.CalculateHealingTime(pawn);
        this.restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - ticksToWait;
        double repairAge = (double)this.restoreCoolDown / GenDate.TicksPerYear;

        if (CryoRegenesis.Settings.debugMode)
        {
            Log.Message("Current Age in Ticks: " + pawn.ageTracker.AgeBiologicalTicks + " vs. " + this.restoreCoolDown);
            Log.Message("(" + pawn.Name.ToStringShort + ") Years to Wait: " + ((double)ticksToWait / GenDate.TicksPerYear) + " | Next repair at: " + repairAge);
        }
    }

    public bool TryHealNextInjury(Pawn pawn, CompRefuelable refuelable)
    {
        if (!this.HasCurableInjuries || this.restoreCoolDown <= NoRegenesisInProgress)
        {
            return false;
        }

        long ticksLeft = pawn.ageTracker.AgeBiologicalTicks - this.restoreCoolDown;
        if (pawn.ageTracker.AgeBiologicalTicks > this.restoreCoolDown)
        {
            return false;
        }

        Hediff hediff = this.hediffsToHeal[0];
        string hediffName = hediff.def.label;
        BodyPartRecord inferiorProstheticPart = GetInferiorAddedPart(pawn, hediff.Part);
        if (inferiorProstheticPart != null && !CryoRegenesis.Settings.healSimpleProsthetics)
        {
            this.DetermineCurableInjuries(pawn);
            return false;
        }

        // Always remove only this hediff. RestorePart() would recursively clear the
        // whole limb tree (hand, fingers, etc.) in one tick. Inferior prosthetics
        // leave MissingBodyPart hediffs on their children when installed; those are
        // healed one-by-one on later cycles after the prosthetic is removed.
        refuelable.ConsumeFuel(Math.Max(refuelable.FuelPercentOfMax * 0.10f, 10));
        pawn.health.RemoveHediff(hediff);

        this.hediffsToHeal.RemoveAt(0);

        this.restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - GenDate.TicksPerYear;
        if (ticksLeft < 0)
        {
            this.restoreCoolDown += ticksLeft;
        }

        if (CryoRegenesis.Settings.debugMode)
        {
            Log.Message("Cured HEDIFF: " + hediffName + " @ " + hediff.def.description + " | " + hediff);
        }

        // Re-scan because removing one hediff can expose new curable injuries.
        this.DetermineCurableInjuries(pawn);
        return true;
    }

    private static BodyPartRecord GetInferiorAddedPart(Pawn pawn, BodyPartRecord bodyPart)
    {
        for (BodyPartRecord currentPart = bodyPart; currentPart != null; currentPart = currentPart.parent)
        {
            Hediff addedPart = pawn.health.hediffSet.hediffs.FirstOrDefault(
                h => h.Part == currentPart
                     && IsInferiorAddedPart(h));
            if (addedPart != null)
            {
                return currentPart;
            }
        }

        return null;
    }

    public bool ShouldEjectAfterHealing(Pawn pawn)
    {
        return CryoRegenesis.Settings.regenUntilHealed
            && !this.enteredHealthy
            && pawn.RaceProps.Humanlike
            && !this.HasCurableInjuries;
    }

    public int AgeHediffs()
    {
        int hediffCount = 0;
        foreach (Hediff injury in this.hediffsToHeal)
        {
            string injuryName = injury.def.label;
            if (injuryName == "cataract" || injuryName == "hearing loss")
            {
                hediffCount += 1;
            }
            else if (injuryName == "bad back" || injuryName == "frail" || injuryName == "dementia" || injuryName == "alzheimer's")
            {
                hediffCount += 1;
            }
        }

        return hediffCount;
    }

    public int InjuryHediffs()
    {
        return this.hediffsToHeal.Count - this.AgeHediffs();
    }

    public static bool HasBetterThanNaturalParent(Pawn pawn, BodyPartRecord bodyPart)
    {
        for (BodyPartRecord currentPart = bodyPart.parent; currentPart != null; currentPart = currentPart.parent)
        {
            if (pawn.health.hediffSet.hediffs.Any(
                    h => h.Part == currentPart
                         && h.def.countsAsAddedPartOrImplant
                         && h.def.addedPartProps?.betterThanNatural == true))
            {
                return true;
            }
        }

        return false;
    }

    private int DetermineCurableInjuries(Pawn pawn)
    {
        List<string> hediffsToIgnore = new List<string>()
        {
            "joywire",
            "painstopper",
            "luciferium",
            "penoxycyline",
            "cryptosleep sickness",
            "CryoRegenesis sedation",
        };
        this.hediffsToHeal = new List<Hediff>();

        #if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
        var hediffsOfPawn = new List<Hediff>();
        pawn.health.hediffSet.GetHediffs<Hediff>(ref hediffsOfPawn);
        foreach (Hediff hediff in hediffsOfPawn.ToList())
        #else
        foreach (Hediff hediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
        #endif
        {
            BodyPartRecord inferiorProstheticPart = GetInferiorAddedPart(pawn, hediff.Part);
            if (inferiorProstheticPart != null)
            {
                if (!CryoRegenesis.Settings.healSimpleProsthetics)
                {
                    continue;
                }

                // Only the prosthetic itself is eligible this scan. Child missing
                // parts under it stay until the prosthetic is gone; otherwise a
                // single heal would wipe or race the whole limb tree.
                if (!IsInferiorAddedPart(hediff) || hediff.Part != inferiorProstheticPart)
                {
                    continue;
                }
            }

            if (hediffsToIgnore.Contains(hediff.def.label))
            {
                continue;
            }

            if (hediff.def.label.Contains("high on "))
            {
                continue;
            }

            if (hediff.def.IsAddiction)
            {
                continue;
            }

            if (hediff.def.label.Contains("alcohol"))
            {
                continue;
            }

            if (!CryoRegenesis.Settings.healSimpleProsthetics && !hediff.def.tendable)
            {
                continue;
            }

            if (hediff.def.hediffClass == typeof(Hediff_Implant))
            {
                continue;
            }

            bool repairableProsthetic =
                CryoRegenesis.Settings.healSimpleProsthetics && IsInferiorAddedPart(hediff);
            if (hediff.def.addedPartProps?.betterThanNatural == true)
            {
                continue;
            }

            if (hediff.def.label == "missing body part" && HasBetterThanNaturalParent(pawn, hediff.Part))
            {
                continue;
            }

            // Skip non-bad hediffs like the GateTraveler implant.
            if (!hediff.def.isBad && !repairableProsthetic)
            {
                continue;
            }

            this.hediffsToHeal.Add(hediff);
            if (CryoRegenesis.Settings.debugMode)
            {
                Log.Message(hediff.def.description + " ( " + hediff.def.hediffClass + ") = " + hediff.GetType().Name);
            }
        }

        return this.hediffsToHeal.Count;
    }

    private static bool IsInferiorAddedPart(Hediff hediff)
    {
        return hediff is Hediff_AddedPart
               && hediff.def.countsAsAddedPartOrImplant
               && hediff.def.addedPartProps != null
               && !hediff.def.addedPartProps.betterThanNatural;
    }

    private int CalculateHealingTime(Pawn pawn)
    {
        int pawnAge = (int)(pawn.ageTracker.AgeBiologicalTicks / GenDate.TicksPerYear);
        if (pawnAge <= (int)Math.Floor(pawn.RaceProps.lifeExpectancy * 0.25))
        {
            return GenDate.TicksPerYear / this.rnd.Next(1, 4);
        }

        if (pawnAge < 100)
        {
            int decadeOfLife = (pawnAge / 10) + 1;
            int baseFrequency = (11 - decadeOfLife) * 10;
            int minFrequency = (int)((double)baseFrequency * baseFrequency / 100);
            int maxFrequency = (int)((double)baseFrequency * (((double)baseFrequency + 100) / 100));

            double frequency = this.rnd.Next(minFrequency, maxFrequency);
            if (CryoRegenesis.Settings.debugMode)
            {
                Log.Message("Healing Frequency: Base (" + baseFrequency + ") Min (" + minFrequency + ") Max (" + maxFrequency + ") Actual: " + frequency + "%");
            }

            return (int)Math.Round((frequency / 100) * (GenDate.TicksPerYear * decadeOfLife));
        }

        return GenDate.TicksPerYear * (8 + this.rnd.Next(0, 4));
    }

    private void ConfigureTargetAge(Pawn pawn)
    {
        if (CryoRegenesis.Settings.debugMode)
        {
            Log.Message("Pawn name: " + pawn.def.defName);
            Log.Message("Race label: " + pawn.def.label); // human-readable race name
            Log.Message("Humanlike: " + pawn.RaceProps.Humanlike);
            Log.Message("Life expectancy: " + pawn.RaceProps.lifeExpectancy);
            #if !RIMWORLD12 && !RIMWORLD13
            Log.Message("Xenotype: " + (pawn.genes?.Xenotype?.defName ?? "none"));
            #endif
            Log.Message("Kind: " + pawn.kindDef?.defName); // e.g. Empire_Fighter, Refugee
            Log.Message("Faction: " + (pawn.Faction?.Name ?? "none"));
        }

        // All humanlikes — baseline humans, xenotypes, and modded humanlike
        // races alike — honor the user's configured target age. Only true
        // animals fall back to a fraction of their species lifespan.
        //
        // Previously this gated on `pawn.def.defName == "Human"`, which
        // silently routed every non-"Human" humanlike (Empire pawns, modded
        // races, some xenotype defs) into lifespan-based targeting. For a
        // long-lived race, 0.25 * lifeExpectancy could exceed the pawn's
        // current age, making IsTargetAge() true on the first tick and
        // ejecting them instantly with no regression.
        if (pawn.RaceProps.Humanlike)
        {
            this.targetAge = CryoRegenesis.Settings.targetAge;
        }
        else
        {
            this.targetAge = (int)Math.Floor(pawn.RaceProps.lifeExpectancy * 0.25);
        }

        if (CryoRegenesis.Settings.debugMode)
        {
            Log.Message("Pawn name: " + pawn.def.defName);
            Log.Message("Life expectancy: " + pawn.RaceProps.lifeExpectancy);
            Log.Message("Target age: " + this.targetAge);
        }
    }
}
