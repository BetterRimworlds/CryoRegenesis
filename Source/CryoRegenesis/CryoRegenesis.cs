using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Random=System.Random;
using UnityEngine;
using Verse;
// ReSharper disable All

namespace BetterRimworlds.CryoRegenesis
{
    public class CryoRegenesis: Mod
    {
        public static Settings Settings;

        public CryoRegenesis(ModContentPack content) : base(content)
        {
            Settings = GetSettings<Settings>() ?? new Settings();
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

    public class Building_CryoRegenesis : Building_CryptosleepCasket, IThingHolder
    {
        private Random rnd = new Random();

        private bool enteredHealthy = false;

        bool isSafeToRepair = true;
        long restoreCoolDown = -1000;
        int enterTime;
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

            fuelConsumption =  fuelPerReversedYear / ((float)GenDate.TicksPerYear / rate);
            // Log.Message("Fuel consumption per Tick: " + fuelConsumption);

            if (HasAnyContents)
            {
                Pawn pawn = ContainedThing as Pawn;
                this.configTargetAge(pawn);
                this.enteredHealthy = this.determineCurableInjuries(pawn) == 0;
            }

            this.contentsKnown = true;
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

            #if RIMWORLD14
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

                this.hediffsToHeal.Add(hediff);
                if (CryoRegenesis.Settings.debugMode) Log.Message(hediff.def.description + " ( " + hediff.def.hediffClass + ") = " + hediff.def.causesNeed + ", " + hediff.GetType().Name);
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
            Scribe_Values.Look<int>(ref enterTime, "enterTime");
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

        public override void Tick()
        {
            bool hasInjuries;
            bool isTargetAge;

            if (HasAnyContents && refuelable.HasFuel)
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
                    #if RIMWORLD14
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
            Pawn pawn = ContainedThing as Pawn;
            pawn.health.AddHediff(cryosickness);

            if (pawn.def.defName == "Human")
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

                pawn.needs.joy.SetInitialLevel();
                pawn.needs.comfort.SetInitialLevel();

                this.possiblyChangeHairColor(pawn);
            }

            pawn.needs.rest.SetInitialLevel();
            pawn.needs.food.SetInitialLevel();

            power.PowerOutput = 0;
            base.EjectContents();
        }

        protected List<Color> getHairColors()
        {
            var BRIGHTRED   = new Color(237.00f / 256.0f, 41.00f / 256.0f, 57.00f / 256.0f);
            var DARKRED     = new Color(146.00f / 256.0f, 39.00f / 256.0f, 36.00f / 256.0f);
            var HAZEL       = new Color(132.61f / 256.0f, 83.20f / 256.0f, 47.10f / 256.0f);
            var BROWN       = new Color(64.00f / 256.0f, 51.20f / 256.0f, 38.40f / 256.0f);
            var DARKBROWN   = new Color(51.20f / 256.0f, 51.20f / 256.0f, 51.20f / 256.0f);
            var BLACK       = new Color(51.20f / 256.0f, 51.20f / 256.0f, 51.20f / 256.0f);
            var DARKBLACK   = new Color(20.20f / 256.0f, 20.20f / 256.0f, 20.20f / 256.0f);
            var BLONDE      = new Color(222.00f / 256.0f, 188.00f / 256.0f, 153.00f / 256.0f);
            var LIGHTBLONDE = new Color(250.00f / 256.0f, 240.00f / 256.0f, 190.00f / 256.0f);

            var colorList = new List<Color>()
            {
                BRIGHTRED, DARKRED, HAZEL, BLONDE, LIGHTBLONDE
            };

            return colorList;
        }

        protected void rerenderPawn(Pawn pawn)
        {
            // Tell the pawn's Drawer that the Person has had a hair-change makeover.
            // This code is from https://github.com/KiameV/rimworld-changedresser/blob/f0b8fcf9073cd1c232fcd26b0b083cb3137924a3/Source/UI/DresserUI.cs
            // Copyright (c) 2017 Travis Offtermatt
            // MIT License
            pawn.Drawer.renderer.graphics.ResolveAllGraphics();
            PortraitsCache.SetDirty(pawn);
        }

        protected void changeHairColor(Pawn pawn, Color hairColor)
        {
            #if RIMWORLD14
            pawn.story.HairColor = hairColor;
            #else
            pawn.story.hairColor = hairColor;
            #endif
            this.rerenderPawn(pawn);
        }

        protected bool changeHairColorRandomly(Pawn pawn)
        {
            if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn is {pawn.ageTracker.AgeChronologicalYears} chronological years old ({pawn.ageTracker.AgeChronologicalTicks} Ticks)");
            int seed = Convert.ToInt32(pawn.ageTracker.AgeChronologicalTicks > Int32.MaxValue - 1
                ? pawn.ageTracker.AgeChronologicalTicks % Int32.MaxValue
                : pawn.ageTracker.AgeChronologicalTicks);

            var rnd = new Random(seed);
            var colorList = this.getHairColors();

            this.changeHairColor(pawn, colorList[rnd.Next(colorList.Count)]);

            return true;
        }

        protected bool hasWhiteOrGrayHair(Pawn pawn)
        {
            string hsv;
            #if RIMWORLD14
            Color hairColor = pawn.story.HairColor;
            #else
            Color hairColor = pawn.story.hairColor;
            #endif
            Color.RGBToHSV(hairColor, out float H, out float S, out float V);
            S *= 100;
            V *= 100;
            hsv = string.Format("{0:0.00}°, {1:0.00}%, {2:0.00}%", H, S, V);

            if (CryoRegenesis.Settings.debugMode) Log.Message("Pawn's hair color: " + hairColor + " (" + hsv + " HSV)" + "; body type: " + pawn.story.bodyType);

            // if (H <= 5 && S <= 5 && V >= 40)
            // {
            //     Log.Message("Grey / White hair detected!");
            // }

            return (H < 5 && S <= 5 && V >= 40);
        }

        protected bool possiblyChangeHairColor(Pawn pawn)
        {
            // This only affects human-like pawns.
            if (pawn.RaceProps.Humanlike == false)
            {
                return false;
            }

            // If they have white or gray hair already, definitely change it! 
            if (this.hasWhiteOrGrayHair(pawn))
            {
                int pawnAge = pawn.ageTracker.AgeBiologicalYears;
                if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn is {pawn.gender} and {pawnAge} years old.");
                // Substantially reduce the odds if the pawn is over the age of 50 (-10% per year).
                if (pawnAge >= 50)
                {
                    if (pawnAge >= 60)
                    {
                        if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn age ({pawnAge}) is over 60, not changing hair color.");
                        return false;
                    }

                    int rangeMax = pawnAge - 50 + 1;
                    int randomNum = rnd.Next(0, pawnAge - 50 + 1);
                    if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn age ({pawnAge}) is >= 50 < 60, max range: {rangeMax}. Random number = {randomNum}.");

                    // 0-1 @ 50 = 50%; 0-2 @ 51 = 33% chance, 0-3 @ 52 = 25% ... 0-10 @ 59 = 9%
                    if (randomNum == 0)
                    {
                        if (CryoRegenesis.Settings.debugMode) Log.Message("Changing the hair color!!");
                        return this.changeHairColorRandomly(pawn);
                    }

                    return false;
                }
                // If the pawn is male and older than 55, definitely change it. 
                // -or-
                // If the pawn is female and older than 30, definitely change it.
                else if (
                    (pawn.gender == Gender.Male && pawnAge <= 55) ||
                    (pawn.gender == Gender.Female && pawnAge <= 30)
                    )
                {
                    if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn is {pawnAge} years old and prematurely balding. Changing the hair color!!");
                    return this.changeHairColorRandomly(pawn);
                }
            }

            var dice1 = rnd.Next(1, 7);
            var dice2 = rnd.Next(1, 7);
            var diceSum = dice1 + dice2;

            if (CryoRegenesis.Settings.debugMode) Log.Message($"Dice rolls: ({dice1}, {dice2}) = {diceSum}");

            // One in Three chance that their hair color will be changed otherwise. 
            // Stats taken from https://statweb.stanford.edu/~susan/courses/s60/split/node65.html (http://archive.is/wip/v39lj)
            // 2 = 2.78%, 3 = 5.56%, 4 = 8.33%, 5 = 11.11%, 11 = 5.56% = 33.34%
            if (diceSum <= 5 || diceSum == 11)
            {
                Log.Message("Fate smiles in their favor! Changing the hair color!!");
                return this.changeHairColorRandomly(pawn);
            }

            return false;
        }

        public override bool TryAcceptThing(Thing thing, bool allowSpecialEffects = true)
        {
            if (thing == null)
            {
                return false;
            }
            if (base.TryAcceptThing(thing, allowSpecialEffects))
            {
                restoreCoolDown = -1000;
                enterTime = Find.TickManager.TicksGame;
                var pawn = thing as Pawn;
                if (pawn == null)
                {
                    return false;
                }

                #if RIMWORLD14
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
                Pawn pawn = ContainedThing as Pawn;
                pawn.ageTracker.AgeBiologicalTicks.TicksToPeriod(out int years, out int quadrums, out int days, out float hours);
                //string bioTime = "AgeBiological".Translate(new object[]{years,quadrums,days});
                string bioTime = "AgeBiological".Translate((NamedArgument) years,
                    (NamedArgument) quadrums, (NamedArgument) days);

                if (isSafeToRepair)
                {
                    if (this.hediffsToHeal.Any())
                    {
                        return base.GetInspectString() + ", " + AgeHediffs(pawn).ToString() + " Age Disabilities, " + InjuryHediffs(pawn).ToString() + " Injuries\n" + bioTime + "\nTime To Heal: " + this.TTLToHeal;
                    }
                    else
                    {
                        return base.GetInspectString() + ", " + AgeHediffs(pawn).ToString() + " Age Disabilities, " + InjuryHediffs(pawn).ToString() + " Injuries\n" + bioTime;
                    }
                }
                else
                {
                    return base.GetInspectString() + " [Error] Has artificial body parts.\n" + bioTime;
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
            Log.Warning("Pawn name: " + pawn.def.defName);
            Log.Warning("Life expectancy: " + pawn.RaceProps.lifeExpectancy);
            Log.Warning("Target age: " + this.targetAge);

            return this.targetAge;
        }
    }
}
