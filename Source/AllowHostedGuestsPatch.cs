// ==== Source/AllowHostedGuestsPatch.cs ====
/*
 * Carry-to-CryoRegenesis float menu.
 *
 * RW 1.2–1.5: Harmony postfix on FloatMenuMakerMap.AddHumanlikeOrders.
 * RW 1.6:     FloatMenuOptionProvider subclass (auto-discovered by the game).
 */

using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BetterRimworlds.CryoRegenesis;

// =========================================================================
//  Shared helpers — used by both the 1.2–1.5 patch and the 1.6 provider.
// =========================================================================

internal static class CryoCarryHelper
{
    /// True when the pawn is carriable: Downed, Moving at 0 %, or sedated
    /// and about to collapse.
    internal static bool IsCryoCarryTargetState(Pawn targetPawn)
    {
        if (targetPawn == null || targetPawn.Dead)
            return false;

        if (targetPawn.Downed)
            return true;

        PawnCapacitiesHandler capacities = targetPawn.health?.capacities;
        if (capacities != null &&
            (!capacities.CapableOf(PawnCapacityDefOf.Moving) ||
             capacities.GetLevel(PawnCapacityDefOf.Moving) <= 0f))
        {
            return true;
        }

        if (targetPawn.health?.hediffSet == null)
            return false;

        if (targetPawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic))
            return true;

        if (CryoRegenesisDefOf.CryoRegenesisSedation != null &&
            targetPawn.health.hediffSet.HasHediff(
                CryoRegenesisDefOf.CryoRegenesisSedation))
        {
            return true;
        }

        return false;
    }

    /// Should we offer the carry-to-cryo option for this target pawn?
    internal static bool ShouldAllowCryoRegenesisCarry(Pawn targetPawn)
    {
        if (targetPawn == null)
            return false;

        if (!IsCryoCarryTargetState(targetPawn))
            return false;

        if (targetPawn.RaceProps.Animal)
            return targetPawn.Faction == Faction.OfPlayer;

        if (targetPawn.RaceProps.Humanlike)
            return true;

        return false;
    }

    /// Nearest empty colony CryoRegenesis casket. No pathing / reservation
    /// checks — those are left to the job / toils at runtime.
    internal static Building_CryoRegenesis FindEmptyCryoRegenesisCasket(
        Pawn targetPawn)
    {
        Map map = targetPawn?.MapHeld;
        if (map == null)
            return null;

        Building_CryoRegenesis best = null;
        int bestDist = int.MaxValue;
        IntVec3 pos = targetPawn.PositionHeld;

        List<Thing> buildings = map.listerThings.ThingsInGroup(
            ThingRequestGroup.BuildingArtificial);

        for (int i = 0; i < buildings.Count; i++)
        {
            if (!(buildings[i] is Building_CryoRegenesis casket))
                continue;

            if (casket.HasAnyContents)
                continue;

            int dist = casket.Position.DistanceToSquared(pos);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = casket;
            }
        }

        return best;
    }

    /// Build and queue the carry job on the acting pawn.
    internal static void IssueCryoCarryJob(Pawn carrier, Pawn patient)
    {
        Building_CryoRegenesis chosen =
            FindEmptyCryoRegenesisCasket(patient);

        if (chosen == null)
        {
            Messages.Message(
                "NoReachableCryoRegenesis".Translate(),
                patient,
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        Job job = JobMaker.MakeJob(
            CryoRegenesisDefOf.CR_CarryToCryoRegenesis,
            patient,
            chosen);

        job.count = 1;
        carrier.jobs.TryTakeOrderedJob(job, JobTag.Misc);
    }

    /// Create a float-menu option for a single carrier → patient pair.
    /// Returns null when the patient is ineligible.
    internal static FloatMenuOption MakeCryoCarryOption(
        Pawn carrier,
        Pawn patient)
    {
        if (!ShouldAllowCryoRegenesisCarry(patient))
            return null;

        string label = "CarryToCryoRegenesisCasket".Translate();

        Building_CryoRegenesis casket =
            FindEmptyCryoRegenesisCasket(patient);

        if (casket == null)
        {
            return new FloatMenuOption(
                label + ": " + "NoReachableCryoRegenesis".Translate(),
                null);
        }

        // Capture for the delegate.
        Pawn capturedPatient = patient;
        Pawn capturedCarrier = carrier;

        FloatMenuOption option = new FloatMenuOption(
            label,
            delegate { IssueCryoCarryJob(capturedCarrier, capturedPatient); },
            MenuOptionPriority.RescueOrCapture);

        return FloatMenuUtility.DecoratePrioritizedTask(
            option,
            carrier,
            patient);
    }
}


// =========================================================================
//  RimWorld 1.2 – 1.5:  Harmony postfix on AddHumanlikeOrders
// =========================================================================

#if !RIMWORLD16

[HarmonyPatch(typeof(FloatMenuMakerMap), "AddHumanlikeOrders")]
public static class Patch_FloatMenuMakerMap_AddHumanlikeOrders_CryoRegenesis
{
    public static void Postfix(
        Vector3 clickPos,
        Pawn pawn,
        List<FloatMenuOption> opts)
    {
        if (pawn?.Map == null)
            return;

        if (pawn.Downed || pawn.Dead || pawn.IsBurning())
            return;

        foreach (Pawn targetPawn in GetCryoCarryCandidates(clickPos, pawn))
        {
            FloatMenuOption option =
                CryoCarryHelper.MakeCryoCarryOption(pawn, targetPawn);

            if (option != null)
                opts.Add(option);
        }
    }

    private static IEnumerable<Pawn> GetCryoCarryCandidates(
        Vector3 clickPos,
        Pawn carrier)
    {
        var seen = new HashSet<Pawn>();

        TargetingParameters tp = MakeCryoCarryTargetingParameters(carrier);

#if RIMWORLD12
        IEnumerable<LocalTargetInfo> targets = GenUI.TargetsAt_NewTemp(
            clickPos, tp, thingsOnly: true);
#else
        IEnumerable<LocalTargetInfo> targets = GenUI.TargetsAt(
            clickPos, tp, thingsOnly: true);
#endif

        foreach (LocalTargetInfo target in targets)
        {
            if (target.Thing is Pawn targetPawn && seen.Add(targetPawn))
                yield return targetPawn;
        }

        // Fallback: neighbourhood cell scan for multi-cell beds, etc.
        Map map = carrier.Map;
        IntVec3 cell = IntVec3.FromVector3(clickPos);

        if (!cell.InBounds(map))
            yield break;

        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                IntVec3 scanCell =
                    new IntVec3(cell.x + x, cell.y, cell.z + z);

                if (!scanCell.InBounds(map))
                    continue;

                List<Thing> things = scanCell.GetThingList(map);

                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Pawn targetPawn &&
                        seen.Add(targetPawn) &&
                        CryoCarryHelper.IsCryoCarryTargetState(targetPawn))
                    {
                        yield return targetPawn;
                    }
                }
            }
        }
    }

    private static TargetingParameters MakeCryoCarryTargetingParameters(
        Pawn carrier)
    {
        return new TargetingParameters
        {
            canTargetLocations = false,
            canTargetSelf      = false,
            canTargetPawns     = true,
            canTargetHumans    = true,
            canTargetAnimals   = true,
            canTargetBuildings = false,
            canTargetItems     = false,

            onlyTargetIncapacitatedPawns    = false,
            mustBeSelectable                = false,
            mapObjectTargetsMustBeAutoAttackable = false,

            validator = target =>
            {
                if (!target.HasThing)
                    return false;

                Pawn targetPawn = target.Thing as Pawn;
                return targetPawn != null
                    && targetPawn != carrier
                    && targetPawn.Spawned
                    && CryoCarryHelper.IsCryoCarryTargetState(targetPawn);
            }
        };
    }
}

#endif // !RIMWORLD16


// =========================================================================
//  RimWorld 1.6:  FloatMenuOptionProvider subclass (auto-discovered)
// =========================================================================

#if RIMWORLD16

/// The game's FloatMenuMakerMap.Init() uses reflection to find every
/// concrete subclass of FloatMenuOptionProvider and instantiates it.
/// Simply existing in a loaded assembly is enough for registration.
public class FloatMenuOptionProvider_CryoRegenesis : FloatMenuOptionProvider
{
    protected override bool Drafted => true;
    protected override bool Undrafted => true;
    protected override bool Multiselect => false;

    /// Provider runs when at least one selected pawn can haul.
    public override bool Applies(FloatMenuContext context)
    {
        // Only offer when the acting pawn(s) are alive and functional.
        foreach (Pawn p in context.ValidSelectedPawns)
        {
            if (!p.Downed && !p.Dead && !p.IsBurning())
                return true;
        }

        return false;
    }

    /// No general (non-target) options.
    public override IEnumerable<FloatMenuOption> GetOptions(
        FloatMenuContext context)
    {
        yield break;
    }

    /// We only care about pawn targets, not thing targets.
    public override bool TargetThingValid(
        Thing thing,
        FloatMenuContext context)
    {
        return false;
    }

    /// Filter clicked pawns to those in a carriable cryo-eligible state.
    public override bool TargetPawnValid(
        Pawn targetPawn,
        FloatMenuContext context)
    {
        return CryoCarryHelper.ShouldAllowCryoRegenesisCarry(targetPawn);
    }

    /// No options for non-pawn things.
    public override IEnumerable<FloatMenuOption> GetOptionsFor(
        Thing thing,
        FloatMenuContext context)
    {
        yield break;
    }

    /// Build carry-to-cryo options for each selected pawn → target pair.
    public override IEnumerable<FloatMenuOption> GetOptionsFor(
        Pawn targetPawn,
        FloatMenuContext context)
    {
        foreach (Pawn carrier in context.ValidSelectedPawns)
        {
            if (carrier.Downed || carrier.Dead || carrier.IsBurning())
                continue;

            if (carrier == targetPawn)
                continue;

            FloatMenuOption option =
                CryoCarryHelper.MakeCryoCarryOption(carrier, targetPawn);

            if (option != null)
                yield return option;
        }
    }
}

#endif // RIMWORLD16
