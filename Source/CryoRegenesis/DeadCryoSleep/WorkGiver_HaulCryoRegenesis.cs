/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * It has been mostly copied from https://github.com/emipa606/DeadCryptosleep/
 *
 * This file is licensed under the MIT License.
 */

using System.Collections.Generic;
using System.Linq;
using BetterRimworlds.CryoRegenesis;
using RimWorld;
using Verse;
using Verse.AI;

namespace FrontierDevelopments.DeadCryptosleep;

public class WorkGiver_HaulCryoRegenesis : WorkGiver_Scanner
{
    public override bool ShouldSkip(Pawn pawn, bool forced = false)
    {
        return !PotentialWorkThingsGlobal(pawn).Any();
    }

    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
    {
        #if RIMWORLD12 || RIMWORLD13
        return pawn.Map.designationManager.allDesignations
            .Where(designation => designation.def == DeadCryosleepDefOf.Deadcryosleep_Haul)
            .Select(designation => designation.target.Thing);
        #else
        return pawn.Map.designationManager.AllDesignations
            .Where(designation => designation.def == DeadCryosleepDefOf.Deadcryosleep_Haul)
            .Select(designation => designation.target.Thing);
        #endif
    }

    public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
    {
        var pod = ClosestValidTarget(pawn.Map, thing.Position);
        var corpse = thing as Corpse;

        return new Job(DeadCryosleepDefOf.HaulCorpseToCryoRegenesisCasket, corpse, pod)
        {
            count = 1
        };
    }

    private static Building_CryoRegenesis ClosestValidTarget(Map map, IntVec3 position)
    {
        return (Building_CryoRegenesis)GenClosest.ClosestThingReachable(
            position,
            map,
            ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial),
            PathEndMode.ClosestTouch,
            TraverseParms.For(TraverseMode.PassDoors),
            validator: thing => thing is Building_CryoRegenesis { HasAnyContents: false });
    }

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        if (t.Map.designationManager.DesignationOn(t, DeadCryosleepDefOf.Deadcryosleep_Haul) == null)
        {
            return false;
        }
        LocalTargetInfo target = t;
        return pawn.CanReserve(target, 1, -1, null, forced) && StrippableUtility.CanBeStrippedByColony(t);
    }
}
