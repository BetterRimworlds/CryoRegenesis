// ==== Source/AllowHostedGuestsPatch.cs ====
/*
 * Carry-to-CryoRegenesis float menu (FloatMenuMakerMap.AddHumanlikeOrders).
 *
 * =============================================================================
 * INCIDENT: option missing for unconscious / Downed prisoners (RW 1.2)
 * =============================================================================
 *
 * What players saw:
 *   Right-click on an unconscious prisoner (Moving 0%, Downed) never offered
 *   "Carry to CryoRegenesis casket", even though the feature was meant for every
 *   immobilised humanlike / colony animal.
 *
 * What was *not* the primary bug (but still worth hardening):
 *   - Cell-only GetThingList lookups miss bed occupants → use GenUI.TargetsAt
 *     plus a small cell neighbourhood scan.
 *   - Strict CanReserveAndReach without ignoreOtherReservations greys out
 *     tended patients → do not gate the menu on path/reserve probes; let the
 *     job fail at runtime if needed.
 *   - Carriable state is broader than `pawn.Downed` alone: also treat Moving
 *     capacity at 0% (and anesthetic / CryoRegenesis sedation) as eligible so
 *     edge ticks and UI "Moving 0%" match player expectations.
 *
 * What *was* the primary bug:
 *   This patch never ran on 1.2. Harmony PatchAll aborted because another
 *   assembly patch targeted a DoRecruit signature that does not exist on 1.2.
 *   See the Harmony loading docblock in CryoRegenesis.cs and TargetMethod
 *   fallbacks on Patch_DoRecruit_RegenContract.
 *
 * How not to repeat this:
 *   - If this option vanishes on one RW version only, verify the patch is
 *     applied (log + Harmony) before changing IsCryoCarryTargetState again.
 *   - Keep IsCryoCarryTargetState = Downed OR Moving <= 0% OR sedation hediffs.
 *   - Keep casket lookup free of pathing/reservation requirements for the menu.
 *   - Multi-version: AddHumanlikeOrders exists through 1.5; 1.6 uses a different
 *     float-menu pipeline and needs a separate approach if support is required.
 * =============================================================================
 */

using System.Collections.Generic;
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
        public static void Postfix(
            Vector3 clickPos,
            Pawn pawn,
            List<FloatMenuOption> opts)
        {
            if (pawn?.Map == null)
                return;

            // The acting pawn must be capable of receiving an ordered job.
            if (pawn.Downed || pawn.Dead || pawn.IsBurning())
                return;

            /*
             * Collect candidate patients under the cursor.
             *
             * GenUI.TargetsAt finds multi-cell bed occupants that a single
             * GetThingList(cell) can miss. A cell scan is kept as a fallback
             * for edge cases (e.g. draw-pos slightly off the click).
             *
             * Intentionally NO pathing / CanReserveAndReach checks here.
             * Prisoners in locked rooms, guests with area restrictions, and
             * bed-bound patients often fail reachability probes even when a
             * colonist can still walk over and carry them. The job/toils
             * handle real pathing at runtime.
             */
            foreach (Pawn targetPawn in GetCryoCarryCandidates(clickPos, pawn))
            {
                if (!ShouldAllowCryoRegenesisCarry(targetPawn))
                    continue;

                string label = "CarryToCryoRegenesisCasket".Translate();

                Building_CryoRegenesis casket =
                    FindEmptyCryoRegenesisCasket(targetPawn);

                if (casket == null)
                {
                    opts.Add(
                        new FloatMenuOption(
                            label
                            + ": "
                            + "NoReachableCryoRegenesis".Translate(),
                            null));

                    continue;
                }

                // Capture for the menu-option delegate.
                Pawn patient = targetPawn;

                FloatMenuOption option = new FloatMenuOption(
                    label,
                    delegate
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

                        pawn.jobs.TryTakeOrderedJob(
                            job,
                            JobTag.Misc);
                    },
                    MenuOptionPriority.RescueOrCapture);

                opts.Add(
                    FloatMenuUtility.DecoratePrioritizedTask(
                        option,
                        pawn,
                        targetPawn));
            }
        }

        private static IEnumerable<Pawn> GetCryoCarryCandidates(
            Vector3 clickPos,
            Pawn carrier)
        {
            var seen = new HashSet<Pawn>();

            TargetingParameters targetingParameters =
                MakeCryoCarryTargetingParameters(carrier);

#if RIMWORLD12
            // RimWorld 1.2 prefers TargetsAt_NewTemp; TargetsAt is obsolete there.
            IEnumerable<LocalTargetInfo> targets = GenUI.TargetsAt_NewTemp(
                clickPos,
                targetingParameters,
                thingsOnly: true);
#else
            IEnumerable<LocalTargetInfo> targets = GenUI.TargetsAt(
                clickPos,
                targetingParameters,
                thingsOnly: true);
#endif

            foreach (LocalTargetInfo target in targets)
            {
                if (target.Thing is Pawn targetPawn && seen.Add(targetPawn))
                    yield return targetPawn;
            }

            // Fallback: direct cell scan (covers some bed / multi-cell cases).
            Map map = carrier.Map;
            IntVec3 cell = IntVec3.FromVector3(clickPos);

            if (!cell.InBounds(map))
                yield break;

            for (int x = -1; x <= 1; x++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    IntVec3 scanCell = new IntVec3(cell.x + x, cell.y, cell.z + z);

                    if (!scanCell.InBounds(map))
                        continue;

                    List<Thing> things = scanCell.GetThingList(map);

                    for (int i = 0; i < things.Count; i++)
                    {
                        if (things[i] is Pawn targetPawn &&
                            seen.Add(targetPawn) &&
                            IsCryoCarryTargetState(targetPawn))
                        {
                            yield return targetPawn;
                        }
                    }
                }
            }
        }

        /*
         * Common-denominator replacement for TargetingParameters.ForRescue.
         *
         * Also accepts pawns under Anesthetic / CryoRegenesis sedation who may
         * briefly not report as Downed (severity edge, just-applied hediff).
         * ForRescue's onlyTargetIncapacitatedPawns would miss those.
         */
        private static TargetingParameters MakeCryoCarryTargetingParameters(
            Pawn carrier)
        {
            return new TargetingParameters
            {
                canTargetLocations = false,
                canTargetSelf = false,

                canTargetPawns = true,
                canTargetHumans = true,
                canTargetAnimals = true,

                canTargetBuildings = false,
                canTargetItems = false,

                // Do NOT set onlyTargetIncapacitatedPawns: anesthetized
                // prisoners can fail that gate at certain severity edges.
                // Our validator handles Downed + sedation explicitly.
                onlyTargetIncapacitatedPawns = false,
                mustBeSelectable = false,
                mapObjectTargetsMustBeAutoAttackable = false,

                validator = target =>
                {
                    if (!target.HasThing)
                        return false;

                    Pawn targetPawn = target.Thing as Pawn;

                    return targetPawn != null
                        && targetPawn != carrier
                        && targetPawn.Spawned
                        && IsCryoCarryTargetState(targetPawn);
                }
            };
        }

        /// True when the pawn is carriable: Downed, Moving at 0%, or sedated
        /// and about to collapse. JobDriver waits for Downed after sedation.
        private static bool IsCryoCarryTargetState(Pawn targetPawn)
        {
            if (targetPawn == null || targetPawn.Dead)
                return false;

            // Standard incapacitated flag (unconscious, pain shock, etc.).
            if (targetPawn.Downed)
                return true;

            // Moving 0% — can't walk even if Downed hasn't flipped yet.
            PawnCapacitiesHandler capacities = targetPawn.health?.capacities;
            if (capacities != null &&
                (!capacities.CapableOf(PawnCapacityDefOf.Moving) ||
                 capacities.GetLevel(PawnCapacityDefOf.Moving) <= 0f))
            {
                return true;
            }

            // Full Anesthetic or our cryo sedation (pending collapse).
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

        private static bool ShouldAllowCryoRegenesisCarry(Pawn targetPawn)
        {
            if (targetPawn == null)
                return false;

            if (!IsCryoCarryTargetState(targetPawn))
                return false;

            /*
             * Animals.
             *
             * Restricted to the player's faction so the option isn't offered
             * on wild or enemy fauna. Species-level petness is deliberately
             * irrelevant: a tamed warg, thrumbo, or other non-pet species is
             * still eligible when it belongs to the player's faction.
             */
            if (targetPawn.RaceProps.Animal)
                return targetPawn.Faction == Faction.OfPlayer;

            /*
             * Any qualifying humanlike is eligible: colonists, slaves,
             * prisoners (including anesthetized quest prisoners), guests,
             * quest lodgers, and downed hostiles alike.
             */
            if (targetPawn.RaceProps.Humanlike)
                return true;

            return false;
        }

        /*
         * Nearest empty colony CryoRegenesis casket. Intentionally no pathing
         * or reservation checks — those are left to the job/toils at runtime.
         */
        private static Building_CryoRegenesis FindEmptyCryoRegenesisCasket(
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
                if (buildings[i] is not Building_CryoRegenesis casket)
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
    }
}
