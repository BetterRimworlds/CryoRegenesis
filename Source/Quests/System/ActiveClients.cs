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

/// Active-contract monitoring, multi-wave pickup, and shuttle departure handling.
public partial class RoyaltyRegenesisQuestSystem
{

    private void CheckActiveClients()
    {
        if (!this.activeClients.Any())
        {
            return;
        }

        // Refresh records before any success/fail decisions (including destroy/leave).
        this.RefreshClientAgeProgress();
        this.UpdateClientsArrivedFlag();

        // Death always hard-resets trust (before shuttle-leave accounting, which may
        // destroy boarding passengers) — except Emperor murder, which arms a Planetkiller.
        if (this.activeClients.Any(client => client.pawn != null && client.pawn.Dead))
        {
            // Prefer the Emperor when any dead client is him. First(Dead) alone can pick a
            // wife who died in the same check window and permanently skip the Emperor
            // death endgame (assassination / Planetkiller).
            RoyaltyRegenesisClient deadClient =
                this.activeClients.FirstOrDefault(client =>
                    client.pawn != null && client.pawn.Dead && client.pawn == this.emperor)
                ?? this.activeClients.First(client => client.pawn != null && client.pawn.Dead);
            Pawn dead = deadClient.pawn;
            Faction sender = this.activeClients.FirstOrDefault()?.sourceFaction
                ?? deadClient.sourceFaction;

            if (this.activeContractStage == RoyaltyRegenesisStage.EmperorArrival
                && deadClient?.pawn == this.emperor)
            {
                this.HandleEmperorDeathEndgame(dead, sender);
                return;
            }

            List<Pawn> survivors = this.activeClients
                .Where(client => client.pawn != null && !client.pawn.Dead)
                .Select(client => client.pawn)
                .ToList();
            Map map = survivors.FirstOrDefault(p => p.MapHeld != null)?.MapHeld ?? this.GetTargetMap();
            if (map != null && survivors.Any())
            {
                this.DepartClients(map, survivors, sender);
            }

            this.MajorTrustReset(
                $"{dead.Name.ToStringShort} died under a CryoRegenesis return contract. " +
                "Trust is shattered — all progress is lost and the chain restarts from the beginning.",
                sender,
                "Client died under contract");
            return;
        }

        // Pickup shuttle left (partial wave or final leave). Delivery ships are not tracked.
        if (this.pickupShuttleSpawned && this.HasContractShuttleLeftMap())
        {
            this.HandleContractShuttleDeparture();
            return;
        }

        // Follow-up pickup after a successful partial return wave.
        if (this.TrySpawnScheduledWavePickup())
        {
            return;
        }

        // Only drop truly destroyed pawns. Clients still in a skyfaller/shuttle container
        // are not Destroyed and must not fail the contract.
        int beforeCount = this.activeClients.Count;
        this.activeClients.RemoveAll(client => client?.pawn == null || client.pawn.Destroyed);
        if (beforeCount != this.activeClients.Count)
        {
            this.LogRoyaltyDebug(
                "Removed " + (beforeCount - this.activeClients.Count)
                + " destroyed/null client(s). Remaining=" + this.activeClients.Count
                + " pickupSpawned=" + this.pickupShuttleSpawned
                + " allReady=" + this.pickupAllClientsReady
                + " arrived=" + this.clientsHaveArrived
                + " returned=" + this.clientsSuccessfullyReturned);
        }

        // Contract ends when every client is gone (usually with the shuttle).
        if (!this.activeClients.Any())
        {
            if (this.clientsHaveArrived || this.pickupShuttleSpawned || this.IsDepartureSuccessBanked()
                || this.clientsSuccessfullyReturned > 0)
            {
                bool success = this.clientsSuccessfullyReturned >= Math.Max(1, this.completionRewardClientCount)
                    || this.IsDepartureSuccessBanked();
                this.LogRoyaltyDebug(
                    "All clients gone after delivery/pickup → FinishContractAfterDeparture(success="
                    + success + " returned=" + this.clientsSuccessfullyReturned
                    + "/" + this.completionRewardClientCount + ")");
                this.FinishContractAfterDeparture(success);
                return;
            }

            this.ClearContractShuttle();
            this.MajorTrustReset(
                "CryoRegenesis clients vanished under contract. Trust is lost — the chain resets.",
                null,
                "Contract clients lost");
            return;
        }

        if (this.pickupShuttleSpawned)
        {
            this.RefreshClientAgeProgress();
            // During boarding, only require ready clients so unfinished ones can stay for later.
            this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);

            if (this.PickupLoadingWindowExpired())
            {
                this.LogRoyaltyDebug(
                    "Pickup loading window expired. allReady=" + this.pickupAllClientsReady
                    + " banked=" + this.departureSuccessBanked
                    + " onMap=" + this.activeClients.Count(c => this.IsClientAvailableOnMap(c.pawn))
                    + " inTransport=" + this.activeClients.Count(c => this.IsClientInReturnTransport(c.pawn)));
                this.LogClientAgeSnapshot("pickup timeout");
                this.FailContractAfterPickupTimeout();
                return;
            }

            // Still boarding: wait for the shuttle to leave (HasContractShuttleLeftMap) or timeout.
            if (this.activeClients.Any(client => !this.IsClientAvailableOnMap(client.pawn))
                || this.activeClients.Any(client => this.IsClientAboardContractShuttle(client.pawn)))
            {
                return;
            }
        }

        // Wait until the delivery shuttle has finished unloading before success/deadline checks.
        // Otherwise a brand-new contract can "complete" or soft-fail while clients are still landing.
        if (!this.clientsHaveArrived)
        {
            return;
        }

        // Keep recruitment locked for the entire stay.
        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            this.LockRecruitment(client.pawn);
            if (client.pawn?.guest != null && client.isPrisoner && !client.pawn.IsPrisoner)
            {
                this.ApplyGuestOrPrisonerStatus(client.pawn, isPrisoner: true);
            }
        }

        this.EnsureGuestClientsAreQuestLodgers();

        this.EnsureContractDeadline();
        this.RefreshClientAgeProgress();

        // Option C: no ship during treatment. Call a long-stay pickup when anyone is ready
        // or the real deadline hits. Required passengers = ready only (Send with partial party).
        this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);

        bool allDone = this.pickupAllClientsReady;
        bool deadlineReached = this.IsContractDeadlineReached();

        if (this.pickupShuttleSpawned)
        {
            // Long-stay pickup on the pad: only auto-leave when remaining clients are all ready
            // or the deadline forces a last call. The Emperor pickup is the exception — colonists
            // may only board once he is aboard, so auto-leaving the instant he loads would slam
            // the door on them. The player presses Send there; the deadline is still the backstop.
            if (deadlineReached || (allDone && !this.IsEmperorRegenContractActive()))
            {
                this.TryPromoteParkedShuttleToLeaveWhenReady();
            }

            return;
        }

        // Waiting on a scheduled follow-up pickup after a partial leave.
        if (this.nextWavePickupTick > 0)
        {
            return;
        }

        List<Pawn> mapHeld = this.GetMapHeldContractPawns();
        bool anyReady = mapHeld.Any(p =>
        {
            RoyaltyRegenesisClient client = this.activeClients.FirstOrDefault(c => c?.pawn == p);
            return client != null && client.everReachedDesiredAge;
        });

        // Call pickup when: anyone ready, everyone ready, or contract deadline.
        if (!anyReady && !allDone && !deadlineReached)
        {
            return;
        }

        if (allDone)
        {
            this.BankDepartureSuccess("all remaining clients ready — calling pickup");
        }

        this.LogRoyaltyDebug(
            "Calling pickup shuttle. anyReady=" + anyReady
            + " allDone=" + allDone
            + " deadlineReached=" + deadlineReached
            + " mapHeld=" + mapHeld.Count);
        this.EnsureReadyClientPickupAvailable(mapHeld);
    }

    /// Spawn a long-stay empty pickup when clients need to leave (ready and/or deadline).
    /// Option C: never reuses a drop-off ship — those leave after unload.
    private void EnsureReadyClientPickupAvailable(List<Pawn> mapHeld)
    {
        Map map = mapHeld?.FirstOrDefault(p => p.MapHeld != null)?.MapHeld ?? this.GetTargetMap();
        if (map == null)
        {
            return;
        }

        if (this.pickupShuttleSpawned)
        {
            this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);
            return;
        }

        Thing parked = this.GetUsableContractShuttle(map);
        if (parked != null)
        {
            // Existing pickup still on the pad — refresh manifest only.
            if (!this.pickupShuttleSpawned)
            {
                this.BeginPickupWindow(longStay: true);
            }

            this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);
            this.ConfigureContractShuttleEmbarkRules(parked.TryGetComp<CompShuttle>());
            this.LogRoyaltyDebug("Pickup already on pad; refreshed ready-only manifest.");
            return;
        }

        List<Pawn> passengers = mapHeld ?? this.GetMapHeldContractPawns();
        if (!passengers.Any())
        {
            return;
        }

        // Ready clients should be free to walk/board, not stuck in a casket.
        List<Pawn> readyToEject = passengers
            .Where(p =>
            {
                RoyaltyRegenesisClient client = this.activeClients.FirstOrDefault(c => c?.pawn == p);
                return client != null && client.everReachedDesiredAge;
            })
            .ToList();
        if (readyToEject.Any())
        {
            this.EjectClientsFromCaskets(map, readyToEject);
        }

        Faction faction = this.activeClients.FirstOrDefault()?.sourceFaction;
        this.LogRoyaltyDebug(
            "Spawning long-stay pickup for " + passengers.Count + " map client(s).");
        this.SpawnPickupShuttle(map, passengers, faction, longStay: true);
    }

    /// When every remaining client is ready (or the deadline hits), allow the parked
    /// long-stay shuttle to leave as soon as its required ready passengers are loaded.
    private void TryPromoteParkedShuttleToLeaveWhenReady()
    {
        Map map = this.GetTargetMap();
        Thing shuttle = map != null ? this.GetUsableContractShuttle(map) : this.GetContractShuttleThing();
        if (shuttle == null || shuttle.Destroyed)
        {
            return;
        }

        this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);

#if !RIMWORLD12
        if (this.contractTransportShip != null
            && this.contractTransportShip.curJob is ShipJob_Wait waitJob)
        {
            waitJob.leaveImmediatelyWhenSatisfied = true;
            waitJob.showGizmos = true;
        }
#endif

#if RIMWORLD12
        CompShuttle comp = shuttle.TryGetComp<CompShuttle>();
        if (comp != null)
        {
            comp.leaveImmediatelyWhenSatisfied = true;
        }
#endif

        if (this.pickupAllClientsReady || this.IsDepartureSuccessBanked())
        {
            this.BankDepartureSuccess("promote parked shuttle — remaining clients ready");
        }

        this.LogRoyaltyDebug("Promoted parked shuttle to leave-when-required-loaded.");
    }

    /// After the contract shuttle leaves: credit ready returnees, keep treating anyone
    /// still on the map, and schedule a follow-up shuttle when a successful partial wave left.
    private void HandleContractShuttleDeparture()
    {
        this.RefreshClientAgeProgress();
        this.LogClientAgeSnapshot("shuttle left");

        List<RoyaltyRegenesisClient> remaining = new List<RoyaltyRegenesisClient>();
        List<RoyaltyRegenesisClient> departed = new List<RoyaltyRegenesisClient>();

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client?.pawn == null || client.pawn.Destroyed)
            {
                departed.Add(client);
                continue;
            }

            if (this.IsClientAboardContractShuttle(client.pawn))
            {
                // Only passengers in this shuttle's transporter actually departed.
                departed.Add(client);
            }
            else
            {
                remaining.Add(client);
            }
        }

        // Emperor-stage branched endings (before partial-wave bookkeeping can soft-continue).
        if (this.activeContractStage == RoyaltyRegenesisStage.EmperorArrival
            && !this.emperorColonistEndgameTriggered)
        {
            // Branch 1: sealed the living Emperor in an unpowered pod while the party fled.
            if (this.TryTriggerYouKeepWhatYouKillEndgame(remaining, departed, "shuttle left"))
            {
                return;
            }

            // Branch 3: free colonists left *with* the Emperor (he must have departed too).
            if (departed.Any(client => client?.pawn == this.emperor))
            {
                this.TryTriggerEmperorColonistEndgameFromSnapshot("shuttle left with Emperor");
            }
            else if (this.emperorShuttleColonistEscapeeLabels != null
                     && this.emperorShuttleColonistEscapeeLabels.Any())
            {
                this.LogRoyaltyDebug(
                    "Free colonists left without the Emperor and without unpowered-casket usurpation — "
                    + "no Imperial Court victory.");
            }
        }

        // Fail the contract if any passenger left before reaching the contracted age.
        RoyaltyRegenesisClient unfinishedDeparture = departed
            .FirstOrDefault(c => c != null && !c.everReachedDesiredAge);
        if (unfinishedDeparture != null)
        {
            this.FinishContractAfterDeparture(
                false,
                failLabel: "CryoRegenesis contract expired",
                failText: "The shuttle departed with a client before the requested regression was finished.");
            return;
        }

        int readyDeparted = departed.Count(c => c != null && c.everReachedDesiredAge);
        this.LogRoyaltyDebug(
            "HandleContractShuttleDeparture: departed=" + departed.Count
            + " readyDeparted=" + readyDeparted
            + " remaining=" + remaining.Count
            + " priorReturned=" + this.clientsSuccessfullyReturned);

        if (remaining.Any())
        {
            if (readyDeparted >= 1)
            {
                this.clientsSuccessfullyReturned += readyDeparted;
                this.FinalizeDepartedWaveClients(departed);
                // Keep only still-present map clients; never retain returned/off-map slots.
                this.activeClients = remaining
                    .Where(c => c?.pawn != null
                        && !c.pawn.Destroyed
                        && !c.pawn.Dead
                        && !this.WasSuccessfullyReturned(c.pawn))
                    .ToList();
                this.ClearContractShuttle();
                this.pickupShuttleSpawned = false;
                this.pickupShuttleSpawnTick = -1;
                this.pickupWindowEndTick = -1;
                this.departureSuccessBanked = false;
                this.contractCompletionLogged = false;
                this.RefreshClientAgeProgress();

                // Option C: only call another ship if ready clients were left behind.
                // Unfinished-only remaining → wait until someone hits target age (EnsureReady…).
                bool readyStillHere = this.activeClients.Any(c =>
                    c != null && c.everReachedDesiredAge && c.pawn != null
                    && !c.pawn.Destroyed && !c.pawn.Dead
                    && this.IsClientAvailableOnMap(c.pawn));

                if (readyStillHere)
                {
                    this.RequestFollowUpPickupForRemaining("partial return — ready clients still on map");
                }
                else
                {
                    this.LogRoyaltyDebug(
                        "Partial return complete; " + this.activeClients.Count
                        + " unfinished client(s) remain — no pickup until someone is ready.");
                    Find.LetterStack.ReceiveLetter(
                        "Regenesis clients returned",
                        readyDeparted + " CryoRegenesis client(s) returned after finishing treatment. "
                        + "Continue regenerating the rest; a pickup will be called when they reach "
                        + "their target age (or at the contract deadline).",
                        LetterDefOf.PositiveEvent,
                        new LookTargets(this.activeClients
                            .Select(c => c?.pawn)
                            .Where(p => p != null && !p.Destroyed)));
                }

                return;
            }

            // Shuttle left or was destroyed without taking any ready client.
            // Keep the contract; free the pad and recover with a later pickup.
            this.ClearContractShuttle();
            this.pickupShuttleSpawned = false;
            this.pickupShuttleSpawnTick = -1;
            this.pickupWindowEndTick = -1;

            if (remaining.Any(c => c != null && c.everReachedDesiredAge))
            {
                this.RequestFollowUpPickupForRemaining("shuttle left — ready clients still on map");
            }
            else
            {
                this.LogRoyaltyDebug(
                    "Contract shuttle lost mid-treatment with no ready clients; "
                    + "replacement will spawn when ages finish or the deadline hits.");
            }

            return;
        }

        // Nobody left on the map — finalize the contract.
        this.clientsSuccessfullyReturned += readyDeparted;
        bool success = this.clientsSuccessfullyReturned >= Math.Max(1, this.completionRewardClientCount)
            || (readyDeparted > 0
                && departed.All(c => c != null && c.everReachedDesiredAge)
                && this.IsDepartureSuccessBanked());

        this.LogRoyaltyDebug(
            "Final wave departure: success=" + success
            + " returned=" + this.clientsSuccessfullyReturned
            + "/" + this.completionRewardClientCount
            + " readyThisWave=" + readyDeparted);
        this.FinishContractAfterDeparture(
            success,
            failLabel: "CryoRegenesis contract expired",
            failText: "The shuttle departed before every contracted client finished regeneration. Another attempt may come later.");
    }

    private void FinalizeDepartedWaveClients(List<RoyaltyRegenesisClient> departed)
    {
        foreach (RoyaltyRegenesisClient client in departed.Where(c => c != null))
        {
            Pawn pawn = client.pawn;
            if (pawn != null)
            {
                // Mark permanently so a later pickup never re-requires or re-imports them.
                if (client.everReachedDesiredAge && !this.returnedClientPawnIds.Contains(pawn.thingIDNumber))
                {
                    this.returnedClientPawnIds.Add(pawn.thingIDNumber);
                }

                this.ReleaseFromCurrentLord(pawn);
                this.RemoveClientFromContractQuest(pawn);

                if (!pawn.Destroyed && !pawn.Dead && client.sourceFaction != null
                    && !client.isPrisoner && pawn.Faction == Faction.OfPlayer)
                {
                    pawn.SetFaction(client.sourceFaction);
                }
            }

            this.ClearContractFlags(new[] { client });
            // Drop the reference so save/load and later waves cannot resurrect this client slot.
            client.pawn = null;
        }
    }

    /// Strip a pawn from quest ExtraFaction lodger lists so the active contract cannot
    /// pull them back when a follow-up shuttle arrives.
    private void RemoveClientFromContractQuest(Pawn pawn)
    {
        if (pawn == null || this.activeContractQuest == null || this.activeContractQuest.Historical)
        {
            return;
        }

        foreach (QuestPart_ExtraFaction part in this.activeContractQuest.PartsListForReading
                     .OfType<QuestPart_ExtraFaction>())
        {
            if (part.affectedPawns != null && part.affectedPawns.Contains(pawn))
            {
                part.affectedPawns.Remove(pawn);
            }
        }
    }

    private bool WasSuccessfullyReturned(Pawn pawn)
    {
        return pawn != null && this.returnedClientPawnIds.Contains(pawn.thingIDNumber);
    }

    /// Living contract clients that are still map-reachable (spawned or in a casket).
    /// Excludes returned, destroyed, world-only, and off-map shuttle passengers.
    private List<Pawn> GetMapHeldContractPawns()
    {
        return this.activeClients
            .Where(c => c?.pawn != null
                && !c.pawn.Destroyed
                && !c.pawn.Dead
                && !this.WasSuccessfullyReturned(c.pawn)
                && this.IsClientAvailableOnMap(c.pawn))
            .Select(c => c.pawn)
            .ToList();
    }

    private void ScheduleNextWavePickup()
    {
        this.nextWavePickupTick = Find.TickManager.TicksGame + WavePickupDelayDays * GenDate.TicksPerDay;
        this.LogRoyaltyDebug(
            "Next wave pickup scheduled at tick " + this.nextWavePickupTick
            + " (delayDays=" + WavePickupDelayDays + "). Remaining clients=" + this.activeClients.Count
            + " returnedIds=" + this.returnedClientPawnIds.Count);
    }

    /// After a pickup leaves with ready clients still on site, call another long-stay
    /// pickup. Does not send a separate "returning" letter — SpawnPickupShuttle already does.
    private void RequestFollowUpPickupForRemaining(string debugReason)
    {
        Map map = this.GetTargetMap();
        List<Pawn> remaining = this.GetMapHeldContractPawns();
        if (!remaining.Any())
        {
            this.LogRoyaltyDebug(
                "RequestFollowUpPickupForRemaining (" + debugReason + "): nobody left on map.");
            return;
        }

        // Already have a pickup (or one is due) — do not stack an extra ship.
        if (this.pickupShuttleSpawned || this.GetUsableContractShuttle(map) != null)
        {
            this.LogRoyaltyDebug(
                "RequestFollowUpPickupForRemaining (" + debugReason + "): pickup already present.");
            this.UpdateShuttleRequiredPawns(readyOnlyIfAnyReady: true);
            return;
        }

        if (this.nextWavePickupTick > 0 && Find.TickManager.TicksGame < this.nextWavePickupTick)
        {
            this.LogRoyaltyDebug(
                "RequestFollowUpPickupForRemaining (" + debugReason + "): already scheduled.");
            return;
        }

        if (map != null)
        {
            Faction faction = this.activeClients.FirstOrDefault()?.sourceFaction;
            this.LogRoyaltyDebug(
                "RequestFollowUpPickupForRemaining (" + debugReason + "): spawning follow-up.");
            this.nextWavePickupTick = -1;
            this.SpawnPickupShuttle(map, remaining, faction, longStay: true);
            return;
        }

        this.ScheduleNextWavePickup();
        this.LogRoyaltyDebug(
            "RequestFollowUpPickupForRemaining (" + debugReason + "): no map yet, scheduled tick="
            + this.nextWavePickupTick);
    }

    /// Spawns a follow-up pickup shuttle after a successful partial return wave.
    /// Returns true when a spawn was attempted this check.
    private bool TrySpawnScheduledWavePickup()
    {
        if (this.nextWavePickupTick < 0 || this.pickupShuttleSpawned)
        {
            return false;
        }

        if (Find.TickManager.TicksGame < this.nextWavePickupTick)
        {
            return false;
        }

        // Still have a usable parked ship — no need for a wave shuttle.
        Map map = this.GetTargetMap();
        if (map != null && this.GetUsableContractShuttle(map) != null)
        {
            this.nextWavePickupTick = -1;
            return false;
        }

        // Only clients still on the map — never world pawns who already flew home.
        List<Pawn> remaining = this.GetMapHeldContractPawns();
        if (!remaining.Any())
        {
            this.nextWavePickupTick = -1;
            return false;
        }

        map = remaining.FirstOrDefault(p => p.MapHeld != null)?.MapHeld ?? map;
        if (map == null)
        {
            this.LogRoyaltyDebug("TrySpawnScheduledWavePickup: no map yet; will retry.");
            return true;
        }

        this.nextWavePickupTick = -1;
        Faction faction = this.activeClients.FirstOrDefault()?.sourceFaction;
        this.LogRoyaltyDebug(
            "Spawning scheduled wave pickup for " + remaining.Count + " remaining map client(s)."
            + " returnedIds=" + this.returnedClientPawnIds.Count);
        // Long stay: at least one ready is expected after a partial wave / missed boarding.
        this.SpawnPickupShuttle(map, remaining, faction, longStay: true);
        return true;
    }

    /// Guest lodgers are not free colonists for CompShuttle.IsAllowed unless they are
    /// requiredPawns or acceptColonists is true. Enable guest embark while Send still
    /// only requires ready clients (see <see cref="UpdateShuttleRequiredPawns"/>).
    /// Free colonists are then filtered by <see cref="MayBoardRegenPickupShuttle"/>
    /// (clients, non-ex DirectRelations, or any free colonist during the Emperor stage).
    private void ConfigureContractShuttleEmbarkRules(CompShuttle compShuttle)
    {
        if (compShuttle == null)
        {
            return;
        }

        // Quest lodgers embark only if they are requiredPawns or acceptColonists is true.
        // acceptColonists also opens the door to free colonists — Patch_CompShuttle_IsAllowed
        // keeps unrelated colonists off non-Emperor regen pickups.
        compShuttle.acceptColonists = true;
        compShuttle.onlyAcceptColonists = false;
#if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
        compShuttle.acceptChildren = true;
        compShuttle.onlyAcceptHealthy = false;
        compShuttle.acceptColonyPrisoners = true;
#endif
    }

    /// Restricts the contract shuttle's required passengers so Send can launch with a
    /// partial party: only clients who have reached target age (on map or already aboard).
    /// Unfinished clients must never appear on the manifest or Send stays disabled forever.
    /// They can still board via acceptColonists so nobody is "Not allowed".
    private void UpdateShuttleRequiredPawns(bool readyOnlyIfAnyReady)
    {
        Map map = this.GetTargetMap();
        Thing shuttle = map != null ? this.GetUsableContractShuttle(map) : this.GetContractShuttleThing();
        if (shuttle == null || shuttle.Destroyed)
        {
            return;
        }

        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        if (compShuttle == null)
        {
            return;
        }

        this.ConfigureContractShuttleEmbarkRules(compShuttle);

        List<Pawn> ready = this.GetReadyContractPawnsForShuttleManifest(shuttle);
        List<Pawn> required;
        if (readyOnlyIfAnyReady && ready.Any())
        {
            // Only finished clients — Send works once they board; unfinished never block launch.
            // Living escorts (guards) leave with the ready wave when possible.
            required = ready.Concat(this.GetMapHeldEscorts()).Distinct().ToList();
        }
        else
        {
            // Nobody ready yet: keep full map-held list so the delivery ship cannot be
            // Sent away empty mid-contract.
            required = this.GetMapHeldContractPawns().Concat(this.GetMapHeldEscorts()).Distinct().ToList();
        }

        // Once the Emperor is regenerated or sealed away, every archon on this map must leave on
        // the Imperial shuttle — as his guest or as his successor. They are ejected from caskets
        // so they can actually board; nobody off this map is fetched.
        List<Pawn> nobles = this.GetRequiredImperialNobleColonists(shuttle);
        if (nobles.Any())
        {
            this.EjectClientsFromCaskets(shuttle.MapHeld ?? map, nobles);
            required = required.Concat(nobles).Distinct().ToList();
            this.AnnounceImperialNobleRequirement(nobles);
        }

        // Drop stale/world/returned refs that would keep AllRequiredThingsLoaded false forever.
        HashSet<Pawn> requiredSet = new HashSet<Pawn>(required);
        bool changed = compShuttle.requiredPawns.Count != required.Count
            || compShuttle.requiredPawns.Any(p => p == null || !requiredSet.Contains(p));

        if (!changed)
        {
            return;
        }

        compShuttle.requiredPawns.Clear();
        if (required.Any())
        {
            compShuttle.requiredPawns.AddRange(required);
        }

#if !RIMWORLD12 && !RIMWORLD13
        // 1.4+: unfinished clients still on the map can otherwise stay "required" when
        // downed and block Send even after we scrub requiredPawns.
        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            Pawn pawn = client?.pawn;
            if (pawn == null || pawn.Destroyed || requiredSet.Contains(pawn))
            {
                continue;
            }

            if (!compShuttle.pawnsToIgnoreIfDownedOfNotOnTheMap.Contains(pawn))
            {
                compShuttle.pawnsToIgnoreIfDownedOfNotOnTheMap.Add(pawn);
            }
        }
#endif

        this.LogRoyaltyDebug(
            "Updated shuttle requiredPawns to " + required.Count
            + (ready.Any() && readyOnlyIfAnyReady
                ? " ready-only (Send once these are aboard; all contract guests may embark)."
                : " map-held (nobody ready yet).")
            + (nobles.Any() ? " Required archon(s): " + string.Join(", ", nobles.Select(p => p.LabelShort)) + "." : string.Empty));
    }

    /// Ready contract clients that should count toward the shuttle manifest: still on the
    /// map/casket, or already inside the contract shuttle. Excludes returned/world-only pawns.
    private List<Pawn> GetReadyContractPawnsForShuttleManifest(Thing shuttle)
    {
        List<Pawn> ready = new List<Pawn>();
        CompTransporter transporter = shuttle?.TryGetComp<CompTransporter>();

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            Pawn pawn = client?.pawn;
            if (client == null || pawn == null || pawn.Destroyed || pawn.Dead
                || this.WasSuccessfullyReturned(pawn))
            {
                continue;
            }

            // Re-record if the casket notification was missed so ready nobles are not locked out.
            if (!client.everReachedDesiredAge
                && this.EvaluateAgeAgainstTarget(client, out _, out _))
            {
                client.everReachedDesiredAge = true;
            }

            if (!client.everReachedDesiredAge)
            {
                continue;
            }

            // A living Emperor sealed in a powered-off casket is being left behind on purpose, so
            // he must not sit on the manifest and lock Send. But only an archon can take a throne:
            // with nobody eligible to usurp him he stays required, and the shuttle cannot leave
            // without him.
            if (client?.pawn == this.emperor
                && this.IsEmperorInUnpoweredCryoCasket()
                && this.GetImperialNobleColonists(shuttle).Any())
            {
                continue;
            }

            bool onMap = this.IsClientAvailableOnMap(pawn);
            bool aboard = transporter != null && transporter.innerContainer.Contains(pawn);
            if (onMap || aboard)
            {
                ready.Add(pawn);
            }
        }

        return ready.Distinct().ToList();
    }

    private bool IsDepartureSuccessBanked()
    {
        return this.departureSuccessBanked
            || this.contractCompletionLogged
            || this.pickupAllClientsReady;
    }

    private void BankDepartureSuccess(string reason)
    {
        if (this.departureSuccessBanked)
        {
            return;
        }

        this.departureSuccessBanked = true;
        this.contractCompletionLogged = true;
        this.pickupAllClientsReady = true;
        this.LogRoyaltyDebug("Departure success banked (" + reason + ").");
    }

    private void UpdateClientsArrivedFlag()
    {
        if (this.clientsHaveArrived || !this.activeClients.Any())
        {
            return;
        }

        if (this.activeClients.All(client => client?.pawn != null && this.IsClientAvailableOnMap(client.pawn)))
        {
            this.clientsHaveArrived = true;
            this.LogRoyaltyDebug("All clients delivered and available on map.");
        }
    }

    /// True when the parked/pickup contract shuttle is no longer on the map after delivery.
    private bool HasContractShuttleLeftMap()
    {
#if !RIMWORLD12
        if (this.contractTransportShip != null)
        {
            return !this.contractTransportShip.ShipExistsAndIsSpawned;
        }
#endif

        if (this.contractShuttle != null)
        {
            return this.contractShuttle.Destroyed || !this.contractShuttle.Spawned;
        }

        // No shuttle reference: only treat as departed once return boarding has started.
        // Mid-contract with a lost ref should fall through to deadline + replacement shuttle.
        return this.pickupShuttleSpawned;
    }

    private Thing GetContractShuttleThing()
    {
        if (this.contractShuttle != null && !this.contractShuttle.Destroyed)
        {
            return this.contractShuttle;
        }

#if !RIMWORLD12
        if (this.contractTransportShip != null && this.contractTransportShip.shipThing != null
            && !this.contractTransportShip.shipThing.Destroyed)
        {
            return this.contractTransportShip.shipThing;
        }
#endif

        return null;
    }

    /// True when the pawn is inside the contract shuttle's transporter container.
    private bool IsClientAboardContractShuttle(Pawn pawn)
    {
        if (pawn == null || pawn.Destroyed || pawn.Dead)
        {
            return false;
        }

        Thing shuttle = this.GetContractShuttleThing();
        if (shuttle == null)
        {
            return false;
        }

        CompTransporter transporter = shuttle.TryGetComp<CompTransporter>();
        return transporter != null && transporter.innerContainer.Contains(pawn);
    }
}
