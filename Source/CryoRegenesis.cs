// ==== ./Source/CryoRegenesis.cs ====
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
using System.Reflection;
using HarmonyLib;
using Random=System.Random;
using UnityEngine;
using Verse;
// ReSharper disable All

namespace BetterRimworlds.CryoRegenesis;

public class CryoRegenesis: Mod
{
    public static Settings Settings;

    public CryoRegenesis(ModContentPack content) : base(content)
    {
        Settings = GetSettings<Settings>() ?? new Settings();

        var harmony = new Harmony("FrontierDevelopments.DeadCryptosleep");
        try
        {
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load harmony patches: {e.Message}\n{e.StackTrace}");
        }
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        base.DoSettingsWindowContents(inRect);
        Settings.DoSettingsWindowContents(inRect);
    }

    public override string SettingsCategory()
    {
        return "CryoRegenesis";
    }
}

public partial class Building_CryoRegenesis : Building_CryptosleepCasket, IThingHolder
{
    private Random rnd = new Random();
    private readonly Cosmetics cosmetics = new Cosmetics();
    private readonly Resurrector resurrector = new Resurrector();

    private bool enteredHealthy = false;

    //bool isSafeToRepair = true;
    long restoreCoolDown = -1000;
    int targetAge; // 21 for humans. 25% of life expectancy for every other lifeform.
    //int rate = 30;
    //int rate = 150;
    int rate = 500;
    // int rate = 1500;
    float fuelConsumption;
    HediffDef cryosickness = HediffDef.Named("CryptosleepSickness");
    CompRefuelable refuelable;
    CompPowerTrader power;
    CompProperties_Power props;
    CompProperties_Refuelable fuelprops;

    private IList<Hediff> hediffsToHeal = new List<Hediff>();

    protected Map currentMap;

    protected string TTLToHeal;

    private int origAge;

    // @see https://github.com/goudaQuiche/BloodAndStains/blob/c8fdf1a312186eb17505c9b2f3e6e5cd3c408e7c/Source/BloodDripping/ToolsHediff.cs
    public static bool HasBionicParent(Pawn pawn, BodyPartRecord BPR)
    {
        List<BodyPartRecord> allParents = new List<BodyPartRecord>();

        if (BPR.IsCorePart)
            return false;

        BodyPartRecord recursiveBPR = BPR.parent;

        while (!recursiveBPR.IsCorePart)
        {
            if (!recursiveBPR.IsCorePart)
                allParents.Add(recursiveBPR);

            recursiveBPR = recursiveBPR.parent;
        }

        if (allParents.NullOrEmpty())
            return false;

        //Log.Warning("Found " + allParents.Count + " parent bpr");

        foreach(BodyPartRecord curP in allParents)
        {
            IEnumerable<Hediff> hList = pawn.health.hediffSet.hediffs.Where(
                h => h.Part == curP
                     && h.def.countsAsAddedPartOrImplant
                     && (h.def.label.Contains("bionic") || h.def.label.Contains("archotech"))
            );
            if (!hList.EnumerableNullOrEmpty())
                return true;
        }

        return false;
    }

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        this.currentMap = map;

        refuelable = GetComp<CompRefuelable>();
        power = GetComp<CompPowerTrader>();
        props = power.Props;
        fuelprops = refuelable.Props;

        // Require more fuel for faster rates.
        float fuelPerReversedYear = 1.0f * ((float)rate / 250);
        fuelConsumption = fuelPerReversedYear / ((float)GenDate.TicksPerYear / rate);
        // Log.Message("Fuel consumption per Tick: " + fuelConsumption);

        resurrector.Initialize(refuelable, fuelprops, power,
            onRejected:    () => base.EjectContents(),
            onResurrected: HandleResurrectionComplete);

        if (HasAnyContents)
        {
            if (ContainedThing is Pawn pawn)
            {
                this.configTargetAge(pawn);
                this.enteredHealthy = this.determineCurableInjuries(pawn) == 0;
            }
            else if (ContainedThing is Corpse corpse)
            {
                resurrector.InitialRot = corpse.GetComp<CompRottable>().RotProgress;
            }
        }

        this.contentsKnown = true;
    }

    private void HandleResurrectionComplete(Pawn pawn)
    {
        power.PowerOn = false;
        power.PowerOutput = 0;
        this.EjectContents();
        pawn.health.AddHediff(HediffDefOf.Anesthetic, null, null);
    }

    private int determineCurableInjuries(Pawn pawn)
    {
        List<string> hediffsToIgnore = new List<string>()
        {
            "joywire",
            "painstopper",
            "luciferium",
            "penoxycyline",
            "cryptosleep sickness",
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
            // Ignore joywires, luciferium and more!
            if (hediffsToIgnore.Contains(hediff.def.label)) {
                continue;
            }

            // Ignore all highs.
            if (hediff.def.label.Contains("high on ")) {
                continue;
            }

            //// Ignore all tolerances.
            //if (hediff.def.label.Contains(" tolerance"))
            //{
            //    continue;
            //}

            // Ignore addictions.
            if (hediff.def.IsAddiction) {
                continue;
            }

            // Ignore everything alcohol related.
            if (hediff.def.label.Contains("alcohol")) {
                continue;
            }

            // Don't heal anything not marked as "bad", if they have the setting enabled.
            if (CryoRegenesis.Settings.healSimpleProsthetics == false && hediff.def.tendable == false)
            {
                continue;
            }

            // Ignore all implants.
            if (hediff.def.hediffClass == typeof(Hediff_Implant))
            {
                continue;
            }

            // Ignore bionic body parts.
            if (hediff.def.label.Contains("bionic") || hediff.def.label.Contains("archotech"))
            {
                continue;
            }

            // Ignore surgically-removed parts (bionics / arcotech)
            if (hediff.def.label == "missing body part")
            {
                // But only if they have a bionic or archotech part...
                if (HasBionicParent(pawn, hediff.Part))
                {
                    continue;
                }
            }

            // Ignore a hediff not marked as bad...
            if (CryoRegenesis.Settings.healNotBad == false && hediff.def.isBad == false)
            {
                continue;
            }

            this.hediffsToHeal.Add(hediff);
            if (CryoRegenesis.Settings.debugMode)
                Log.Message(hediff.def.description + " ( " + hediff.def.hediffClass + ") = " + hediff.GetType().Name);
        }

        return this.hediffsToHeal.Count;
    }

    public int AgeHediffs(Pawn pawn)
    {
        if (pawn != null)
        {
            int hediffs = 0;
            foreach (Hediff injury in this.hediffsToHeal)
            {
                string injuryName = injury.def.label;
                if (injuryName == "cataract")
                {
                    hediffs += 1;
                }
                else if (injuryName == "hearing loss")
                {
                    hediffs += 1;
                }
                else if (injuryName == "bad back" || injuryName == "frail" || injuryName == "dementia" || injuryName == "alzheimer's")
                    hediffs += 1;
            }
            return hediffs;
        }
        return 0;
    }

    protected int InjuryHediffs(Pawn pawn)
    {
        if (pawn != null)
        {
            int OldAgeHediffs = this.AgeHediffs(pawn);

            return this.hediffsToHeal.Count() - OldAgeHediffs;
        }

        return 0;
    }

    public override void ExposeData()
    {
        base.ExposeData();
        string fuelReqs = String.Join(",", resurrector.ResurrectionFuelReqs);
        Scribe_Values.Look<string>(ref fuelReqs, "ResurrectionFuelReqs");
        float resProgress = resurrector.ResurrectionProgress;
        Scribe_Values.Look<float>(ref resProgress, "ResurrectionProgress", 0f);
        resurrector.ResurrectionProgress = resProgress;
        Scribe_Values.Look(ref origAge, "OrigAge", 50);
        this.ExposeTrueAgeData();

        if (String.IsNullOrEmpty(fuelReqs) == false)
        {
            resurrector.ResurrectionFuelReqs = fuelReqs.Split(',').Select(int.Parse).ToArray();
        }
    }

    private int CalculateHealingTime(Pawn pawn)
    {
        // Get the pawn's age in Years. e.g., 65 years.
        int pawnAge = (int) (pawn.ageTracker.AgeBiologicalTicks / GenDate.TicksPerYear);

        // If the pawn is 25% of its max age or younger, set it for a year or less.
        if (pawnAge <= (int)Math.Floor(pawn.RaceProps.lifeExpectancy * 0.25)) {
            return GenDate.TicksPerYear / rnd.Next(1, 4);
        }
        else if (pawnAge < 100)
        {
            // Get the decade. e.g., 7th decade
            int decadeOfLife = (pawnAge / 10) + 1;

            // 10  =   ?? - 10    = 10
            //  9  =   11 -  9      20
            //  8  =   11 -  8      30
            //  7  =   11 -  7      40
            //  6  =   11 -  6      50
            //  5  =   11 -  5      60
            //  4  =   11 -  4      70
            //  3  =   11 -  3      80
            //  2  =   11 -  2      90
            //  1  =   11 -  1   = 100

            int baseFrequency = (11 - decadeOfLife) * 10;
            // E.g., if decade = 8, base = 30, min = 30 * (30/100) = 9
            // E.g., if decade = 4, base = 70, min = 70 * (70/100) = 49
            int minFrequency = (int)((double)baseFrequency * (double)baseFrequency / 100);
            // E.g., if decade = 8, base = 30, max = 30 * ((30+100) / 100) = 39
            // E.g., if decade = 4, base = 70, max = 70 * ((70+100) / 100) = 119
            int maxFrequency = (int)((double)baseFrequency * (((double)baseFrequency + 100) / 100));

            double frequency = rnd.Next(minFrequency, maxFrequency);

            if (CryoRegenesis.Settings.debugMode) Log.Message("Healing Frequency: Base (" + baseFrequency + ") Min (" + minFrequency + ") Max (" + maxFrequency + ") Actual: " + frequency + "%");

            return (int)Math.Round((frequency / 100) * (GenDate.TicksPerYear * decadeOfLife));
        }
        else
        {
            // For immortals and other long-living creatures, like Thrumbos, it's 8-12 years.
            return GenDate.TicksPerYear * (8 + rnd.Next(0, 4));
        }
    }

    #if RIMWORLD16
    protected override void Tick()
    #else
    public override void Tick()
    #endif
    {
        base.Tick();

        bool hasInjuries;
        bool isTargetAge;

        if (!HasAnyContents)
        {
            resurrector.ResetFuelRequirement();
            return;
        }

        // Handle corpse resurrection - check every tick for smoother progress updates
        if (this.ContainedThing.def.defName.StartsWith("Corpse_"))
        {
            if (Find.TickManager.TicksGame % 180 == 0)
            {
                // Turn off / pause rotting once inside.
                var corpse = ContainedThing as Corpse;
                var rottable = corpse.GetComp<CompRottable>();
                rottable.RotProgress = resurrector.InitialRot;
                resurrector.Process(corpse);
            }

            return;
        }

        if (refuelable.HasFuel)
        {
            Pawn pawn = ContainedThing as Pawn;
            float pawnAge = pawn.ageTracker.AgeBiologicalTicks / GenDate.TicksPerYear;

            isTargetAge = pawn.ageTracker.AgeBiologicalTicks <= ((GenDate.TicksPerYear * this.targetAge) + rate);
            hasInjuries = this.hediffsToHeal != null && this.hediffsToHeal.Any();

            // if (this.isSafeToRepair == false)
            // {
            //     this.EjectContents();
            //     this.props.basePowerConsumption = 0;
            //     power.PowerOutput = 0;
            //
            //     return;
            // }

            if (power.PowerOn)
            {
                long ticksLeft = (pawn.ageTracker.AgeBiologicalTicks - restoreCoolDown);
                double repairAge = (double)restoreCoolDown / (double)GenDate.TicksPerYear;

                if (isTargetAge && !hasInjuries)
                {
                    this.EjectContents();
                    // this.props.basePowerConsumption = 0;
                    power.PowerOn = false;
                    power.PowerOutput = 0;

                    return;
                }

                if (power.PowerOn && hasInjuries && !isTargetAge /*&& pawn.ageTracker.AgeBiologicalTicks % GenDate.TicksPerSeason <= rate*/)
                {
                    //float timeLeft = ((float) ticksLeft / (float) GenDate.TicksPerYear);
                    float totalDays = (float) ticksLeft / (float) GenDate.TicksPerDay;
                    ticksLeft.TicksToPeriod(out int years, out int quadrums, out int days, out float hours);
                    string timeToWait = "";
                    timeToWait += TranslatorFormattedStringExtensions.Translate(years == 1 ? "Period1Year" : "PeriodYears", (NamedArgument) years);
                    timeToWait += ", " + TranslatorFormattedStringExtensions.Translate(quadrums == 1 ? "Period1Quadrum" : "PeriodQuadrums", (NamedArgument) quadrums);
                    timeToWait += " (" + TranslatorFormattedStringExtensions.Translate(days == 1 ? "Period1Day" : "PeriodDays", string.Format("{0:0.00}", totalDays)) + ")";

                    this.TTLToHeal = timeToWait;
                    if (pawn.ageTracker.AgeBiologicalTicks % GenDate.TicksPerSeason <= rate)
                    {
                        if (CryoRegenesis.Settings.debugMode) Log.Message("(" + pawn.Name.ToStringShort + ") Time to Wait: " + timeToWait + " | Next repair at: " + repairAge);
                    }
                }

                if (hasInjuries && ticksLeft <= 0 && refuelable.FuelPercentOfMax < 0.10f)
                {
                    Log.Message("Not enough Uranium to heal.");
                }

                if (isTargetAge)
                {
                    restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks;
                }

                // Remove all health-related injuries if they're younger than the repairAge.
                if (hasInjuries && restoreCoolDown > -1000 && pawn.ageTracker.AgeBiologicalTicks <= restoreCoolDown)
                {
                    string hediffName;
                    foreach (Hediff hediff in this.hediffsToHeal)
                    {
                        hediffName = hediff.def.label;

                        refuelable.ConsumeFuel(Math.Max(refuelable.FuelPercentOfMax * 0.10f, 10));

                        pawn.health.RemoveHediff(hediff);
                        this.hediffsToHeal.RemoveAt(0);

                        restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - GenDate.TicksPerYear;
                        if (ticksLeft < 0)
                        {
                            restoreCoolDown += ticksLeft;
                        }
                        //restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - GenDate.TicksPerSeason;
                        if (CryoRegenesis.Settings.debugMode) Log.Message("Cured HEDIFF: " + hediffName + " @ " + hediff.def.description + " | " + hediff.ToString());

                        // Look for new injuries caused by the healing. E.g., removing a prostetic leg will lead to numerous new
                        // injuries in the feet.
                        this.determineCurableInjuries(pawn);
                        break;
                    }
                }

                if (hasInjuries && (ticksLeft <= 0 || restoreCoolDown == -1000))
                {
                    int ticksToWait = this.CalculateHealingTime(pawn);

                    restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - ticksToWait;
                    repairAge = (double)restoreCoolDown / (double)GenDate.TicksPerYear;
                    if (CryoRegenesis.Settings.debugMode) Log.Message("Current Age in Ticks: " + pawn.ageTracker.AgeBiologicalTicks + " vs. " + restoreCoolDown);
                    if (CryoRegenesis.Settings.debugMode) Log.Message("(" + pawn.Name.ToStringShort + ") Years to Wait: " + ((double)ticksToWait / (double)GenDate.TicksPerYear) + " | Next repair at: " + repairAge);
                }
            }

            if (CryoRegenesis.Settings.regenUntilHealed == true && this.enteredHealthy == false && pawn.RaceProps.Humanlike && !this.hediffsToHeal.Any())
            {
                Log.Warning("No more injuries; ejecting.");
                this.EjectContents();
            }

            if (pawn.ageTracker.AgeBiologicalTicks > GenDate.TicksPerYear * targetAge)
            {
                #if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
                power.PowerOutput = -props.PowerConsumption;
                #else
                power.PowerOutput = -props.basePowerConsumption;
                #endif

                if (power.PowerOn)
                {
                    if (pawn.ageTracker.AgeBiologicalTicks > GenDate.TicksPerYear * targetAge)
                    {
                        refuelable.ConsumeFuel(fuelConsumption * ((pawnAge - 10) * 0.1f));

                        pawn.ageTracker.AgeBiologicalTicks = Math.Max(pawn.ageTracker.AgeBiologicalTicks - rate, GenDate.TicksPerYear * targetAge);
                    }
                }
            }
        }
        else
            power.PowerOutput = 0;
    }

    public override void EjectContents()
    {
        if (ContainedThing is not Pawn)
        {
            power.PowerOutput = 0;
            base.EjectContents();

            return;
        }

        Pawn pawn = ContainedThing as Pawn;
        pawn.health.AddHediff(cryosickness);

        if ((pawn.IsPrisoner == true || pawn.IsColonist) && pawn.NonHumanlikeOrWildMan() == false)
        {
            // Remove negative and now-irrelevant thoughts:
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.MyOrganHarvested);
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.BotchedMySurgery);
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.SleptInCold);
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.SleptInHeat);
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.SleptOnGround);
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.SleptOutside);
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ThoughtDefOf.SleepDisturbed);
            pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.ArtifactMoodBoost);
            pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.Catharsis);

            if (pawn.IsPrisoner == false)
            {
                pawn.needs.joy.SetInitialLevel();
                pawn.needs.comfort.SetInitialLevel();
            }

            cosmetics.PossiblyChangeHairColor(pawn);

            // Give them a positive thought.
            int currentAge = Mathf.RoundToInt(pawn.ageTracker.AgeBiologicalYearsFloat);

            RegenesisThoughts.AddBodyPositivityThought(pawn, this.origAge, currentAge);
        }

        pawn.needs.rest.SetInitialLevel();
        pawn.needs.food.SetInitialLevel();

        power.PowerOutput = 0;
        this.ApplyTrueAgeOnEject(pawn);
        base.EjectContents();

        this.ResetTrueAgeTracking();

        // if (pawn.IsColonist == false)
        // {
        //     // Remove the guest from the Forcefully Kept WorldPawns.
        //     RimWorld.Planet.WorldPawns worldPawns = Find.WorldPawns;
        //     worldPawns.ForcefullyKeptPawns.Remove(pawn);
        // }
    }

    public override bool TryAcceptThing(Thing thing, bool allowSpecialEffects = true)
    {
        if (thing == null)
        {
            return false;
        }

        if (thing.IsDessicated())
        {
            return false;
        }

        resurrector.InitialRot = -1f;

        if (thing.def.defName.StartsWith("Corpse_"))
        {
            resurrector.ResetForNewCorpse();

            bool status = base.TryAcceptThing(thing, allowSpecialEffects);
            if (status)
            {
                // Record the initial rot level.
                var corpse = ContainedThing as Corpse;
                var rottable = corpse.GetComp<CompRottable>();
                resurrector.InitialRot = rottable.RotProgress;
            }

            return status;
        }

        var pawn = thing as Pawn;

        if (base.TryAcceptThing(thing, allowSpecialEffects))
        {
            this.origAge = Mathf.RoundToInt(pawn.ageTracker.AgeBiologicalYearsFloat);
            this.RecordTrueAgeSnapshot(pawn);

            restoreCoolDown = -1000;

            #if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
            power.PowerOutput = -props.PowerConsumption;
            #else
            power.PowerOutput = -props.basePowerConsumption;
            #endif

            // foreach (Hediff hediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
            // {
            //     // if (hediff.def.hediffClass.ToString() == "Verse.Hediff_AddedPart")
            //     // {
            //     //     Messages.Message("Won't repair: " + pawn.Name.ToStringShort + " has an added part: " + hediff.def.label, MessageTypeDefOf.RejectInput);
            //     //     isSafeToRepair = false;
            //     //
            //     //     return false;
            //     // }
            //     if (hediff.def.hediffClass.ToString() == "Verse.Hediff_Pregnant")
            //     {
            //         Messages.Message("Won't repair: Pregnant", MessageTypeDefOf.RejectInput);
            //         isSafeToRepair = false;
            //
            //         return false;
            //     }
            // }

            this.configTargetAge(pawn);
            this.enteredHealthy = this.determineCurableInjuries(pawn) == 0;

            return true;
        }

        return false;
    }

    public override string GetInspectString()
    {
        if (HasAnyContents)
        {
            if (ContainedThing.def.defName.StartsWith("Corpse_"))
            {
                string debugString = "";
                if (CryoRegenesis.Settings.debugMode)
                {
                    var corpse = ContainedThing as Corpse;
                    var rotProgress = corpse.GetComp<CompRottable>().RotProgress;
                    debugString = $"; Rotting: {rotProgress}";
                }

                string status = $"Dead (Resurrecting: {resurrector.ResurrectionProgress * 100:F1}%{debugString})\n";
                if (resurrector.ResurrectionFuelReqs[2] > 0)
                {
                    status += "Needed Luciferium: " + resurrector.ResurrectionFuelReqs[2] + "\n";
                }
                else if (resurrector.ResurrectionFuelReqs[0] > 0)
                {
                    status += "Needed Uranium: " + resurrector.ResurrectionFuelReqs[0] + "\n";
                }
                else if (resurrector.ResurrectionFuelReqs[1] > 0)
                {
                    status += "Needed Gold: " + resurrector.ResurrectionFuelReqs[1] + "\n";
                }

                return status + base.GetInspectString();
            }

            Pawn pawn = ContainedThing as Pawn;
            pawn.ageTracker.AgeBiologicalTicks.TicksToPeriod(out int years, out int quadrums, out int days, out float hours);
            //string bioTime = "AgeBiological".Translate(new object[]{years,quadrums,days});
            string bioTime = "AgeBiological".Translate((NamedArgument) years,
                (NamedArgument) quadrums, (NamedArgument) days);

            if (this.hediffsToHeal.Any())
            {
                return base.GetInspectString() + ", " + AgeHediffs(pawn).ToString() + " Age Disabilities, " + InjuryHediffs(pawn).ToString() + " Injuries\n" + bioTime + "\nTime To Heal: " + this.TTLToHeal;
            }
            else
            {
                return base.GetInspectString() + ", " + AgeHediffs(pawn).ToString() + " Age Disabilities, " + InjuryHediffs(pawn).ToString() + " Injuries\n" + bioTime;
            }
        }
        else return base.GetInspectString();
    }

    private int configTargetAge(Pawn pawn)
    {
        // Determine the pawn's target age based on their species' life expectancy.
        // 21 for humans. 25% of life expectancy for everything else.
        if (pawn.def.defName == "Human")
        {
            this.targetAge = CryoRegenesis.Settings.targetAge;
        }
        else
        {
            this.targetAge = (int)Math.Floor(pawn.RaceProps.lifeExpectancy * 0.25);
        }
        Log.Message("Pawn name: " + pawn.def.defName);
        Log.Message("Life expectancy: " + pawn.RaceProps.lifeExpectancy);
        Log.Message("Target age: " + this.targetAge);

        return this.targetAge;
    }
}
