// ==== Source/AllowHostedGuestsPatch.cs ====
using System.Collections.Generic;
using System.Linq;
using BetterRimworlds.CryoRegenesis;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CryoRegenesis.HarmonyPatches
{
    [HarmonyPatch(typeof(FloatMenuMakerMap), "AddHumanlikeOrders")]
    public static class Patch_FloatMenuMakerMap_AddHumanlikeOrders_CryoRegenesis
    {
        public static void Postfix(Vector3 clickPos, Pawn pawn, List<FloatMenuOption> opts)
        {
            if (pawn?.Map == null)
                return;

            // The acting pawn must be capable of receiving an ordered job.
            if (pawn.Downed || pawn.Dead || pawn.IsBurning())
                return;

            Pawn targetPawn = GetClickedDownedPawn(clickPos, pawn.Map);

            if (targetPawn == null)
                return;

            if (!ShouldAllowCryoRegenesisCarry(targetPawn))
                return;

            string label = "CarryToCryoRegenesisCasket".Translate();

            Building_CryoRegenesis casket = FindCryoRegenesisCasketFor(pawn, targetPawn);

            if (casket == null)
            {
                opts.Add(new FloatMenuOption(label + ": " + "NoReachableCryoRegenesis".Translate(), null));
                return;
            }

            if (!pawn.CanReserveAndReach(targetPawn, PathEndMode.Touch, Danger.Deadly))
            {
                opts.Add(new FloatMenuOption(label + ": " + "CannotReach".Translate(targetPawn.LabelShort), null));
                return;
            }

            if (!pawn.CanReserve(casket))
            {
                opts.Add(new FloatMenuOption(label + ": " + "Reserved".Translate(casket.Label), null));
                return;
            }

            FloatMenuOption option = new FloatMenuOption(
                label,
                delegate
                {
                    Job job = JobMaker.MakeJob(CryoRegenesisDefOf.CR_CarryToCryoRegenesis, targetPawn, casket);

                    job.count = 1;

                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                },
                MenuOptionPriority.RescueOrCapture
            );

            opts.Add(FloatMenuUtility.DecoratePrioritizedTask(option, pawn, targetPawn));
        }

        private static Pawn GetClickedDownedPawn(Vector3 clickPos, Map map)
        {
            IntVec3 cell = IntVec3.FromVector3(clickPos);

            if (!cell.InBounds(map))
                return null;

            return cell
                .GetThingList(map)
                .OfType<Pawn>()
                .FirstOrDefault(
                    candidate =>
                        candidate.Downed &&
                        !candidate.Dead &&
                        (
                            candidate.RaceProps.Humanlike ||
                            candidate.RaceProps.Animal
                        )
                );
        }

        private static bool ShouldAllowCryoRegenesisCarry(Pawn targetPawn)
        {
            if (targetPawn == null)
                return false;

            if (targetPawn.Dead || !targetPawn.Downed)
                return false;

            /*
             * Animals.
             *
             * Restricted to the player's faction so the option isn't
             * offered on wild or enemy fauna. Species-level petness is
             * deliberately irrelevant — a tamed warg, thrumbo, or other
             * non-pet species is still eligible when it belongs to the
             * player's faction.
             */
            if (targetPawn.RaceProps.Animal)
                return targetPawn.Faction == Faction.OfPlayer;

            /*
             * Any downed humanlike is eligible — colonists, slaves,
             * prisoners, guests, quest lodgers, and downed hostiles alike.
             * The click already required Downed && !Dead, so this covers
             * every rescue/capture-style scenario, including sedated pawns.
             */
            if (targetPawn.RaceProps.Humanlike)
                return true;

            return false;
        }

        private static Building_CryoRegenesis FindCryoRegenesisCasketFor(Pawn carrier, Pawn targetPawn)
        {
            Map map = carrier?.Map;

            if (map == null || targetPawn == null)
                return null;

            return GenClosest.ClosestThingReachable(
                targetPawn.Position,
                map,
                ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial),
                PathEndMode.InteractionCell,
                TraverseParms.For(carrier, Danger.Deadly, TraverseMode.ByPawn),
                validator: thing =>
                {
                    if (thing is not Building_CryoRegenesis casket)
                        return false;

                    if (casket.HasAnyContents)
                        return false;

                    if (!carrier.CanReserve(casket))
                        return false;

                    return true;
                }
            ) as Building_CryoRegenesis;
        }
    }
}
