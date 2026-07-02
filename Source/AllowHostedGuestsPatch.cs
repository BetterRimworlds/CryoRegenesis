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

            if (pawn.Downed || pawn.Dead || pawn.IsBurning())
                return;

            Pawn targetPawn = GetClickedDownedPawn(clickPos, pawn.Map);

            if (targetPawn == null)
                return;

            if (!ShouldAllowCryoRegenesisCarry(targetPawn))
                return;

            Building_CryptosleepCasket casket = FindCryoRegenesisCasketFor(pawn, targetPawn);

            string label = "Carry to CryoRegenesis casket";

            if (casket == null)
            {
                opts.Add(new FloatMenuOption(
                    label + ": " + "No reachable CryoRegenesis casket",
                    null
                ));

                return;
            }

            if (!pawn.CanReserveAndReach(targetPawn, PathEndMode.Touch, Danger.Deadly))
            {
                opts.Add(new FloatMenuOption(
                    label + ": " + "CannotReach".Translate(targetPawn.LabelShort),
                    null
                ));

                return;
            }

            if (!pawn.CanReserve(casket))
            {
                opts.Add(new FloatMenuOption(
                    label + ": " + "Reserved".Translate(casket.Label),
                    null
                ));

                return;
            }

            opts.Add(FloatMenuUtility.DecoratePrioritizedTask(
                new FloatMenuOption(
                    label,
                    delegate
                    {
                        Job job = JobMaker.MakeJob(
                            JobDefOf.CarryToCryptosleepCasket,
                            targetPawn,
                            casket
                        );

                        job.count = 1;
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    },
                    MenuOptionPriority.RescueOrCapture
                ),
                pawn,
                targetPawn
            ));
        }

        private static Pawn GetClickedDownedPawn(Vector3 clickPos, Map map)
        {
            IntVec3 cell = IntVec3.FromVector3(clickPos);

            if (!cell.InBounds(map))
                return null;

            return cell.GetThingList(map)
                .OfType<Pawn>()
                .FirstOrDefault(p => p.Downed && !p.Dead && p.RaceProps.Humanlike);
        }


        private static bool IsGuest(Pawn pawn)
        {
            #if RIMWORLD12
            return pawn.guest != null
                   && pawn.HostFaction != null
                ;
            #else
            return (pawn.guest.GuestStatus == GuestStatus.Guest || pawn.guest.GuestStatus == GuestStatus.Prisoner);
            #endif
        }

        private static bool ShouldAllowCryoRegenesisCarry(Pawn targetPawn)
        {
            if (targetPawn == null || targetPawn.Dead || !targetPawn.Downed)
                return false;

            // Prisoners.
            if (targetPawn.IsPrisoner)
                return true;

            // Royalty / hospitality / guest-style pawns.
            if (IsGuest(targetPawn))
                return true;

            // Royalty quest lodgers.
            // This exists in newer RimWorld versions. If compiling against an older
            // version, wrap this in #if or reflection.
            if (targetPawn.IsQuestLodger())
                return true;

            return false;
        }

        private static Building_CryptosleepCasket FindCryoRegenesisCasketFor(Pawn carrier, Pawn targetPawn)
        {
            Map map = carrier.Map;

            return GenClosest.ClosestThingReachable(
                targetPawn.Position,
                map,
                ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial),
                PathEndMode.InteractionCell,
                TraverseParms.For(carrier, Danger.Deadly, TraverseMode.ByPawn),
                validator: thing =>
                {
                    if (thing is not Building_CryptosleepCasket casket)
                        return false;

                    if (!IsCryoRegenesisCasket(casket))
                        return false;

                    if (casket.HasAnyContents)
                        return false;

                    if (!carrier.CanReserve(casket))
                        return false;

                    return true;
                }
            ) as Building_CryptosleepCasket;
        }

        private static bool IsCryoRegenesisCasket(Building_CryptosleepCasket casket)
        {
            // Best if your casket has a custom building class.
            return (casket is Building_CryoRegenesis);
        }
    }
}
