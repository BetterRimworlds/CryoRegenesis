/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * It has been mostly copied from https://github.com/emipa606/DeadCryptosleep/
 *
 * This file is licensed under the MIT License.
 */

using System;
using System.Linq;
using BetterRimworlds.CryoRegenesis;
using Verse;

namespace FrontierDevelopments.DeadCryptosleep;

public class Designator_HaulCryoRegenesis : Designator
{
    public Designator_HaulCryoRegenesis()
    {
        // defaultLabel = "BetterRimworlds.CryoRegenesis.Designator.Label".Translate();
        // defaultDesc = "BetterRimworlds.CryoRegenesis.Designator.Desc".Translate();
        defaultLabel = "haul to CryoRegenesis casket";
        defaultDesc  = "haul to CryoRegenesis casket";
        icon = DeadCryosleepDefOf.CryoRegenesisCasket.uiIcon;
        iconAngle = DeadCryosleepDefOf.CryoRegenesisCasket.uiIconAngle;
        iconOffset = DeadCryosleepDefOf.CryoRegenesisCasket.uiIconOffset;
        iconProportions = DeadCryosleepDefOf.CryoRegenesisCasket.graphicData.drawSize.RotatedBy(DeadCryosleepDefOf.CryoRegenesisCasket.defaultPlacingRot);
        iconDrawScale = GenUI.IconDrawScale(DeadCryosleepDefOf.CryoRegenesisCasket);
    }

    #if !RIMWORLD16
    public override int DraggableDimensions => 2;
    #endif

    protected override DesignationDef Designation => DeadCryosleepDefOf.Deadcryosleep_Haul;

    private bool PodsAreAvailable()
    {
        #if RIMWORLD12 || RIMWORLD13
        var designated = Map.designationManager.allDesignations.Count(designation => designation.def == DeadCryosleepDefOf.Deadcryosleep_Haul);
        #else
        var designated = Map.designationManager.AllDesignations.Count(designation => designation.def == DeadCryosleepDefOf.Deadcryosleep_Haul);
        #endif

        var availableCaskets = Map.listerBuildings.allBuildingsColonist
            .OfType<Building_CryoRegenesis>()
            .Count(casket => !casket.HasAnyContents);

        return availableCaskets >= designated;
    }

    public override AcceptanceReport CanDesignateCell(IntVec3 cell)
    {
        if (!cell.InBounds(Map) || cell.Fogged(Map))
            return false;

        try
        {
            CanDesignateThing(
                Map.thingGrid.ThingsListAt(cell)
                    .OfType<Corpse>()
                    .First());
            return true;
        }
        catch (InvalidOperationException)
        {
            // return "BetterRimworlds.CryoRegenesis.Designator.NoCorpses".Translate();
            return "No corpses to haul to cryoregenesis casket";
        }
    }

    public override void DesignateSingleCell(IntVec3 cell)
    {
        cell.GetThingList(Map).OfType<Corpse>().ToList().ForEach(DesignateThing);
    }

    public override AcceptanceReport CanDesignateThing(Thing thing)
    {
        switch (thing)
        {
            case Corpse _:
                return true;
        }

        return "FrontierDevelopments.DeadCryptosleep.Designator.MustBeCorpse".Translate();
    }

    public override void DesignateThing(Thing thing)
    {
        if (CanDesignateThing(thing).Accepted)
        {
            Map.designationManager.AddDesignation(new Designation((LocalTargetInfo) thing, Designation));
        }
    }
}
