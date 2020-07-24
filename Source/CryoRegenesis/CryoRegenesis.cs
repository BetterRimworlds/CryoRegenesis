using RimWorld;
using System;
using System.Linq;
using Verse;

namespace CryoRegenesis
{
    public class Building_CryoRegenesis : Building_CryptosleepCasket
    {
        private Random rnd = new Random();

        int cryptoHediffCooldown;
        int cryptoHediffCooldownBase = GenDate.TicksPerMonth / 2;
        long restoreCoolDown = -1000;
        int enterTime;
        //int rate = 30;
        //int rate = 150;
        int rate = 1500;
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
            fuelprops.fuelConsumptionRate = fuelConsumption / 60f;
            fuelConsumption = (refuelable.Props.fuelCapacity * fuelprops.fuelConsumptionRate) / GenDate.TicksPerYear;

            //fuelprops.fuelConsumptionRate = 0.33333f;
            Log.Message("Fuel capacity: " + refuelable.Props.fuelCapacity);
            Log.Message("Fuel capacity v2: " + fuelprops.fuelCapacity);
            Log.Message("Fuel consumption rate: " + fuelprops.fuelConsumptionRate);
            Log.Message("Fuel consumption rate v2: " + fuelprops.fuelConsumptionRate);
            Log.Message("Fuel consumption per Year: " + (refuelable.Props.fuelCapacity * refuelable.Props.fuelConsumptionRate));
            Log.Message("Fuel consumption per Tick: " + fuelConsumption);
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

            Log.Message("Healing Frequency: Base (" + baseFrequency + ") Min (" + minFrequency + ") Max (" + maxFrequency + ") Actual: " + frequency + "%");

            return (int)Math.Round((frequency / 100) * (GenDate.TicksPerYear * decadeOfLife));
        }

        public override void Tick()
        {
            bool is21OrYounger;
            if (HasAnyContents && refuelable.HasFuel)
            {
                Pawn pawn = ContainedThing as Pawn;

                if (power.PowerOn)
                {
                    bool hasInjuries;

                    long ticksLeft = (pawn.ageTracker.AgeBiologicalTicks - restoreCoolDown);
                    double targetAge = (double)restoreCoolDown / (double)GenDate.TicksPerYear;

                    is21OrYounger = pawn.ageTracker.AgeBiologicalTicks <= ((GenDate.TicksPerYear * 21) + rate);
                    hasInjuries = (pawn.health.hediffSet.GetHediffs<Hediff>().Count() > 0);

                    if (is21OrYounger && !hasInjuries)
                    {
                        //this.props.basePowerConsumption = 0;
                        power.PowerOn = false;
                        power.PowerOutput = 0;

                        return;
                    }

                    refuelable.ConsumeFuel(fuelConsumption);
                    //if (pawn.ageTracker.AgeBiologicalTicks % GenDate.TicksPerSeason == 0)
                    if (power.PowerOn && hasInjuries && !is21OrYounger && pawn.ageTracker.AgeBiologicalTicks % GenDate.TicksPerSeason <= rate)
                    {
                        Log.Message("(" + pawn.NameStringShort + ") Years to Wait: " + ((double)ticksLeft / (double)GenDate.TicksPerYear) + " | Target age: " + targetAge);
                    }

                    if (is21OrYounger)
                    {
                        restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks;
                    }

                    if (refuelable.FuelPercent < 0.10f)
                    {
                        Log.Message("Not enough Uranium to heal.");
                    }

                    // Remove all health-related injuries if they're younger than the targetAge.
                    if (hasInjuries && refuelable.FuelPercent >= 0.10f && restoreCoolDown > -1000 && pawn.ageTracker.AgeBiologicalTicks <= restoreCoolDown)
                    {
                        Log.Message("Fuel count to fully refueled: " + refuelable.GetFuelCountToFullyRefuel());
                        Log.Message("Cooled down");

                        string hediffName;
                        foreach (Hediff oldHediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
                        {
                            hediffName = oldHediff.def.label;
                            refuelable.ConsumeFuel(1f);

                            if (hediffName == "gunshot")
                            {
                                pawn.health.RemoveHediff(oldHediff);
                            }
                            else
                            {
                                pawn.health.RemoveHediff(oldHediff);
                            }

                            restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - GenDate.TicksPerYear;
                            //restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - GenDate.TicksPerSeason;
                            Log.Message("Cured HEDIFF: " + hediffName + " @ " + oldHediff.def.description + " | " + oldHediff.ToString());

                            break;
                        }
                    }

                    if (hasInjuries && (ticksLeft <= 0 || restoreCoolDown == -1000))
                    {
                        int ticksToWait = this.CalculateHealingTime(pawn);

                        restoreCoolDown = pawn.ageTracker.AgeBiologicalTicks - ticksToWait;

                        targetAge = (double)restoreCoolDown / (double)GenDate.TicksPerYear;
                        Log.Message("Current Age in Ticks: " + pawn.ageTracker.AgeBiologicalTicks + " vs. " + restoreCoolDown);
                        Log.Message("(" + pawn.NameStringShort + ") Years to Wait: " + ((double)ticksToWait / (double)GenDate.TicksPerYear) + " | Target age: " + targetAge);
                    }
                }

                bool hasHediffs = AgeHediffs(pawn) > 0;
                if (hasHediffs || pawn.ageTracker.AgeBiologicalTicks > GenDate.TicksPerYear * 21)
                {
                    power.PowerOutput = -props.basePowerConsumption;
                    if (power.PowerOn)
                    {
                        refuelable.ConsumeFuel(fuelConsumption);
                        cryptoHediffCooldown = Math.Max(cryptoHediffCooldown - 1, 0);
                        if (pawn.ageTracker.AgeBiologicalTicks > GenDate.TicksPerYear * 21)
                            pawn.ageTracker.AgeBiologicalTicks = Math.Max(pawn.ageTracker.AgeBiologicalTicks - rate, GenDate.TicksPerYear * 21);
                        if (hasHediffs && cryptoHediffCooldown == 0)
                        {
                            foreach (Hediff oldHediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
                            {
                                string hediffName = oldHediff.def.label;
                                if (hediffName == "bad back" && pawn.ageTracker.AgeBiologicalYears < 39)
                                {
                                    pawn.health.RemoveHediff(oldHediff);
                                    cryptoHediffCooldown = cryptoHediffCooldownBase;
                                    break;
                                }
                                else if (hediffName == "frail" && pawn.ageTracker.AgeBiologicalYears < 48)
                                {
                                    pawn.health.RemoveHediff(oldHediff);
                                    cryptoHediffCooldown = cryptoHediffCooldownBase;
                                    break;
                                }
                                else if (hediffName == "cataract" && pawn.ageTracker.AgeBiologicalYears < 52)
                                {
                                    foreach (Hediff cataractHediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
                                    {
                                        if (cataractHediff.def.label == "cataract")
                                        {
                                            pawn.health.RemoveHediff(cataractHediff);
                                        }
                                    }
                                    cryptoHediffCooldown = cryptoHediffCooldownBase;
                                    break;
                                }
                                else if (hediffName == "hearing loss" && pawn.ageTracker.AgeBiologicalYears < 52)
                                {
                                    foreach (Hediff hearingHediff in pawn.health.hediffSet.GetHediffs<Hediff>().ToList())
                                    {
                                        if (hearingHediff.def.label.ToString() == "hearing loss")
                                        {
                                            pawn.health.RemoveHediff(hearingHediff);
                                        }
                                    }
                                    cryptoHediffCooldown = cryptoHediffCooldownBase;
                                    break;
                                }
                                else if (hediffName == "dementia" && pawn.ageTracker.AgeBiologicalYears < 66)
                                {
                                    pawn.health.RemoveHediff(oldHediff);
                                    cryptoHediffCooldown = cryptoHediffCooldownBase;
                                    break;
                                }
                                else if (hediffName == "alzheimer's" && pawn.ageTracker.AgeBiologicalYears < 72)
                                {
                                    //oldHediff.Heal(1 / 7.5f);
                                    oldHediff.DirectHeal(1 / 7.5f);
                                    if (oldHediff.Severity > 0) cryptoHediffCooldown = GenDate.TicksPerDay;
                                    else
                                    {
                                        cryptoHediffCooldown = cryptoHediffCooldownBase;
                                        pawn.health.RemoveHediff(oldHediff);
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                    power.PowerOutput = 0;
            }
            else
                power.PowerOutput = 0;
        }
        public override void EjectContents()
        {
            Pawn pawn = ContainedThing as Pawn;
            if (pawn.ageTracker.AgeBiologicalTicks >= GenDate.TicksPerYear * 21)
            {
                pawn.health.AddHediff(cryosickness);
            }
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
                return base.GetInspectString() + ", " + AgeHediffs(pawn).ToString() + " Age Disabilities, " + InjuryHediffs(pawn).ToString() + " Injuries\n" + bioTime;
            }
            else return base.GetInspectString();
        }
    }
}