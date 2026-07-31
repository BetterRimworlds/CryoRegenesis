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
 *
 * =============================================================================
 * HARMONY PATCH LOADING — DO NOT REGRESS
 * =============================================================================
 *
 * Problem (RimWorld 1.2, 2026-07):
 *   "Carry to CryoRegenesis casket" never appeared for unconscious / Downed
 *   prisoners. Eligibility and float-menu code looked correct; the real failure
 *   was that *no* Harmony patches in this assembly applied at all.
 *
 * Root cause:
 *   Mod construction used a single `harmony.PatchAll(Assembly)`. One unrelated
 *   patch (Patch_DoRecruit_RegenContract) resolved TargetMethod() against the
 *   *1.3+* InteractionWorker_RecruitAttempt.DoRecruit signature. On 1.2 that
 *   method still takes `float recruitChance`, so AccessTools returned null,
 *   PatchAll threw, and the catch block only logged:
 *     "Failed to load harmony patches: ..."
 *   Every other patch — including FloatMenuMakerMap.AddHumanlikeOrders for the
 *   carry option — died with it. Symptom looked like a carry-eligibility bug.
 *
 * How not to repeat this:
 *   1. Never rely on one PatchAll for the whole assembly when any TargetMethod
 *      is version-sensitive. Apply each [HarmonyPatch] type with
 *      CreateClassProcessor(type).Patch() inside its own try/catch (see ctor).
 *   2. When resolving vanilla methods by signature, probe *every* supported
 *      RimWorld version (1.2 often differs). Prefer ordered AccessTools.Method
 *      fallbacks; never assume 1.3+ is the only shape.
 *   3. If a float-menu / job feature "does nothing" on one game version only,
 *      check the player log first for Harmony load failures before rewriting
 *      gameplay conditions (Downed, Moving %, beds, etc.).
 *   4. After changing any TargetMethod / patch attribute, boot *each* target
 *      version (or at least 1.2 and latest) and confirm no
 *      "[CryoRegenesis] Harmony patch failed" lines at startup.
 * =============================================================================
 */

using RimWorld;
using System.Reflection;
using HarmonyLib;
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

        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        {
            try
            {
                if (!type.IsDefined(typeof(HarmonyPatch), inherit: true))
                    continue;

                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                Log.Error(
                    $"[CryoRegenesis] Harmony patch failed on {type.FullName}: {ex}");
            }
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
    private const float ActivePowerConsumption = 3000f;
    private readonly Cosmetics cosmetics = new Cosmetics();
    private readonly RegenesisCycle regenesisCycle = new RegenesisCycle();
    private readonly Resurrector resurrector = new Resurrector();
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

    protected Map currentMap;
    private bool IsDisconnectedFromPowerGrid => power?.PowerNet == null;
    private bool IsPowerUnavailable => power == null || !power.PowerOn || IsDisconnectedFromPowerGrid;

    /// True when the pod is acting as a normal cryptosleep casket (flicked off, unpowered,
    /// or disconnected) rather than an active CryoRegenesis chamber.
    public bool IsUnpoweredCryptosleepMode => this.IsPowerUnavailable;

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
                this.regenesisCycle.InitializeForLoadedPawn(pawn);
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

    public override void ExposeData()
    {
        base.ExposeData();
        string fuelReqs = String.Join(",", resurrector.ResurrectionFuelReqs);
        Scribe_Values.Look<string>(ref fuelReqs, "ResurrectionFuelReqs");
        float resProgress = resurrector.ResurrectionProgress;
        Scribe_Values.Look<float>(ref resProgress, "ResurrectionProgress", 0f);
        resurrector.ResurrectionProgress = resProgress;
        this.regenesisCycle.ExposeData();
        this.ExposeTrueAgeData();

        if (String.IsNullOrEmpty(fuelReqs) == false)
        {
            resurrector.ResurrectionFuelReqs = fuelReqs.Split(',').Select(int.Parse).ToArray();
        }
    }

    #if RIMWORLD16
    protected override void Tick()
    #else
    public override void Tick()
    #endif
    {
        base.Tick();

        if (IsPowerUnavailable)
        {
            if (power != null)
            {
                power.PowerOutput = 0;
            }

            return;
        }

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

            isTargetAge = this.regenesisCycle.IsTargetAge(pawn);
            hasInjuries = this.regenesisCycle.HasCurableInjuries;

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
                // Record contract completion before the healthy-at-target branch ejects
                // the pawn and returns. Natural Guest departure otherwise bypasses
                // MarkTargetAgeReached entirely.
                if (isTargetAge)
                {
                    this.regenesisCycle.MarkTargetAgeReached(pawn);
                }

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
                    this.regenesisCycle.UpdateHealingEta(pawn, rate);
                }

                if (this.regenesisCycle.IsOutOfFuelForHealing(pawn, refuelable))
                {
                    Log.Message("Not enough Uranium to heal.");
                }

                this.regenesisCycle.TryHealNextInjury(pawn, refuelable);

                if (this.regenesisCycle.ShouldScheduleHealing(pawn))
                {
                    this.regenesisCycle.ScheduleNextHealing(pawn);
                }
            }

            if (this.regenesisCycle.ShouldEjectAfterHealing(pawn))
            {
                Log.Warning("No more injuries; ejecting.");
                this.EjectContents();
            }

            if (pawn.ageTracker.AgeBiologicalTicks > this.regenesisCycle.TargetAgeTicks)
            {
                #if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
                power.PowerOutput = -props.PowerConsumption;
                #else
                power.PowerOutput = -ActivePowerConsumption;
                #endif

                if (power.PowerOn)
                {
                    if (pawn.ageTracker.AgeBiologicalTicks > this.regenesisCycle.TargetAgeTicks)
                    {
                        refuelable.ConsumeFuel(fuelConsumption * ((pawnAge - 10) * 0.1f));

                        long ageBeforeRegression = pawn.ageTracker.AgeBiologicalTicks;
                        pawn.ageTracker.AgeBiologicalTicks = Math.Max(ageBeforeRegression - rate, this.regenesisCycle.TargetAgeTicks);
                        long ageRemoved = ageBeforeRegression - pawn.ageTracker.AgeBiologicalTicks;
                        TrueAgeTracker trueAgeTracker = pawn.health?.hediffSet?
                            .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
                        trueAgeTracker?.RecordCryoRegenesisDeAging(ageRemoved);
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
            if (power != null)
            {
                power.PowerOutput = 0;
            }

            base.EjectContents();

            return;
        }

        Pawn pawn = ContainedThing as Pawn;
        pawn.health.AddHediff(cryosickness);

        // True Age ledger must update before body-positivity thoughts, which
        // read lifetime years erased from the tracker.
        int sessionYearsRemoved = 0;
        if (pawn.ageTracker != null)
        {
            int currentAge = Mathf.RoundToInt(pawn.ageTracker.AgeBiologicalYearsFloat);
            sessionYearsRemoved = this.regenesisCycle.OriginalAge - currentAge;
        }

        this.ApplyTrueAgeOnEject(pawn);

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

            // Stackable regen mood; stage/duration from lifetime True Age removed.
            RegenesisThoughts.AddBodyPositivityThought(pawn, sessionYearsRemoved);
        }

        pawn.needs.rest.SetInitialLevel();
        pawn.needs.food.SetInitialLevel();

        if (power != null)
        {
            power.PowerOutput = 0;
        }

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
            this.regenesisCycle.BeginNewPawn(pawn);
            this.RecordTrueAgeSnapshot(pawn);

            if (!IsPowerUnavailable)
            {
                #if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
                power.PowerOutput = -props.PowerConsumption;
                #else
                power.PowerOutput = -ActivePowerConsumption;
                #endif
            }
            else if (power != null)
            {
                power.PowerOutput = 0;
            }

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

            var trueAgeTracker = pawn.health?.hediffSet?
                .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
            if (trueAgeTracker != null && trueAgeTracker.underRegenContract && trueAgeTracker.desiredAgeTicks > 0)
            {
                bioTime += "\nContract target age: " + trueAgeTracker.GetContractTargetAgeYears().ToString("0.#")
                    + " (" + trueAgeTracker.GetContractProgressPercent().ToString("0") + "% there)";
            }

            if (this.regenesisCycle.HasCurableInjuries)
            {
                return base.GetInspectString() + ", " + this.regenesisCycle.AgeHediffs() + " Age Disabilities, " + this.regenesisCycle.InjuryHediffs() + " Injuries\n" + bioTime + "\nTime To Heal: " + this.regenesisCycle.TtlToHeal;
            }
            else
            {
                return base.GetInspectString() + ", " + this.regenesisCycle.AgeHediffs() + " Age Disabilities, " + this.regenesisCycle.InjuryHediffs() + " Injuries\n" + bioTime;
            }
        }
        else return base.GetInspectString();
    }
}


[DefOf]
public static class CryoRegenesisDefOf
{
    public static HediffDef CryoRegenesisSedation;
    public static JobDef CR_CarryToCryoRegenesis;
    public static RecipeDef CR_AdministerCryoRegenesisSedation;

    static CryoRegenesisDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(CryoRegenesisDefOf));
    }
}
