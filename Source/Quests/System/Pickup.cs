/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Verse.AI.Group;

#if RIMWORLD15 || RIMWORLD16
using LudeonTK;
#endif

namespace BetterRimworlds.CryoRegenesis;

/// Pickup shuttle spawn/board, contract finish/fail, and guest-lodger bookkeeping.
public partial class RoyaltyRegenesisQuestSystem
{
    /// Living non-regen escorts still on the map (Emperor-stage guards, etc.).
    private List<Pawn> GetMapHeldEscorts()
    {
        if (this.contractEscorts == null || !this.contractEscorts.Any())
        {
            return new List<Pawn>();
        }

        return this.contractEscorts
            .Where(p => p != null && !p.Destroyed && !p.Dead && this.IsClientAvailableOnMap(p))
            .Distinct()
            .ToList();
    }

    /// Keep contracted guests (and escorts) in their home faction as guests of the
    /// colony — the same arrangement as Imperial nobles. Joining a visiting ruler
    /// to the player faction makes them a colonist, and their home faction then
    /// appoints a replacement leader.
    private void EnsureGuestClientsAreQuestLodgers()
    {
        if (this.pickupShuttleSpawned || this.activeContractQuest == null || this.activeContractQuest.Historical)
        {
            return;
        }

        List<Pawn> pawns = this.activeClients
            .Where(client => client != null && !client.isPrisoner && client.pawn != null)
            .Select(client => client.pawn)
            .Distinct()
            .ToList();

        if (this.contractEscorts != null)
        {
            pawns.AddRange(
                this.contractEscorts.Where(p => p != null && !p.Destroyed && !pawns.Contains(p)));
        }

        foreach (Pawn pawn in pawns)
        {
            RoyaltyRegenesisClient client = this.activeClients.FirstOrDefault(c => c?.pawn == pawn);
            Faction homeFaction = client?.sourceFaction
                ?? this.activeClients.FirstOrDefault()?.sourceFaction
                ?? pawn.Faction;

            this.KeepPawnAsHomeFactionGuest(pawn, homeFaction);
        }
    }

    /// Home-faction guest of the colony. Never joins the player faction.
    /// For a current faction leader, ExtraFaction HomeFaction is reserved first so a
    /// later SetFaction to the player cannot make their polity appoint a replacement.
    private void KeepPawnAsHomeFactionGuest(Pawn pawn, Faction homeFaction)
    {
        if (pawn == null || pawn.Destroyed)
        {
            return;
        }

        if (homeFaction == null || homeFaction == Faction.OfPlayer)
        {
            homeFaction = pawn.Faction;
        }

        if (homeFaction != null && homeFaction != Faction.OfPlayer && homeFaction.leader == pawn)
        {
            this.ReserveHomeFactionForLeader(pawn, homeFaction);
        }

        if (homeFaction != null && homeFaction != Faction.OfPlayer && pawn.Faction != homeFaction)
        {
            this.ReleaseFromCurrentLord(pawn);
            pawn.SetFaction(homeFaction);
        }

        if (this.CanBeGuestOfPlayer(pawn) && pawn.HostFaction != Faction.OfPlayer)
        {
            this.ApplyGuestOrPrisonerStatus(pawn, isPrisoner: false);
        }

        this.LockRecruitment(pawn);
    }

    /// Quest ExtraFaction must be on the manager cache (not only the quest part
    /// list) or HasExtraHomeFaction stays false and SetFaction(OfPlayer) still
    /// makes the home faction replace its leader.
    private void ReserveHomeFactionForLeader(Pawn pawn, Faction homeFaction)
    {
        if (this.activeContractQuest == null || pawn == null || homeFaction == null)
        {
            return;
        }

        QuestPart_ExtraFaction part = this.activeContractQuest.PartsListForReading
            .OfType<QuestPart_ExtraFaction>()
            .FirstOrDefault(candidate =>
                candidate.extraFaction != null
                && candidate.extraFaction.faction == homeFaction
                && candidate.extraFaction.factionType == ExtraFactionType.HomeFaction);

        if (part == null)
        {
            part = new QuestPart_ExtraFaction
            {
                extraFaction = new ExtraFaction(homeFaction, ExtraFactionType.HomeFaction),
                affectedPawns = new List<Pawn>(),
            };
            this.activeContractQuest.AddPart(part);
            List<QuestPart_ExtraFaction> cache = Find.QuestManager?.ExtraFactionQuestParts;
            if (cache != null && !cache.Contains(part))
            {
                cache.Add(part);
            }
        }

        if (part.affectedPawns == null)
        {
            part.affectedPawns = new List<Pawn>();
        }

        if (!part.affectedPawns.Contains(pawn))
        {
            part.affectedPawns.Add(pawn);
        }
    }

    /// Returns any leftover player-faction guest conversions to their home faction
    /// after the contract ends. Must run while client references still exist
    /// (before <see cref="activeClients"/> is cleared).
    ///
    /// Do NOT call this at boarding time: SetFaction away from the player mid-leave
    /// can break ExitOnShuttle / CompShuttle loading and soft-fail successful contracts.
    private void RestoreGuestClientFactions()
    {
        foreach (RoyaltyRegenesisClient client in this.activeClients.Where(client => client != null && !client.isPrisoner))
        {
            Pawn pawn = client.pawn;
            if (pawn == null || pawn.Destroyed || client.sourceFaction == null)
            {
                continue;
            }

            if (pawn.Faction != Faction.OfPlayer)
            {
                continue;
            }

            // ExtraFaction first if they are still that polity's leader, then home
            // faction. SetFaction(OfPlayer) without ExtraFaction is what makes a
            // visiting ruler get replaced.
            if (client.sourceFaction.leader == pawn)
            {
                this.ReserveHomeFactionForLeader(pawn, client.sourceFaction);
            }

            // Drop any leave/boarding lord first so SetFaction does not race with it.
            this.ReleaseFromCurrentLord(pawn);
            pawn.SetFaction(client.sourceFaction);
            // Do not re-apply Guest of player — they are going home, not visiting.
            // ApplyGuestOrPrisonerStatus here recreated a visitor state that left
            // rulers showing as Town Councilman while still stuck in guest AI.
        }
    }

    private void ReleaseFromCurrentLord(Pawn pawn)
    {
        pawn?.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ForcedToJoinOtherLord);
    }

    /// Finalizes the active contract quest on shuttle departure (or equivalent leave).
    /// Success if ages were banked/recorded or multi-wave returns covered the full party;
    /// otherwise soft-fails without a full chain reset.
    private void FinishContractAfterDeparture(
        bool success,
        string failLabel = null,
        string failText = null)
    {
        // Nothing left to settle (already finalized).
        if (this.activeContractStage == RoyaltyRegenesisStage.NotStarted
            && this.activeContractQuest == null
            && !this.activeClients.Any())
        {
            this.ClearContractShuttle();
            this.ClearContractTiming();
            this.LogRoyaltyDebug("FinishContractAfterDeparture: nothing to finalize.");
            return;
        }

        // Final re-evaluation from banked flags and any clients still referenced.
        this.RefreshClientAgeProgress();

        // Multi-wave accounting: every original client must have returned after reaching
        // their target. Do not let a final-wave banked record wipe an earlier unfinished leaver.
        bool multiWave = this.clientsSuccessfullyReturned > 0
            || this.completionRewardClientCount > this.activeClients.Count;
        if (multiWave)
        {
            bool waveSuccess = this.clientsSuccessfullyReturned >= Math.Max(1, this.completionRewardClientCount);
            if (waveSuccess != success)
            {
                this.LogRoyaltyDebug(
                    "Finish multi-wave success: arg=" + success + " → returned="
                    + this.clientsSuccessfullyReturned + "/" + this.completionRewardClientCount
                    + " → " + waveSuccess);
            }

            success = waveSuccess;
        }
        else
        {
            bool recordedSuccess = this.IsDepartureSuccessBanked()
                || (this.activeClients.Any()
                    && this.activeClients.All(c => c != null && c.everReachedDesiredAge));
            if (recordedSuccess != success)
            {
                this.LogRoyaltyDebug(
                    "Finish success override: arg=" + success + " → recorded=" + recordedSuccess);
                success = recordedSuccess;
            }
        }

        RoyaltyRegenesisStage finishedStage = this.activeContractStage;
        bool triggerEndgame = this.activeClients.Any(client => client != null && client.triggerRoyalAscent);

        // Safety net for Branch 3 only when the Emperor himself is no longer on the map
        // (he left with the shuttle). Never fire Imperial Court for an abandoned Emperor.
        if (finishedStage == RoyaltyRegenesisStage.EmperorArrival
            && !this.emperorColonistEndgameTriggered
            && !this.IsEmperorInUnpoweredCryoCasket()
            && !this.IsEmperorStillAvailableOnMap())
        {
            this.TryTriggerEmperorColonistEndgameFromSnapshot("finish contract with Emperor gone");
        }

        this.LogRoyaltyDebug(
            "FinishContractAfterDeparture success=" + success
            + " stage=" + finishedStage
            + " clients=" + this.activeClients.Count
            + " triggerRoyalAscent=" + triggerEndgame
            + " emperorColonistEndgame=" + this.emperorColonistEndgameTriggered
            + " banked=" + this.departureSuccessBanked
            + " completionLatched=" + this.contractCompletionLogged
            + " allReady=" + this.pickupAllClientsReady
            + " returned=" + this.clientsSuccessfullyReturned
            + "/" + this.completionRewardClientCount);
        this.LogClientAgeSnapshot("finish");

        // Return lodgers to their home faction while client pawn refs still exist.
        // EndActiveContractQuest also calls Restore as a safety net, but activeClients
        // is cleared below so it must happen here first.
        this.RestoreGuestClientFactions();

        this.ClearContractFlags(this.activeClients);
        this.activeClients.Clear();
        this.ClearContractShuttle();
        this.ClearContractTiming();

        if (triggerEndgame && success)
        {
            this.LogRoyaltyDebug(
                this.emperorColonistEndgameTriggered
                    ? "Outcome: SUCCESS + Imperial Court colonist endgame (shuttle departed)."
                    : "Outcome: SUCCESS + Royal Ascent endgame (shuttle departed).");
            this.activeContractStage = finishedStage;
            this.GrantCompletionRewardIfEligible(finishedStage);
            this.EndActiveContractQuest(QuestEndOutcome.Success);
            this.stage = RoyaltyRegenesisStage.Completed;
            this.royalAscentTriggered = true;
            this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
            this.CompleteChainQuest();
            // Colonists who left with the Emperor already got the victory ending —
            // do not also open the vanilla Royal Ascent visit quest.
            if (!this.emperorColonistEndgameTriggered)
            {
                this.TryMakeRoyalAscentAvailable();
            }

            return;
        }

        if (success)
        {
            this.LogRoyaltyDebug("Outcome: SUCCESS — CompleteActiveContract (shuttle departed).");
            this.activeContractStage = finishedStage;
            this.GrantCompletionRewardIfEligible(finishedStage);
            this.EndActiveContractQuest(QuestEndOutcome.Success);
            this.CompleteActiveContract();
        }
        else
        {
            // Soft failure: contract fails, stage progress kept, no full chain reset.
            this.LogRoyaltyDebug("Outcome: FAIL — shuttle departed without banked age targets.");
            this.EndActiveContractQuest(QuestEndOutcome.Fail);
            this.ScheduleRetry(
                failLabel ?? "CryoRegenesis contract expired",
                failText
                ?? "The shuttle departed before the requested regression was finished. Another attempt may come later.");
        }
    }

    private void FailContractAfterPickupTimeout()
    {
        // The 3-day boarding window is logistics only. Soft-fail only when the real
        // contract deadline has passed (or nobody is left to keep treating).
        List<RoyaltyRegenesisClient> stillHere = this.activeClients
            .Where(c => c?.pawn != null && !c.pawn.Destroyed && !c.pawn.Dead
                && this.IsClientAvailableOnMap(c.pawn))
            .ToList();
        int readyHere = stillHere.Count(c => c.everReachedDesiredAge);
        bool deadlineReached = this.IsContractDeadlineReached();

        this.LogRoyaltyDebug(
            "Pickup window expired. stillHere=" + stillHere.Count
            + " readyHere=" + readyHere
            + " banked=" + this.IsDepartureSuccessBanked()
            + " deadlineReached=" + deadlineReached
            + " returned=" + this.clientsSuccessfullyReturned
            + "/" + this.completionRewardClientCount);

        // Ready clients missed the ship — keep the contract and call another pickup.
        if (stillHere.Any() && (readyHere > 0 || this.IsDepartureSuccessBanked()))
        {
            this.EndPickupWindowKeepContract(
                "Pickup window expired with " + readyHere + " ready client(s) still on map — "
                + "requesting follow-up shuttle.");
            this.RequestFollowUpPickupForRemaining("boarding window expired with ready clients");
            return;
        }

        // Unfinished clients only, still before the real return date: dismiss the empty
        // boarding attempt and keep regenerating. Do not soft-fail or re-arm a 3-day clock.
        if (stillHere.Any() && !deadlineReached)
        {
            this.EndPickupWindowKeepContract(
                "Pickup window expired mid-treatment before contract deadline — "
                + "dismissing ship, continuing regeneration.");
            Find.LetterStack.ReceiveLetter(
                "Regenesis pickup left",
                "The pickup shuttle left while CryoRegenesis clients were still being treated. "
                + "The contract continues until the scheduled return date. "
                + "Another shuttle will come when remaining clients are ready or the deadline arrives.",
                LetterDefOf.NeutralEvent,
                new LookTargets(stillHere.Select(c => c.pawn)));
            return;
        }

        // Ages banked → still succeed when the loading window ends without everyone aboard.
        if (this.IsDepartureSuccessBanked())
        {
            this.LogRoyaltyDebug(
                "Pickup window expired after success was banked — waiting for real shuttle departure.");
            return;
        }

        // Past the real deadline (or no one left on the map) without a successful return.
        this.LogRoyaltyDebug("FailContractAfterPickupTimeout — deadline passed / clients not returned.");
        this.LogClientAgeSnapshot("pickup timeout fail");
        this.FinishContractAfterDeparture(
            false,
            "CryoRegenesis return missed",
            "The shuttle left before every CryoRegenesis client finished treatment and returned. "
            + "Another attempt may come later.");
    }

    /// Clears pickup-mode flags so the 3-day boarding window cannot soft-fail the contract
    /// while treatment continues under the real deadline.
    private void EndPickupWindowKeepContract(string debugReason)
    {
        this.LogRoyaltyDebug(debugReason);
#if !RIMWORLD12
        // A long-stay pickup is waiting forever. Dismiss it before dropping our reference
        // so it cannot remain parked while a later pickup is spawned.
        this.contractTransportShip?.ForceJob(ShipJobDefOf.FlyAway);
#endif
        this.ClearContractShuttle();
        this.pickupShuttleSpawned = false;
        this.pickupShuttleSpawnTick = -1;
        this.pickupWindowEndTick = -1;
        // Do not leave nextWavePickupTick set unless the caller schedules one.
        // Clearing an in-progress pickup must not keep a stale wave timer from earlier.
        if (this.nextWavePickupTick > 0
            && Find.TickManager.TicksGame >= this.nextWavePickupTick)
        {
            this.nextWavePickupTick = -1;
        }
    }

    private bool IsContractDeadlineReached()
    {
        if (this.contractDeadlineTick <= 0)
        {
            return false;
        }

        int now = Find.TickManager.TicksGame;

        // Never fire before the minimum stay after contract start.
        if (this.contractStartTick >= 0)
        {
            int minDeadline = this.contractStartTick + MinContractDays * GenDate.TicksPerDay;
            if (now < minDeadline)
            {
                return false;
            }
        }

        return now >= this.contractDeadlineTick;
    }

    /// True when the client is map-reachable (spawned, in a casket, or walking) — not still
    /// aboard an incoming delivery shuttle / skyfaller.
    private bool IsClientAvailableOnMap(Pawn pawn)
    {
        if (pawn == null || pawn.Destroyed || pawn.Dead)
        {
            return false;
        }

        if (pawn.Spawned)
        {
            return true;
        }

        // Inside a player cryo casket counts as "on map" for contract progress.
        if (pawn.ParentHolder is Building_CryoRegenesis)
        {
            return true;
        }

        // Still in shuttle/transporter/skyfaller — wait for unload.
        return false;
    }

    private bool IsClientInReturnTransport(Pawn pawn)
    {
        if (pawn == null || pawn.Destroyed || pawn.Dead)
        {
            return false;
        }

        return !this.IsClientAvailableOnMap(pawn);
    }

    private bool PickupLoadingWindowExpired()
    {
        if (!this.pickupShuttleSpawned || this.pickupShuttleSpawnTick < 0)
        {
            return false;
        }

        int endTick = this.pickupWindowEndTick > 0
            ? this.pickupWindowEndTick
            : this.pickupShuttleSpawnTick + this.GetReadyPickupStayTicks();
        return Find.TickManager.TicksGame >= endTick;
    }

    private void BeginPickupWindow(bool longStay)
    {
        this.pickupShuttleSpawned = true;
        this.pickupShuttleSpawnTick = Find.TickManager.TicksGame;
        int stayTicks = longStay
            ? this.GetReadyPickupStayTicks()
            : ShuttleLeaveDelayDays * GenDate.TicksPerDay;
        this.pickupWindowEndTick = this.pickupShuttleSpawnTick + stayTicks;
        this.emperorNobleRequirementAnnounced = false;
        // New boarding window — previous wave's empty/full snapshot must not carry over.
        if (!this.emperorColonistEndgameTriggered)
        {
            this.ClearEmperorShuttleColonistSnapshot();
        }
    }

    /// Pickup letter body; Emperor stage invites free colonists to board for the ending.
    private string BuildPickupShuttleLetterText(bool longStay)
    {
        string body = longStay
            ? "A shuttle has arrived because at least one CryoRegenesis client finished treatment. "
              + "It will stay for many days — load ready clients when you want and Send. "
              + "Unfinished clients can keep regenerating."
            : "A shuttle has arrived for finished CryoRegenesis clients. "
              + "Load ready clients onto it yourself (unfinished clients can stay for a later pickup).";

        if (this.IsEmperorRegenContractActive())
        {
            body += "\n\nAny number of your colonists may board this shuttle, but only once the "
                + "Emperor is alive and either aboard it himself or sealed in a powered-off "
                + "CryoRegenesis casket, and every other guest is offworld or aboard at their "
                + "target age. Until then colonists are turned away at the ramp.\n\n"
                + "Once the Emperor is regenerated or sealed away, every archon of your colony "
                + "must be aboard before the shuttle will launch — and it will not leave the "
                + "Emperor behind unless one of them is there to take his throne.\n\n"
                + "If even one colonist leaves with the Emperor, your story ends in victory as "
                + "guests of the Imperial court.";
        }

        return body;
    }

    /// Used for death/trust-break emergency leaves: eject and call a pickup if needed.
    private bool DepartClients(Map map, List<Pawn> pawns, Faction faction)
    {
        this.RefreshClientAgeProgress();
        this.LogRoyaltyDebug(
            "DepartClients begin. map=" + (map?.ToString() ?? "null")
            + " pawns=" + (pawns?.Count ?? 0)
            + " faction=" + (faction?.Name ?? "null")
            + " allReady=" + this.pickupAllClientsReady);

        this.EjectClientsFromCaskets(map, pawns);

        List<Pawn> living = pawns.Where(p => p != null && !p.Destroyed && !p.Dead).ToList();
        if (!living.Any())
        {
            this.LogRoyaltyDebug("DepartClients: no living pawns after eject.");
            return false;
        }

        foreach (Pawn pawn in living)
        {
            Hediff anesthetic = pawn.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.Anesthetic);
            if (anesthetic != null)
            {
                pawn.health.RemoveHediff(anesthetic);
                this.LogRoyaltyDebug("Removed anesthetic from " + pawn.Name.ToStringShort);
            }
        }

        // Option C: always use an explicit pickup ship (never a leftover delivery pad ship).
        if (this.pickupShuttleSpawned && this.GetUsableContractShuttle(map) != null)
        {
            this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);
            this.TryPromoteParkedShuttleToLeaveWhenReady();
            return true;
        }

        this.LogRoyaltyDebug("Spawning pickup for " + living.Count + " client(s).");
        bool spawned = this.SpawnPickupShuttle(map, living, faction, longStay: true);
        this.LogRoyaltyDebug("SpawnPickupShuttle result=" + spawned + " pickupSpawned=" + this.pickupShuttleSpawned);
        this.LogClientAgeSnapshot("after spawn pickup shuttle");
        return spawned;
    }

    private Thing GetUsableContractShuttle(Map map)
    {
        if (this.contractShuttle != null && !this.contractShuttle.Destroyed && this.contractShuttle.Spawned
            && this.contractShuttle.Map == map)
        {
            return this.contractShuttle;
        }

#if !RIMWORLD12
        if (this.contractTransportShip != null
            && this.contractTransportShip.ShipExistsAndIsSpawned
            && this.contractTransportShip.shipThing != null
            && this.contractTransportShip.shipThing.Map == map)
        {
            this.contractShuttle = this.contractTransportShip.shipThing;
            return this.contractShuttle;
        }
#endif

        return null;
    }

    private void BoardAndSendShuttle(Map map, Thing shuttle, List<Pawn> living, Faction faction)
    {
        List<Pawn> passengers = (living ?? new List<Pawn>())
            .Where(p => p != null && !p.Destroyed && !p.Dead && !this.WasSuccessfullyReturned(p))
            .Distinct()
            .ToList();

        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        // Prefer ready-only even on this path so a mixed deadline list can still Send.
        List<Pawn> readyPassengers = passengers
            .Where(p =>
            {
                RoyaltyRegenesisClient client = this.activeClients.FirstOrDefault(c => c?.pawn == p);
                return client != null && client.everReachedDesiredAge;
            })
            .ToList();
        List<Pawn> manifest = readyPassengers.Any() ? readyPassengers : passengers;
        if (compShuttle != null)
        {
            compShuttle.requiredPawns.Clear();
            compShuttle.requiredPawns.AddRange(manifest);
        }

#if !RIMWORLD12
        // Promote Wait job to leave as soon as required pawns re-board; keep Send gizmo usable.
        if (this.contractTransportShip != null
            && this.contractTransportShip.curJob is ShipJob_Wait waitJob)
        {
            waitJob.leaveImmediatelyWhenSatisfied = true;
            waitJob.showGizmos = true;
        }
#endif

#if RIMWORLD12
        if (compShuttle != null)
        {
            compShuttle.leaveImmediatelyWhenSatisfied = true;
            if (compShuttle.leaveAfterTicks <= 0 || compShuttle.leaveAfterTicks > ShuttleLeaveDelayDays * GenDate.TicksPerDay)
            {
                compShuttle.leaveAfterTicks = ShuttleLeaveDelayDays * GenDate.TicksPerDay;
            }
        }
#endif

        // Do not assign ExitOnShuttle — that makes every guest rush the pad. The player
        // loads required/ready clients with Autoload or carry, then Send / auto-leave.
        // Short boarding window: this path is all-ready or deadline, not the early-ready long stay.
        this.BeginPickupWindow(longStay: false);
        this.RefreshClientAgeProgress();
        this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);
        if (this.pickupAllClientsReady || this.contractCompletionLogged
            || (this.activeClients.Any()
                && this.activeClients.All(c => c != null && c.everReachedDesiredAge)))
        {
            this.BankDepartureSuccess("board-and-send with ages ready");
        }

        this.LogRoyaltyDebug(
            "BoardAndSendShuttle: pickupSpawned=true tick=" + this.pickupShuttleSpawnTick
            + " windowEnd=" + this.pickupWindowEndTick
            + " requiredPawns=" + (compShuttle?.requiredPawns.Count ?? 0)
            + " allReady=" + this.pickupAllClientsReady
            + " banked=" + this.departureSuccessBanked
            + " autoBoard=false");
    }

    private bool SpawnPickupShuttle(Map map, List<Pawn> living, Faction faction, bool longStay = false)
    {
        // A new pickup gets a fresh launch manifest; the previous wave has already
        // completed or was cleared before this method is called.
        this.pickupLaunchClientPawnIds.Clear();

        // Only map-held remaining clients. Never re-import world pawns who already returned.
        List<Pawn> passengers = (living ?? new List<Pawn>())
            .Where(p => p != null && !p.Destroyed && !p.Dead
                && !this.WasSuccessfullyReturned(p)
                && this.IsClientAvailableOnMap(p))
            .Distinct()
            .ToList();

        if (!passengers.Any())
        {
            this.LogRoyaltyDebug("SpawnPickupShuttle aborted: no map-held remaining clients.");
            return false;
        }

        // Manifest is ready-only when anyone is ready so Send is not locked on unfinished clients.
        List<Pawn> required = passengers
            .Where(p =>
            {
                RoyaltyRegenesisClient client = this.activeClients.FirstOrDefault(c => c?.pawn == p);
                return client != null && client.everReachedDesiredAge;
            })
            .ToList();
        if (!required.Any())
        {
            required = passengers;
        }

        int stayTicks = this.GetReadyPickupStayTicks();
        this.LogRoyaltyDebug(
            "SpawnPickupShuttle for " + passengers.Count + " map client(s), required="
            + required.Count + " longStay=" + longStay
            + " stayDays=" + (stayTicks / (float)GenDate.TicksPerDay).ToString("0.0")
            + ": " + string.Join(", ", passengers.Select(p => p.Name?.ToStringShort ?? "?")));

#if RIMWORLD12
        // longStay: do not auto-leave when partial required load; player uses Send.
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: null,
            requiredPawns: required,
            leaveImmediatelyWhenSatisfied: !longStay,
            hideControls: false);

        CompShuttle compShuttle = shuttle?.TryGetComp<CompShuttle>();
        if (shuttle == null || compShuttle == null)
        {
            Log.Error("[CryoRegenesis] Pickup shuttle generation failed; destroying clients off-map.");
            this.DestroyClientsOffMap(passengers);
            return true;
        }

        this.ConfigureContractShuttleEmbarkRules(compShuttle);
        this.SanitizePickupShuttle(shuttle, required);
        compShuttle.leaveAfterTicks = stayTicks;
        this.contractShuttle = shuttle;
        this.BeginPickupWindow(longStay);
        this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);

        IntVec3 cell = DropCellFinder.TradeDropSpot(map);
        if (!cell.IsValid)
        {
            cell = DropCellFinder.RandomDropSpot(map);
        }

        if (!GenPlace.TryPlaceThing(
                SkyfallerMaker.MakeSkyfaller(ThingDefOf.ShuttleIncoming, shuttle),
                cell,
                map,
                ThingPlaceMode.Near))
        {
            Log.Warning("[CryoRegenesis] Could not place pickup shuttle; destroying clients off-map.");
            this.DestroyClientsOffMap(passengers);
            if (!shuttle.Destroyed)
            {
                shuttle.Destroy(DestroyMode.Vanish);
            }

            return true;
        }

        // No ExitOnShuttle lord — player loads ready clients manually (Autoload / carry).
        this.RefreshClientAgeProgress();
        this.LogRoyaltyDebug(
            "Pickup shuttle (1.2) placed. leaveAfterTicks=" + compShuttle.leaveAfterTicks
            + " longStay=" + longStay
            + " allReady=" + this.pickupAllClientsReady
            + " (no auto-board lord)");

        Find.LetterStack.ReceiveLetter(
            "Regenesis Pickup Shuttle",
            this.BuildPickupShuttleLetterText(longStay),
            LetterDefOf.NeutralEvent,
            new LookTargets(shuttle));
#else
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: null,
            requiredPawns: required,
            hideControls: false);

        if (shuttle == null)
        {
            Log.Error("[CryoRegenesis] Pickup shuttle generation failed; destroying clients off-map.");
            this.DestroyClientsOffMap(passengers);
            return true;
        }

        // Empty ship — contents must never include prior returnees.
        TransportShip ship = TransportShipMaker.MakeTransportShip(
            TransportShipDefOf.Ship_Shuttle,
            null,
            shuttle);
        this.ConfigureContractShuttleEmbarkRules(shuttle.TryGetComp<CompShuttle>());
        this.SanitizePickupShuttle(shuttle, required);

        ShipJob_Arrive arrive = (ShipJob_Arrive)ShipJobMaker.MakeShipJob(ShipJobDefOf.Arrive);
        arrive.mapParent = map.Parent;
        arrive.factionForArrival = faction ?? Faction.OfPlayer;
        if (passengers[0].MapHeld == map)
        {
            arrive.mapOfPawn = passengers[0];
        }

        ship.AddJob(arrive);

        if (longStay)
        {
            // Park like the delivery shuttle: days of pad time, Send when the player is ready.
            ShipJob_Wait waitForever = (ShipJob_Wait)ShipJobMaker.MakeShipJob(ShipJobDefOf.WaitForever);
            waitForever.leaveImmediatelyWhenSatisfied = false;
            waitForever.showGizmos = true;
            ship.AddJob(waitForever);
        }
        else
        {
            ShipJob_WaitTime wait = (ShipJob_WaitTime)ShipJobMaker.MakeShipJob(ShipJobDefOf.WaitTime);
            wait.duration = stayTicks;
            wait.leaveImmediatelyWhenSatisfied = true;
            wait.showGizmos = true;
            wait.sendAwayIfAllDespawned = passengers.Cast<Thing>().ToList();
            ship.AddJob(wait);
        }

        ShipJob_FlyAway flyAway = (ShipJob_FlyAway)ShipJobMaker.MakeShipJob(ShipJobDefOf.FlyAway);
        // Never unload partial cargo back onto the map when leaving.
        flyAway.dropMode = TransportShipDropMode.None;
        ship.AddJob(flyAway);
        ship.Start();

        this.contractTransportShip = ship;
        this.contractShuttle = shuttle;
        this.BeginPickupWindow(longStay);

        // No ExitOnShuttle lord — guests must not stampede the pad. The player chooses
        // who boards (ready clients) via Autoload / carry-to-shuttle / Send.
        this.RefreshClientAgeProgress();
        this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);
        this.LogRoyaltyDebug(
            "Pickup shuttle (TransportShip) started. longStay=" + longStay
            + " stayDays=" + (stayTicks / (float)GenDate.TicksPerDay).ToString("0.0")
            + " windowEnd=" + this.pickupWindowEndTick
            + " leaveImmediatelyWhenSatisfied=" + (!longStay)
            + " allReady=" + this.pickupAllClientsReady
            + " passengers=" + passengers.Count
            + " required=" + required.Count
            + " container=" + (ship.TransporterComp?.innerContainer?.Count ?? -1)
            + " autoBoard=false"
            + " ship=" + (ship != null)
            + " shuttleThing=" + (shuttle?.LabelCap ?? "null"));

        Find.LetterStack.ReceiveLetter(
            "Regenesis Pickup Shuttle",
            this.BuildPickupShuttleLetterText(longStay),
            LetterDefOf.NeutralEvent,
            new LookTargets(shuttle));
#endif

        return true;
    }

    /// How long an early-ready pickup should remain available (at least two weeks, or
    /// through the real contract deadline — whichever is longer).
    private int GetReadyPickupStayTicks()
    {
        int minStay = ReadyPickupMinStayDays * GenDate.TicksPerDay;
        if (this.contractDeadlineTick <= 0)
        {
            return minStay;
        }

        int untilDeadline = this.contractDeadlineTick - Find.TickManager.TicksGame;
        // Small post-deadline buffer so a ship parked on the last day is still usable.
        int throughDeadline = Math.Max(0, untilDeadline) + ShuttleLeaveDelayDays * GenDate.TicksPerDay;
        return Math.Max(minStay, throughDeadline);
    }

    /// Empty container + requiredPawns limited to the allowed remaining map clients.
    private void SanitizePickupShuttle(Thing shuttle, List<Pawn> allowedPassengers)
    {
        if (shuttle == null)
        {
            return;
        }

        HashSet<Pawn> allowed = new HashSet<Pawn>(allowedPassengers ?? Enumerable.Empty<Pawn>());
        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        if (compShuttle != null)
        {
            compShuttle.requiredPawns.RemoveAll(p => p == null || !allowed.Contains(p) || this.WasSuccessfullyReturned(p));
            if (!compShuttle.requiredPawns.Any() && allowed.Any())
            {
                compShuttle.requiredPawns.AddRange(allowed);
            }
        }

        CompTransporter transporter = shuttle.TryGetComp<CompTransporter>();
        if (transporter?.innerContainer == null || !transporter.innerContainer.Any)
        {
            return;
        }

        // Brand-new pickups must arrive empty. Anything already inside is a bug — strip it.
        List<Thing> stowaways = transporter.innerContainer.ToList();
        foreach (Thing thing in stowaways)
        {
            transporter.innerContainer.Remove(thing);
            if (thing is Pawn pawn && !pawn.Destroyed)
            {
                this.LogRoyaltyDebug(
                    "Sanitized stowaway off pickup shuttle: " + (pawn.Name?.ToStringShort ?? pawn.LabelShort));
                // Already-returned / world pawns must not reappear on the colony map.
                if (this.WasSuccessfullyReturned(pawn) || Find.WorldPawns.Contains(pawn))
                {
                    continue;
                }

                // Unexpected living stowaway that is not a world pawn — discard rather than
                // dump them onto the map as a false "returnee".
                if (!pawn.Spawned)
                {
                    pawn.Destroy(DestroyMode.Vanish);
                }
            }
            else if (thing != null && !thing.Destroyed)
            {
                thing.Destroy(DestroyMode.Vanish);
            }
        }
    }

    private void ClearContractShuttle()
    {
        this.contractShuttle = null;
#if !RIMWORLD12
        this.contractTransportShip = null;
#endif
        this.pickupLaunchClientPawnIds.Clear();
        // Do not clear emperorShuttleColonistEscapeeLabels here — FinishContractAfterDeparture
        // may still need them after HandleContractShuttleDeparture already nulls the ship ref.
    }

    private void ClearEmperorShuttleColonistSnapshot()
    {
        this.emperorShuttleColonistEscapeeLabels?.Clear();
        this.emperorEscapeeSnapshotSealed = false;
        if (!this.emperorColonistEndgameTriggered)
        {
            this.emperorUsurpationCountCandidate = null;
            this.emperorUsurpationCountLabel = string.Empty;
            this.emperorCourtHonoree = null;
            this.emperorCourtHonoreeLabel = string.Empty;
        }
    }

    private void ClearContractFlags(IEnumerable<RoyaltyRegenesisClient> clients)
    {
        foreach (RoyaltyRegenesisClient client in clients)
        {
            if (client?.pawn == null)
            {
                continue;
            }

            TrueAgeTracker tracker = client.pawn.health?.hediffSet?
                .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
            if (tracker != null)
            {
                tracker.underRegenContract = false;
            }
        }
    }
}
