// ==== ./Source/CorpseResurrector.cs ====
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
using System;
using System.Linq;
using UnityEngine;
using Verse;
// ReSharper disable All

namespace BetterRimworlds.CryoRegenesis;

public class Resurrector
{
    public const int REQ_URANIUM    = 150;
    public const int REQ_GOLD       = 500;
    public const int REQ_LUCIFERIUM = 50;

    // State — kept public for Building_CryoRegenesis.ExposeData serialization.
    public float ResurrectionProgress = 0f;
    public int[] ResurrectionFuelReqs = new int[3] { REQ_URANIUM, REQ_GOLD, REQ_LUCIFERIUM };
    public float InitialRot = -1f;

    private CompRefuelable refuelable;
    private CompProperties_Refuelable fuelprops;
    private CompPowerTrader power;
    private Action onRejected;
    private Action<Pawn> onResurrected;

    /// <summary>
    /// Wire up building components and ejection callbacks. Must be called from SpawnSetup.
    /// </summary>
    public void Initialize(
        CompRefuelable refuelable,
        CompProperties_Refuelable fuelprops,
        CompPowerTrader power,
        Action onRejected,
        Action<Pawn> onResurrected)
    {
        this.refuelable    = refuelable;
        this.fuelprops     = fuelprops;
        this.power         = power;
        this.onRejected    = onRejected;
        this.onResurrected = onResurrected;
    }

    public void ResetFuelRequirement()
    {
        fuelprops.fuelFilter = new ThingFilter();
        fuelprops.fuelFilter.SetAllow(ThingDefOf.Uranium, true);
        ResurrectionFuelReqs[0] -= (int)refuelable.Fuel;
        fuelprops.fuelCapacity = 150;
        ResurrectionProgress = 0f;
    }

    /// <summary>Full reset when a new corpse is accepted.</summary>
    public void ResetForNewCorpse()
    {
        ResurrectionProgress = 0f;
        ResurrectionFuelReqs = new int[3] { REQ_URANIUM, REQ_GOLD, REQ_LUCIFERIUM };
    }

    /// <summary>
    /// Drive the resurrection state machine for one tick cycle.
    /// Calls onRejected or onResurrected when the state resolves.
    /// </summary>
    public void Process(Corpse corpse)
    {
        // Make sure that they have a brain. Everything else is optional.
        var deadPawn  = corpse.InnerPawn;
        var pawnBrain = deadPawn.health.hediffSet.GetBrain();
        if (pawnBrain == null)
        {
            Messages.Message("[CryoRegenesis] Pawn rejected: No brain.", MessageTypeDefOf.RejectInput);
            onRejected();
            return;
        }

        // Make sure that the corpse isn't more than 10% degraded.
        var rottable = corpse.GetComp<CompRottable>();
        // If they've been dead and unfrozen for more than 1 day, the CryoRegenesis casket cannot resurrect them.
        if (rottable != null && rottable.RotProgress > 60_000)
        {
            Messages.Message(
                "[CryoRegenesis] Pawn rejected: More than 1 day unfrozen (" + rottable.RotProgress + "). Their brain is too degraded.",
                MessageTypeDefOf.RejectInput);
            onRejected();
            return;
        }

        // --- State Machine for Resource Gathering ---

        // State 1: Need Luciferium (first priority as requested)
        if (this.ResurrectionFuelReqs[2] > 0)
        {
            // Configure the refuelable component to accept Luciferium
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Uranium,    false);
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Gold,       false);
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Luciferium, true);

            // Set the capacity to what is still needed
            fuelprops.fuelCapacity = this.ResurrectionFuelReqs[2];

            // If fuel has been delivered, process it
            if (refuelable.Fuel > 0)
            {
                int delivered = (int)Math.Ceiling(refuelable.Fuel);
                this.ResurrectionFuelReqs[2] -= delivered;
                refuelable.ConsumeFuel(refuelable.Fuel);
                Log.Message($"[CryoRegenesis] Luciferium delivered: {delivered}. Needed: {this.ResurrectionFuelReqs[2]}");
            }

            // Stop processing for this tick; we are waiting for more fuel.
            return;
        }
        // State 2: Need Uranium (checked after Luciferium is complete)
        else if (this.ResurrectionFuelReqs[0] > 0)
        {
            // Configure the refuelable component to accept Uranium
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Uranium,    true);
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Gold,       false);
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Luciferium, false);

            // Set the capacity to what is still needed
            fuelprops.fuelCapacity = this.ResurrectionFuelReqs[0];

            // If fuel has been delivered, process it
            if (refuelable.Fuel > 0)
            {
                int delivered = (int)Math.Ceiling(refuelable.Fuel);
                this.ResurrectionFuelReqs[0] -= delivered;
                refuelable.ConsumeFuel(refuelable.Fuel);
                Log.Message($"[CryoRegenesis] Uranium delivered: {delivered}. Needed: {this.ResurrectionFuelReqs[0]}");
            }

            // Stop processing for this tick; we are waiting for more fuel.
            return;
        }
        // State 3: Need Gold (checked after Luciferium and Uranium are complete)
        else if (this.ResurrectionFuelReqs[1] > 0)
        {
            // Configure the refuelable component to accept Gold
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Uranium,    false);
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Gold,       true);
            fuelprops.fuelFilter.SetAllow(ThingDefOf.Luciferium, false);

            // Set the capacity to what is still needed
            fuelprops.fuelCapacity = this.ResurrectionFuelReqs[1];

            // If fuel has been delivered, process it
            if (refuelable.Fuel > 0)
            {
                int delivered = (int)Math.Ceiling(refuelable.Fuel);
                this.ResurrectionFuelReqs[1] -= delivered;
                refuelable.ConsumeFuel(refuelable.Fuel);
                Log.Message($"[CryoRegenesis] Gold delivered: {delivered}. Needed: {this.ResurrectionFuelReqs[1]}");
            }

            // Stop processing for this tick; we are waiting for more fuel.
            return;
        }

        if (Find.TickManager.TicksGame % 1000 == 0)
        {
            return;
        }

        // --- Resurrection Progress (only runs if all fuel requirements are met) ---
        // The check for fuel requirements is implicitly handled by falling through the if/else-if chain.

        // Turn off refueling now that we're full
        fuelprops.fuelCapacity = 0;

        // Adjust this value to control resurrection speed
        float resurrectionFactor = 0.001f;

        // Increment progress
        this.ResurrectionProgress += resurrectionFactor;
        this.ResurrectionProgress = Mathf.Clamp01(this.ResurrectionProgress); // Ensure it stays between 0 and 1

        if (CryoRegenesis.Settings.debugMode)
            Log.Message($"[CryoRegenesis] Resurrection Progress: {this.ResurrectionProgress * 100:F1}%");

        // Check if resurrection is complete
        if (this.ResurrectionProgress >= 1.0f)
        {
            var resurrectedPawn = corpse.InnerPawn;
            Log.Message($"[CryoRegenesis] {resurrectedPawn.Name} has been resurrected!");
            Messages.Message($"[CryoRegenesis] {resurrectedPawn.Name} has been resurrected!", resurrectedPawn, MessageTypeDefOf.PositiveEvent);

            #if RIMWORLD15 || RIMWORLD16
            ResurrectionUtility.TryResurrectWithSideEffects(resurrectedPawn);
            #else
            ResurrectionUtility.ResurrectWithSideEffects(resurrectedPawn);
            #endif

            this.ResurrectionProgress = 0f;
            this.AddLuciferiumSideEffect(resurrectedPawn);

            // Reset fuel requirements for the next pawn
            this.ResetFuelRequirement();

            onResurrected(resurrectedPawn);
        }
    }

    public void AddLuciferiumSideEffect(Pawn pawn)
    {
        if (pawn?.health?.hediffSet == null)
            return;

        // Add the addiction directly
        HediffDef luciferiumAddiction = DefDatabase<HediffDef>.GetNamed("LuciferiumAddiction");
        Hediff addictionHediff = HediffMaker.MakeHediff(luciferiumAddiction, pawn);
        pawn.health.AddHediff(addictionHediff);

        // Important: Set the last dose time to prevent immediate withdrawal
        Need_Chemical drugNeed = pawn.needs?.AllNeeds.OfType<Need_Chemical>()
            .FirstOrDefault(n => n.def.defName == "Chemical_Luciferium");

        if (drugNeed != null)
        {
            drugNeed.CurLevel = drugNeed.MaxLevel; // Start them off satisfied
        }

        pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(
            DefDatabase<ThoughtDef>.GetNamed("CryoRegenesis_LuciferiumSideEffect")
        );
    }
}
