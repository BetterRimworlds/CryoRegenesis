// ==== Source/Recipe_AdministerCryoRegenesisSedation.cs ====
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace BetterRimworlds.CryoRegenesis;

public class Recipe_AdministerCryoRegenesisSedation : Recipe_Surgery
{
    private const string NoAvailableCasketKey = "BetterRimworlds.CryoRegenesis.Sedation.NoAvailableCasket";

    public override IEnumerable<BodyPartRecord> GetPartsToApplyOn(Pawn pawn, RecipeDef recipe)
    {
        if (!CanSedatePawn(pawn))
            yield break;

        if (FindEmptyCasket(pawn, null) == null)
            yield break;

        yield return null;
    }

#if RIMWORLD12
    public override bool AvailableOnNow(Thing thing)
    {
        if (!base.AvailableOnNow(thing))
            return false;

        if (thing is not Pawn pawn)
            return false;

        return CanSedatePawn(pawn) && FindEmptyCasket(pawn, null) != null;
    }
#else
    public override bool AvailableOnNow(Thing thing, BodyPartRecord part = null)
    {
        if (!base.AvailableOnNow(thing, part))
            return false;

        if (thing is not Pawn pawn)
            return false;

        return CanSedatePawn(pawn) && FindEmptyCasket(pawn, null) != null;
    }
#endif

    public override void ApplyOnPawn(Pawn pawn, BodyPartRecord part, Pawn billDoer, List<Thing> ingredients, Bill bill)
    {
        Building_CryoRegenesis casket = FindEmptyCasket(pawn, billDoer);
        if (casket == null)
        {
            Messages.Message(NoAvailableCasketKey.Translate(), pawn, MessageTypeDefOf.RejectInput);
            return;
        }

        Hediff hediff = HediffMaker.MakeHediff(
            CryoRegenesisDefOf.CryoRegenesisSedation,
            pawn
        );

        hediff.Severity = hediff.def.initialSeverity;
        pawn.health.AddHediff(hediff);

        // Immediately haul the sedated pawn to the casket we validated before sedation.
        if (billDoer == null)
            return;

        Job job = JobMaker.MakeJob(CryoRegenesisDefOf.CR_CarryToCryoRegenesis, pawn, casket);
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
        billDoer.jobs?.jobQueue?.EnqueueFirst(job, JobTag.Misc);
    }

    private static Building_CryoRegenesis FindEmptyCasket(Pawn pawn, Pawn carrier)
    {
        Map map = pawn?.MapHeld;
        if (map == null)
            return null;

        /*
         * Availability (carrier == null) must NOT require the patient to path
         * to the casket. Prisoners, prison guests, and area-restricted guests
         * often cannot leave their pen, and the whole point of sedation is that
         * a doctor carries them. Only check that an empty colony casket exists.
         *
         * When applying (carrier != null), require the doctor to reserve/reach.
         */
        return map.listerBuildings
            .AllBuildingsColonistOfClass<Building_CryoRegenesis>()
            .Where(c => !c.HasAnyContents)
            .OrderBy(c => c.Position.DistanceToSquared(pawn.PositionHeld))
            .FirstOrDefault(c =>
            {
                if (carrier == null)
                    return true;

                return carrier.CanReserveAndReach(c, PathEndMode.InteractionCell, Danger.Deadly);
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
