using RimWorld;
using System;
using System.Linq;
using Verse;

namespace CryoRegenesis
{
    public class Building_CryoRegenesis : Building_CryptosleepCasket
    {
        private Random rnd = new Random();

        bool isSafeToRepair = true;
        int cryptoHediffCooldown;
        int cryptoHediffCooldownBase = GenDate.TicksPerMonth / 2;
        long restoreCoolDown = -1000;
        int enterTime;
        int targetAge; // 21 for humans. 25% of life expectancy for every other lifeform.
        //int rate = 30;
        //int rate = 150;
        int rate = 500;
        float fuelConsumption;
        HediffDef cryosickness = HediffDef.Named("CryptosleepSickness");
        CompRefuelable refuelable;
        CompPowerTrader power;
        CompProperties_Power props;
        CompProperties_Refuelable fuelprops;

        public int AgeHediffs(Pawn pawn)
        {
            if (pawn != null)
            {
                bool hasCataracts = false;
                bool hasHearingLoss = false;
                int hediffs = 0;
                foreach (Hediff injury in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
                {
                    string injuryName = injury.def.label;
                    if (injuryName == "cataract" && !hasCataracts)
                    {
                        hediffs += 1;
                        hasCataracts = true;
                    }
                    else if (injuryName == "hearing loss" && !hasHearingLoss)
                    {
                        hediffs += 1;
                        hasHearingLoss = true;
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

                return pawn.health.hediffSet.GetHediffs<Hediff>().Count() - OldAgeHediffs;
            }

            return 0;
        }

        public override void SpawnSetup()
        {
            base.SpawnSetup();
            refuelable = GetComp<CompRefuelable>();
            power = GetComp<CompPowerTrader>();
            props = power.Props;
            fuelprops = refuelable.Props;

            // Require more fuel for faster rates.
            float fuelPerReversedYear = 1.0f * ((float)rate / 250);

            fuelConsumption =  fuelPerReversedYear / ((float)GenDate.TicksPerYear / rate);
            Log.Message("Fuel consumption per Tick: " + fuelConsumption);

            if (HasAnyContents)
            {
                Pawn pawn = ContainedThing as Pawn;
                this.configTargetAge(pawn);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.LookValue<int>(ref cryptoHediffCooldown, "cryptoHediffCooldown");
            Scribe_Values.LookValue<int>(ref enterTime, "enterTime");
        }

        private int CalculateHealingTime(Pawn pawn)
        {
            // Get the pawn's age in Years. e.g., 65 years.
            int pawnAge = (int) (pawn.ageTracker.AgeBiologicalTicks / GenDate.TicksPerYear);

            // If the pawn is age 25 or younger, set it for a year or less.
            if (pawnAge <= 25) {
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
                Log.Message("Base Frequency: " + baseFrequency);
                Log.Message("Min Frequency: " + minFrequency);
                Log.Message("Max Frequency: " + maxFrequency);

                double frequency = rnd.Next(minFrequency, maxFrequency);

                Log.Message("Healing Frequency: Base (" + baseFrequency + ") Min (" + minFrequency + ") Max (" + maxFrequency + ") Actual: " + frequency + "%");

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
                hasInjuries = (pawn.health.hediffSet.GetHediffs<Hediff>().Count() > 0);

                if (this.isSafeToRepair == false)
                {
                    this.EjectContents();
                    this.props.basePowerConsumption = 0;
                    power.PowerOutput = 0;

                    return;
                }

                if (power.PowerOn)
                {
                    long ticksLeft = (pawn.ageTracker.AgeBiologicalTicks - restoreCoolDown);
                    double repairAge = (double)restoreCoolDown / (double)GenDate.TicksPerYear;

                    if (isTargetAge && !hasInjuries)
                    {
                        this.EjectContents();
                        this.props.basePowerConsumption = 0;
                        power.PowerOn = false;
                        power.PowerOutput = 0;

                        return;
                    }

                    if (power.PowerOn && hasInjuries && !isTargetAge && pawn.ageTracker.AgeBiologicalTicks % GenDate.TicksPerSeason <= rate)
                    {
                        Log.Message("(" + pawn.NameStringShort + ") Years to Wait: " + ((double)ticksLeft / (double)GenDate.TicksPerYear) + " | Next repair at: " + repairAge);
                    }

                    if (hasInjuries && ticksLeft <= 0 && refuelable.FuelPercent < 0.10f)
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
                        Log.Message("Cooled down");
                        
                        string hediffName;
                        foreach (Hediff oldHediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
                        {
                            hediffName = oldHediff.def.label;
                            refuelable.ConsumeFuel(Math.Max(refuelable.FuelPercent * 0.10f, 10));

                            pawn.health.RemoveHediff(oldHediff);

                            restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - GenDate.TicksPerYear;
                            if (ticksLeft < 0)
                            {
                                restoreCoolDown += ticksLeft;
                            }
                            //restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - GenDate.TicksPerSeason;
                            Log.Message("Cured HEDIFF: " + hediffName + " @ " + oldHediff.def.description + " | " + oldHediff.ToString());

                            break;
                        }
                    }

                    if (hasInjuries && (ticksLeft <= 0 || restoreCoolDown == -1000))
                    {
                        int ticksToWait = this.CalculateHealingTime(pawn);

                        restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - ticksToWait;
                        repairAge = (double)restoreCoolDown / (double)GenDate.TicksPerYear;
                        Log.Message("Current Age in Ticks: " + pawn.ageTracker.AgeBiologicalTicks + " vs. " + restoreCoolDown);
                        Log.Message("(" + pawn.NameStringShort + ") Years to Wait: " + ((double)ticksToWait / (double)GenDate.TicksPerYear) + " | Next repair at: " + repairAge);
                    }
                }

                //if ((!hasInjuries || (hasInjuries && refuelable.FuelPercent >= 0.25f)) && pawn.ageTracker.AgeBiologicalTicks > GenDate.TicksPerYear * 21)
                if (pawn.ageTracker.AgeBiologicalTicks > GenDate.TicksPerYear * targetAge)
                {
                    power.PowerOutput = -props.basePowerConsumption;
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

            power.PowerOutput = 0;
            base.EjectContents();
        }

        public override bool TryAcceptThing(Thing thing, bool allowSpecialEffects = true)
        {
            if (base.TryAcceptThing(thing, allowSpecialEffects))
            {
                cryptoHediffCooldown = cryptoHediffCooldownBase;
                restoreCoolDown = -1000;
                enterTime = Find.TickManager.TicksGame;
                if (refuelable.HasFuel && (AgeHediffs(thing as Pawn) > 0 || (thing as Pawn).ageTracker.AgeBiologicalTicks > GenDate.TicksPerYear * 21))
                {
                    power.PowerOutput = -props.basePowerConsumption;
                }

                Pawn pawn = (Pawn)thing;

                foreach (Hediff hediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
                {
                    if (hediff.def.hediffClass.ToString() == "Verse.Hediff_AddedPart")
                    {
                        Log.Error("[CryoRegenesis] " + pawn.NameStringShort + " has an added part: " + hediff.def.label);
                        isSafeToRepair = false;

                        return false;
                    }
                    else if (hediff.def.hediffClass.ToString() == "Verse.Hediff_Pregnant")
                    {
                        Log.Error("Won't repair: Pregnant");
                        isSafeToRepair = false;

                        return false;
                    }
                }

                this.configTargetAge(pawn);

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
                string bioTime = "AgeBiological".Translate(new object[]{years,quadrums,days});

                if (isSafeToRepair)
                {
                    return base.GetInspectString() + ", " + AgeHediffs(pawn).ToString() + " Age Disabilities, " + InjuryHediffs(pawn).ToString() + " Injuries\n" + bioTime;
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
                this.targetAge = 21;
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
