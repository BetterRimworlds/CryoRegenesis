// ==== Source/Recipe_AdministerCryoRegenesisSedation.cs ====
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace BetterRimworlds.CryoRegenesis;

public class Recipe_AdministerCryoRegenesisSedation : Recipe_Surgery
{
    public override IEnumerable<BodyPartRecord> GetPartsToApplyOn(Pawn pawn, RecipeDef recipe)
    {
        if (!CanSedatePawn(pawn))
            yield break;

        if (FindEmptyCasket(pawn, null) == null)
            yield break;

        yield return null;
    }

#if !RIMWORLD12
    public override bool AvailableOnNow(Thing thing, BodyPartRecord part = null)
    {
        if (!base.AvailableOnNow(thing, part))
            return false;

        if (thing is not Pawn pawn)
            return false;

        return CanSedatePawn(pawn) && FindEmptyCasket(pawn, null) != null;
    }
#endif

    public override void ApplyOnPawn(
        Pawn pawn,
        BodyPartRecord part,
        Pawn billDoer,
        List<Thing> ingredients,
        Bill bill
    )
    {
        Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(
            CryoRegenesisDefOf.CryoRegenesisSedation
        );

        if (existing != null)
        {
            existing.Severity = existing.def.initialSeverity;
        }
        else
        {
            Hediff hediff = HediffMaker.MakeHediff(
                CryoRegenesisDefOf.CryoRegenesisSedation,
                pawn
            );

            hediff.Severity = hediff.def.initialSeverity;
            pawn.health.AddHediff(hediff);
        }

        // Immediately haul the sedated pawn to an empty casket.
        if (billDoer == null)
            return;

        Building_CryoRegenesis casket = FindEmptyCasket(pawn, billDoer);
        if (casket == null)
            return;

        Job job = JobMaker.MakeJob(
            CryoRegenesisDefOf.CR_CarryToCryoRegenesis,
            pawn,
            casket
        );
        job.count = 1;

        /*
         * Do NOT StartJob(InterruptForced) here: ApplyOnPawn runs inside the
         * doctor's DoBill job, whose reservations on the patient are still
         * held. Force-starting the carry job races those reservations and
         * TryMakePreToilReservations can fail silently.
         *
         * Enqueueing instead makes the carry job start the instant the bill
         * job finishes and releases its reservations.
         */
        billDoer.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
    }

    private static Building_CryoRegenesis FindEmptyCasket(Pawn pawn, Pawn carrier)
    {
        Map map = pawn?.MapHeld;
        if (map == null)
            return null;

        return map.listerBuildings
            .AllBuildingsColonistOfClass<Building_CryoRegenesis>()
            .Where(c => !c.HasAnyContents)
            .OrderBy(c => c.Position.DistanceToSquared(pawn.PositionHeld))
            .FirstOrDefault(c =>
            {
                if (carrier != null)
                    return carrier.CanReserveAndReach(c, PathEndMode.InteractionCell, Danger.Deadly);

                return pawn.CanReach(c, PathEndMode.InteractionCell, Danger.Deadly);
            });
    }

    private static bool CanSedatePawn(Pawn pawn)
    {
        if (pawn == null)
            return false;

        if (pawn.Dead || pawn.Destroyed)
            return false;

        if (pawn.health == null || pawn.health.hediffSet == null)
            return false;

        // Humanlikes are always eligible; animals only if they're an owned
        // colony pet (tamed, faction == player). Species-level "petness" is
        // irrelevant here — wargs/thrumbos have petness 0 but can be pets.
        if (!pawn.RaceProps.Humanlike &&
            !(pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer))
            return false;

        if (pawn.health.hediffSet.HasHediff(CryoRegenesisDefOf.CryoRegenesisSedation))
            return false;

        return true;
    }
}