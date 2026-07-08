// ==== Source/JobDriver_CarryToCryoRegenesis.cs ====
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace BetterRimworlds.CryoRegenesis;

public class JobDriver_CarryToCryoRegenesis : JobDriver
{
    private const TargetIndex PatientIndex = TargetIndex.A;
    private const TargetIndex CasketIndex = TargetIndex.B;

    /**
     * How long (in ticks) to wait at the patient's side for sedation to
     * down them before giving up. 600 ticks = ~10 in-game seconds.
     */
    private const int MaxWaitForDownedTicks = 600;

    private Pawn Patient => job.GetTarget(PatientIndex).Pawn;

    private Building_CryoRegenesis Casket =>
        job.GetTarget(CasketIndex).Thing as Building_CryoRegenesis;

    public override bool TryMakePreToilReservations(bool errorOnFailed)
    {
        return pawn.Reserve(
                   Patient,
                   job,
                   1,
                   -1,
                   null,
                   errorOnFailed
               )
               &&
               pawn.Reserve(
                   Casket,
                   job,
                   1,
                   -1,
                   null,
                   errorOnFailed
               );
    }

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDestroyedNullOrForbidden(PatientIndex);
        this.FailOnDestroyedNullOrForbidden(CasketIndex);

        /*
         * Deliberately no FailOn(!Patient.Downed) here: a freshly sedated
         * human may still be standing for a few ticks. The wait toil below
         * handles that window.
         */
        this.FailOn(() =>
            Patient == null ||
            Patient.Dead ||
            Casket == null ||
            Casket.HasAnyContents
        );

        yield return Toils_Goto.GotoThing(
            PatientIndex,
            PathEndMode.Touch
        );

        /*
         * Sedation may take a few ticks to down a large pawn (humans have a
         * higher effective consciousness threshold than small animals).
         * Wait at the patient's side until they collapse, with a safety
         * valve so the carrier doesn't stand there forever if the hediff
         * never downs them.
         */
        Toil waitForDowned = new Toil
        {
            defaultCompleteMode = ToilCompleteMode.Never,
            defaultDuration = MaxWaitForDownedTicks,
        };

        waitForDowned.initAction = () =>
        {
            // Already downed? Skip the wait entirely.
            if (Patient != null && Patient.Downed)
                waitForDowned.actor.jobs.curDriver.ReadyForNextToil();
        };

        waitForDowned.tickAction = () =>
        {
            if (Patient != null && Patient.Downed)
                waitForDowned.actor.jobs.curDriver.ReadyForNextToil();
        };

        waitForDowned.AddFailCondition(() =>
            waitForDowned.actor.jobs.curDriver.ticksLeftThisToil <= 0 &&
            (Patient == null || !Patient.Downed)
        );

        yield return waitForDowned;

        yield return Toils_Haul.StartCarryThing(
            PatientIndex,
            false,
            false,
            false
        );

        yield return Toils_Goto.GotoThing(
            CasketIndex,
            PathEndMode.InteractionCell
        );

        /*
         * Deposit via the casket's own TryAcceptThing override rather than
         * the generic DepositHauledThingInContainer toil.
         *
         * The vanilla deposit toil moves the pawn straight into the casket's
         * ThingOwner without routing through Building_CryoRegenesis.
         * TryAcceptThing — so BeginNewPawn()/ConfigureTargetAge() and the
         * true-age snapshot never run, and the pawn inherits the previous
         * occupant's stale cycle state (wrong OriginalAge/TargetAge, instant
         * ejection). Calling TryAcceptThing explicitly guarantees the accept
         * path — and cycle initialization — runs exactly once.
         */
        Toil deposit = new Toil
        {
            defaultCompleteMode = ToilCompleteMode.Instant,
        };
        deposit.initAction = () =>
        {
            Pawn actor = deposit.actor;
            Building_CryoRegenesis casket = Casket;
            Pawn patient = Patient;

            if (casket == null || patient == null)
                return;

            // The carrier is currently holding the patient; hand them to the
            // casket through its accept override. TryAcceptThing pulls the
            // pawn out of the carrier's ThingOwner itself.
            if (!casket.TryAcceptThing(patient))
            {
                Log.Warning(
                    "[CryoRegenesis] Casket refused patient "
                    + patient.LabelShort
                    + "; dropping carried pawn instead."
                );

                // Fallback: don't strand the pawn inside the carrier.
                if (actor.carryTracker?.CarriedThing == patient)
                {
                    actor.carryTracker.TryDropCarriedThing(
                        actor.Position,
                        ThingPlaceMode.Near,
                        out _
                    );
                }
            }
        };
        deposit.AddFinishAction(() =>
        {
            // If somehow the pawn is still carried after accept, drop them
            // so they aren't lost inside the carrier.
            Pawn actor = deposit.actor;
            if (actor.carryTracker?.CarriedThing is Pawn stillCarried &&
                stillCarried == Patient)
            {
                actor.carryTracker.TryDropCarriedThing(
                    actor.Position,
                    ThingPlaceMode.Near,
                    out _
                );
            }
        });

        yield return deposit;
    }
}