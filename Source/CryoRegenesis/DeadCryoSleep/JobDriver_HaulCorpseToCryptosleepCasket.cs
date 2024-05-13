/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * It has been mostly copied from https://github.com/emipa606/DeadCryptosleep/
 *
 * This file is licensed under the MIT License.
 */

using System.Collections.Generic;
using BetterRimworlds.CryoRegenesis;
using Verse;
using Verse.AI;

namespace FrontierDevelopments.DeadCryptosleep.Jobs;

public class JobDriver_HaulCorpseToCryoRegenesisCasket : JobDriver
{
    private const TargetIndex CorpseIndex = TargetIndex.A;
    private const TargetIndex CryoRegenesisCasketIndex = TargetIndex.B;

    private Corpse Corpse => (Corpse)this.job.GetTarget(CorpseIndex).Thing;

    private Building_CryoRegenesis CryoRegenesisCasket =>
        (Building_CryoRegenesis)this.job.GetTarget(CryoRegenesisCasketIndex).Thing;

    public override bool TryMakePreToilReservations(bool errorOnFailed)
    {
        return pawn.Reserve(job.GetTarget(CryoRegenesisCasketIndex), job, 1, -1, null, errorOnFailed)
               && pawn.Reserve(job.GetTarget(CorpseIndex), job, 1, -1, null, errorOnFailed);
    }

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDestroyedOrNull(CorpseIndex);
        this.FailOnDestroyedOrNull(CryoRegenesisCasketIndex);
        this.FailOn(() => !CryoRegenesisCasket.Accepts(Corpse));
        yield return Toils_Goto
            .GotoThing(CorpseIndex, PathEndMode.OnCell)
            .FailOnDestroyedNullOrForbidden(TargetIndex.A)
            .FailOnDespawnedNullOrForbidden(TargetIndex.B)
            .FailOn(() => CryoRegenesisCasket.HasAnyContents)
            .FailOn(() => !pawn.CanReach(Corpse, PathEndMode.OnCell, Danger.Deadly))
            .FailOnSomeonePhysicallyInteracting(TargetIndex.A);
        yield return Toils_Haul.StartCarryThing(CorpseIndex);
        yield return Toils_Goto.GotoThing(CryoRegenesisCasketIndex, PathEndMode.InteractionCell);
        yield return Toils_General
            .Wait(500)
            .FailOnCannotTouch(CryoRegenesisCasketIndex, PathEndMode.InteractionCell)
            .WithProgressBarToilDelay(CryoRegenesisCasketIndex);
        yield return new Toil
        {
            initAction = () => CryoRegenesisCasket.TryAcceptThing(Corpse),
            defaultCompleteMode = ToilCompleteMode.Instant
        };
    }

}
