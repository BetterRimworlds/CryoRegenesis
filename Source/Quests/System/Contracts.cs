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

/// Contract startup: stage starters, client prep, deadlines, and delivery.
public partial class RoyaltyRegenesisQuestSystem
{

    private void StartRulerPrisonerContract(Map map)
    {
        Faction sender = this.RandomPlanetarySenderFaction();
        if (sender == null)
        {
            this.ScheduleRetry(
                "No eligible factions",
                "No non-Empire, non-Ancient planetary factions are available to send CryoRegenesis prisoners. Retrying later.");
            return;
        }

        List<Pawn> availablePawns = this.GetAvailableWorldPawns(sender, 19);
        if (!availablePawns.Any())
        {
            this.ScheduleRetry(
                "No eligible faction members",
                $"{sender.Name} has no eligible world pawns available for a CryoRegenesis trial. Retrying later.");
            return;
        }

        int pawnCount = Math.Min(Rand.RangeInclusive(1, 2), availablePawns.Count);
        List<Pawn> pawns = new List<Pawn>();
        for (int i = 0; i < pawnCount; i++)
        {
            Pawn pawn = availablePawns.RandomElement();
            availablePawns.Remove(pawn);
            int duration = Rand.Element(HalfYearTicks, GenDate.TicksPerYear, GenDate.TicksPerYear * 2);
            long targetTicks = Math.Max(18L * GenDate.TicksPerYear, pawn.ageTracker.AgeBiologicalTicks - duration);
            this.PrepareClient(pawn, targetTicks, "foreign prisoner", sender, isPrisoner: true, contractDays: 0);
            pawns.Add(pawn);
        }

        int contractDays = this.CalculateContractDays();
        this.SetActiveContractDeadlineDays(contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.RulerPrisoners;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"{sender.Name} has sent prisoners for a trial CryoRegenesis contract. " +
            $"Their requested regression is six months, one year, or two years. " +
            $"Treat them by {returnText}. A pickup shuttle is called when clients finish regeneration. " +
            "Do not recruit them — death or recruitment will destroy trust and reset the chain.";
        this.DeliverClientsByShuttle(
            map,
            pawns,
            sender,
            "CryoRegenesis contract: prisoners",
            letterBody);
        this.BeginContractQuest(
            "CryoRegenesis: Prisoner trial",
            letterBody,
            sender,
            isPrisoner: true);
    }

    private void StartLeaderContract(Map map)
    {
        const int minimumLeaderAge = 31;
        Pawn leader = this.GetAvailablePlanetaryLeaders(minimumLeaderAge)
            .RandomElementWithFallback(null);
        if (leader == null)
        {
            this.ScheduleRetry(
                "No regional faction leaders",
                this.BuildPlanetaryLeaderFailureReport(minimumLeaderAge));
            return;
        }

        Faction sender = leader.Faction;

        int yearsToRemove = Rand.RangeInclusive(2, 60);
        int targetAgeYears = Math.Max(
            30,
            (leader.ageTracker.AgeBiologicalYears - yearsToRemove) / 5 * 5);
        long targetTicks = targetAgeYears * GenDate.TicksPerYear;
        // Leaders come as guests (not prisoners) with a hard return time.
        List<Pawn> party = new List<Pawn> { leader };
        int contractDays = MinContractDays;
        this.PrepareClient(leader, targetTicks, "planetary ruler", sender, isPrisoner: false, contractDays: contractDays);
        party.AddRange(this.PrepareRomanticPartners(
            leader,
            sender,
            "ruler companion",
            isPrisoner: false,
            contractDays: contractDays));
        contractDays = this.CalculateContractDays();
        this.SetActiveContractDeadlineDays(contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.Leaders;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"{leader.Name.ToStringShort} of {sender.Name}, a ruler over the age of 30, has arrived by shuttle " +
            $"for a privately negotiated CryoRegenesis stay."
            + RoyaltyRegenesisQuestPartners.CompanionArrivalText(party.Count - 1)
            + $" Treat them by {returnText}. A pickup shuttle arrives when they finish (or at the deadline). " +
            "Do not recruit them — death or recruitment will destroy trust and reset the chain.";
        this.DeliverClientsByShuttle(
            map,
            party,
            sender,
            "CryoRegenesis contract: ruler",
            letterBody);
        this.BeginContractQuest(
            "CryoRegenesis: Planetary ruler",
            letterBody,
            sender,
            isPrisoner: false);
    }

    private void StartLowerNobilityContract(Map map)
    {
        Faction empire = this.EmpireFaction();
        if (empire == null)
        {
            this.ScheduleRetry("Empire unavailable", "The Empire could not be found for the lower nobility contract.");
            return;
        }

        // Each noble must be older than the youngest requestable age (21) to have anything to regress.
        List<Pawn> availableNobles = this.GetAvailableWorldPawns(empire, 22);
        if (!availableNobles.Any())
        {
            this.ScheduleRetry("No eligible imperial nobles", "No eligible Imperial world pawns are available for a CryoRegenesis stay. Retrying later.");
            return;
        }

        int pawnCount = Math.Min(Rand.RangeInclusive(2, 10), availableNobles.Count);
        List<Pawn> pawns = new List<Pawn>();
        HashSet<Pawn> party = new HashSet<Pawn>();
        int contractDays = MinContractDays;
        int nobleCount = 0;
        for (int i = 0; i < pawnCount; i++)
        {
            availableNobles.RemoveAll(candidate => party.Contains(candidate));
            if (!availableNobles.Any())
            {
                break;
            }

            Pawn pawn = availableNobles.RandomElement();
            availableNobles.Remove(pawn);
            int targetAge = Rand.RangeInclusive(21, Math.Min(40, pawn.ageTracker.AgeBiologicalYears - 1));
            long targetTicks = targetAge * (long)GenDate.TicksPerYear;
            this.PrepareClient(pawn, targetTicks, "lower imperial noble", empire, isPrisoner: false, contractDays: contractDays);
            pawns.Add(pawn);
            party.Add(pawn);
            nobleCount++;

            // Spouses/lovers ride with their noble rather than as a second independent client.
            foreach (Pawn partner in this.PrepareRomanticPartners(
                pawn,
                empire,
                "noble companion",
                isPrisoner: false,
                contractDays: contractDays,
                alreadyInParty: party))
            {
                pawns.Add(partner);
                party.Add(partner);
                availableNobles.Remove(partner);
            }
        }

        if (nobleCount == 0)
        {
            this.ScheduleRetry("No eligible imperial nobles", "No eligible Imperial world pawns are available for a CryoRegenesis stay. Retrying later.");
            return;
        }

        contractDays = this.CalculateContractDays();
        this.SetActiveContractDeadlineDays(contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.LowerNobility;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Empire has sent {nobleCount} lower nobles by shuttle to verify your CryoRegenesis process. " +
            $"Each noble has chosen their own regression age between 21 and 40."
            + RoyaltyRegenesisQuestPartners.CompanionArrivalText(pawns.Count - nobleCount)
            + $" Treat them by {returnText}. Pickup shuttles are called when clients finish regeneration. Do not recruit them — death or recruitment will destroy trust and reset the chain.";
        this.DeliverClientsByShuttle(
            map,
            pawns,
            empire,
            "Imperial CryoRegenesis contract",
            letterBody);
        this.BeginContractQuest(
            "CryoRegenesis: Imperial nobles",
            letterBody,
            empire,
            isPrisoner: false);
    }

    private void StartStellarchContract(Map map)
    {
        Faction empire = this.EmpireFaction();
        if (empire == null)
        {
            this.ScheduleRetry("Empire unavailable", "The Empire could not be found for the Stellarch contract.");
            return;
        }

        int targetAge = Rand.RangeInclusive(21, 35);
        Pawn stellarch = this.GetAvailableWorldPawns(empire, targetAge + 1).RandomElementWithFallback(null);
        if (stellarch == null)
        {
            this.ScheduleRetry("No eligible Stellarch", "No eligible Imperial world pawn is available for the Stellarch's CryoRegenesis stay. Retrying later.");
            return;
        }

        long targetTicks = targetAge * (long)GenDate.TicksPerYear;
        List<Pawn> party = new List<Pawn> { stellarch };
        int contractDays = MinContractDays;
        this.PrepareClient(stellarch, targetTicks, "stellarch", empire, isPrisoner: false, contractDays: contractDays);
        party.AddRange(this.PrepareRomanticPartners(
            stellarch,
            empire,
            "stellarch companion",
            isPrisoner: false,
            contractDays: contractDays));
        contractDays = this.CalculateContractDays();
        this.SetActiveContractDeadlineDays(contractDays);

        this.activeContractStage = RoyaltyRegenesisStage.StellarchArrival;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Stellarch has arrived by shuttle for CryoRegenesis and has chosen to regress to age {targetAge}."
            + RoyaltyRegenesisQuestPartners.CompanionArrivalText(party.Count - 1)
            + $" Treat them by {returnText}. A pickup shuttle arrives when treatment is finished.";
        this.DeliverClientsByShuttle(
            map,
            party,
            empire,
            "The Stellarch has arrived",
            letterBody);
        this.BeginContractQuest(
            "CryoRegenesis: Stellarch",
            letterBody,
            empire,
            isPrisoner: false);
    }

    private void StartEmperorContract(Map map)
    {
        Faction empire = this.EmpireFaction();
        if (empire == null)
        {
            this.ScheduleRetry("Empire unavailable", "The Empire could not be found for the Emperor contract.");
            return;
        }

        // High Stellarch = Empire faction leader when available; Emperor + wives are
        // always generated (they never exist as world pawns) via SpawnStoryHuman.
        Pawn highStellarch = this.GetFactionLeader(empire, 0);
        RoyaltyEmperor.Party party = RoyaltyEmperor.Build(empire, highStellarch);
        if (party.emperor == null || !party.regenMembers.Any())
        {
            this.ScheduleRetry(
                "Emperor party unavailable",
                "Could not assemble the High Stellarch / Emperor CryoRegenesis party. Retrying later.");
            return;
        }

        this.emperor = party.emperor;
        this.contractEscorts.Clear();
        int contractDays = MinContractDays;

        foreach (RoyaltyEmperor.RegenMember member in party.regenMembers)
        {
            if (member?.pawn == null)
            {
                continue;
            }

            this.PrepareClient(
                member.pawn,
                member.desiredAgeTicks,
                member.role,
                empire,
                isPrisoner: false,
                contractDays: contractDays,
                triggerRoyalAscent: member.triggerRoyalAscent);
        }

        foreach (Pawn escort in party.escorts)
        {
            if (escort == null)
            {
                continue;
            }

            // Guests only — no PrepareClient / no regen tracker.
            this.ApplyGuestOrPrisonerStatus(escort, isPrisoner: false);
            this.LockRecruitment(escort);
            this.contractEscorts.Add(escort);
        }

        contractDays = this.CalculateContractDays();
        this.SetActiveContractDeadlineDays(contractDays);

        this.activeContractStage = RoyaltyRegenesisStage.EmperorArrival;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody = party.BuildLetterBody(returnText);
        this.DeliverClientsByShuttle(
            map,
            party.AllArrivalPawns,
            empire,
            "The High Stellarch and the Emperor have arrived",
            letterBody);
        this.BeginContractQuest(
            "CryoRegenesis: The Emperor",
            letterBody,
            empire,
            isPrisoner: false);
    }

    /// Spouses, fiancés, and lovers for all royalty-chain guest contracts.
    /// Ages come from <see cref="RoyaltyRegenesisQuestPartners"/> (husbands 45, wives/girlfriends 20).
    private List<Pawn> PrepareRomanticPartners(
        Pawn primary,
        Faction faction,
        string role,
        bool isPrisoner,
        int contractDays,
        ICollection<Pawn> alreadyInParty = null)
    {
        List<Pawn> prepared = new List<Pawn>();
        List<RoyaltyRegenesisQuestPartners.PartnerArrival> partners =
            RoyaltyRegenesisQuestPartners.Collect(
                primary,
                (partner, minimumAge) => this.IsAvailableWorldPawn(partner, faction, minimumAge),
                alreadyInParty);

        foreach (RoyaltyRegenesisQuestPartners.PartnerArrival partner in partners)
        {
            this.PrepareClient(
                partner.pawn,
                partner.TargetAgeTicks,
                role,
                faction,
                isPrisoner,
                contractDays);
            prepared.Add(partner.pawn);
        }

        return prepared;
    }

    private void PrepareClient(
        Pawn pawn,
        long desiredAgeTicks,
        string role,
        Faction sourceFaction,
        bool isPrisoner,
        int contractDays,
        bool triggerRoyalAscent = false)
    {
        desiredAgeTicks = Math.Max(0, Math.Min(desiredAgeTicks, pawn.ageTracker.AgeBiologicalTicks));
        TrueAgeTracker tracker = RegenesisThoughts.GetOrAddTrueAgeTracker(pawn, pawn.ageTracker.AgeBiologicalTicks);
        if (tracker != null)
        {
            tracker.desiredAgeTicks = desiredAgeTicks;
            tracker.underRegenContract = true;
            tracker.contractStartAgeTicks = pawn.ageTracker.AgeBiologicalTicks;
        }

        if (isPrisoner || this.CanBeGuestOfPlayer(pawn))
        {
            this.ApplyGuestOrPrisonerStatus(pawn, isPrisoner);
        }

        this.LockRecruitment(pawn);

        if (isPrisoner && !pawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic))
        {
            pawn.health.AddHediff(HediffDefOf.Anesthetic);
        }

        int now = Find.TickManager.TicksGame;
        if (this.contractStartTick < 0)
        {
            this.contractStartTick = now;
        }

        int stayDays = Math.Max(MinContractDays, contractDays);
        // Always a real future TicksGame deadline — never 0 (0 soft-fails immediately).
        int returnByTick = now + stayDays * GenDate.TicksPerDay;

        RoyaltyRegenesisClient client = new RoyaltyRegenesisClient();
        client.pawn = pawn;
        client.desiredAgeTicks = desiredAgeTicks;
        client.role = role;
        client.triggerRoyalAscent = triggerRoyalAscent;
        client.returnByTick = returnByTick;
        client.isPrisoner = isPrisoner;
        client.sourceFaction = sourceFaction;
        client.contractStartRemovedAgeTicks = tracker?.cryoRegenesisRemovedAgeTicks ?? -1L;
        this.activeClients.Add(client);
        this.completionRewardClientCount = this.activeClients.Count;

        this.EnsureContractDeadline();
    }

    /// Syncs <see cref="contractDeadlineTick"/> from client return ticks.
    /// Only repairs clearly invalid (≤ 0) deadlines — does not push a live timer forward.
    private void EnsureContractDeadline()
    {
        this.activeClients.RemoveAll(client => client == null);
        if (!this.activeClients.Any())
        {
            this.ClearContractTiming();
            return;
        }

        int now = Find.TickManager.TicksGame;
        if (this.contractStartTick < 0)
        {
            this.contractStartTick = now;
        }

        int minDeadline = this.contractStartTick + MinContractDays * GenDate.TicksPerDay;

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client == null)
            {
                continue;
            }

            // Unset / corrupt only. Do not rewrite valid future deadlines every tick.
            if (client.returnByTick <= 0)
            {
                client.returnByTick = minDeadline;
            }
        }

        this.contractDeadlineTick = this.activeClients.Max(c => c.returnByTick);
        if (this.contractDeadlineTick < minDeadline)
        {
            this.contractDeadlineTick = minDeadline;
        }
    }

    private void ClearContractTiming()
    {
        this.contractStartTick = -1;
        this.contractDeadlineTick = -1;
        this.pickupShuttleSpawned = false;
        this.pickupShuttleSpawnTick = -1;
        this.pickupWindowEndTick = -1;
        this.pickupAllClientsReady = false;
        this.contractCompletionLogged = false;
        this.clientsHaveArrived = false;
        this.departureSuccessBanked = false;
        this.nextWavePickupTick = -1;
        this.clientsSuccessfullyReturned = 0;
        this.emperorNobleRequirementAnnounced = false;
        this.returnedClientPawnIds.Clear();
        this.contractEscorts.Clear();
        // Keep escapee labels / endgame flag across Clear during finish so a late
        // TryTrigger still sees them; wipe when a brand-new contract begins.
        if (!this.emperorColonistEndgameTriggered)
        {
            this.ClearEmperorShuttleColonistSnapshot();
        }

        this.ResetCompletionRewardState();
    }

    /// Guest of the colony while remaining in <paramref name="pawn"/>'s own faction.
    /// Vanilla refuses Guest status when the pawn is already player-faction or hostile.
    private bool CanBeGuestOfPlayer(Pawn pawn)
    {
        if (pawn?.guest == null || pawn.Faction == null || pawn.Faction == Faction.OfPlayer)
        {
            return false;
        }

        return !pawn.Faction.HostileTo(Faction.OfPlayer);
    }

    private void ApplyGuestOrPrisonerStatus(Pawn pawn, bool isPrisoner)
    {
        if (pawn?.guest == null)
        {
            return;
        }

        if (!isPrisoner && !this.CanBeGuestOfPlayer(pawn))
        {
            return;
        }

#if RIMWORLD12
        pawn.guest.SetGuestStatus(Faction.OfPlayer, isPrisoner);
#else
        pawn.guest.SetGuestStatus(Faction.OfPlayer, isPrisoner ? GuestStatus.Prisoner : GuestStatus.Guest);
#endif

        if (isPrisoner)
        {
#if RIMWORLD12 || RIMWORLD13 || RIMWORLD14
            pawn.guest.interactionMode = PrisonerInteractionModeDefOf.NoInteraction;
#else
            // 1.5+: exclusive interaction modes; block recruitment attempts.
            pawn.guest.SetNoInteraction();
#endif
#if !RIMWORLD12 && !RIMWORLD13
            // Keep resistance high so they never "break" into recruitability.
            if (pawn.guest.resistance < 50f)
            {
                pawn.guest.resistance = 99f;
            }
#endif
        }
        else if (pawn.playerSettings != null)
        {
            // Walking guests default to NoCare; doctors will ignore sedation bills.
            pawn.playerSettings.medCare = MedicalCareCategory.Best;
        }
    }

    private void LockRecruitment(Pawn pawn)
    {
        if (pawn?.guest == null)
        {
            return;
        }

#if !RIMWORLD12 && !RIMWORLD13
        pawn.guest.Recruitable = false;
#endif
    }

    /// Allows enough time for every requested regression run back-to-back in a
    /// single casket at its normal rate, with a handling day per client for
    /// loading, unloading, and treatment setup between sessions.
    private int CalculateContractDays()
    {
        long totalRegressionTicks = this.activeClients
            .Where(client => client?.pawn?.ageTracker != null)
            .Select(client => Math.Max(0L, client.pawn.ageTracker.AgeBiologicalTicks - client.desiredAgeTicks))
            .DefaultIfEmpty(0L)
            .Sum();

        int regressionDays = (int)Math.Ceiling(
            (double)totalRegressionTicks
            / RegressionTicksPerGameTick
            / GenDate.TicksPerDay);

        int handlingDays = ContractHandlingBufferDays * Math.Max(1, this.activeClients.Count);
        return Math.Max(MinContractDays, regressionDays + handlingDays);
    }

    private void SetActiveContractDeadlineDays(int contractDays)
    {
        if (!this.activeClients.Any())
        {
            return;
        }

        int now = Find.TickManager.TicksGame;
        if (this.contractStartTick < 0)
        {
            this.contractStartTick = now;
        }

        int stayDays = Math.Max(MinContractDays, contractDays);
        int returnByTick = this.contractStartTick + stayDays * GenDate.TicksPerDay;
        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client != null)
            {
                client.returnByTick = returnByTick;
            }
        }

        this.contractDeadlineTick = returnByTick;
    }

    private string FormatReturnDeadline(int contractDays)
    {
        int returnTick = Find.TickManager.TicksGame + contractDays * GenDate.TicksPerDay;
        return RoyaltyRegenesisQuestFactory.FormatGameTickDate(returnTick);
    }

    /// Drop-off only (Option C / vanilla hospitality): Arrive → Unload → FlyAway empty.
    /// No ship sits on the pad during treatment. Pickups are called when clients are ready.
    private void DeliverClientsByShuttle(Map map, List<Pawn> pawns, Faction faction, string label, string text)
    {
        if (map == null || pawns == null || !pawns.Any())
        {
            return;
        }

#if RIMWORLD12
        this.DeliverByLegacyShuttle(map, pawns, faction);
#else
        this.DeliverByTransportShip(map, pawns, faction);
#endif

        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent, new LookTargets(pawns));
    }

#if RIMWORLD12
    /// RimWorld 1.2: drop clients and leave — do not park for the whole stay.
    private void DeliverByLegacyShuttle(Map map, List<Pawn> pawns, Faction faction)
    {
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: faction,
            requiredPawns: null,
            leaveImmediatelyWhenSatisfied: true,
            dropEverythingOnArrival: true,
            stayAfterDroppedEverythingOnArrival: false,
            hideControls: true);

        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        if (compShuttle != null)
        {
            // Short leave after unload so the pad frees for other quests / later pickups.
            compShuttle.leaveAfterTicks = GenDate.TicksPerHour;
        }

        CompTransporter transporter = shuttle.TryGetComp<CompTransporter>();
        transporter?.innerContainer.TryAddRangeOrTransfer(pawns.Cast<Thing>(), true, false);

        // Do not retain as contractShuttle — this is drop-off only, not the return ship.
        this.ClearContractShuttle();
        this.pickupShuttleSpawned = false;

        IntVec3 cell = DropCellFinder.TradeDropSpot(map);
        if (!cell.IsValid)
        {
            cell = DropCellFinder.RandomDropSpot(map);
        }

        GenPlace.TryPlaceThing(
            SkyfallerMaker.MakeSkyfaller(ThingDefOf.ShuttleIncoming, shuttle),
            cell,
            map,
            ThingPlaceMode.Near);
    }
#else
    /// RimWorld 1.3+: Arrive → Unload → FlyAway (empty). Pad free during treatment.
    private void DeliverByTransportShip(Map map, List<Pawn> pawns, Faction faction)
    {
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: faction,
            requiredPawns: null,
            hideControls: true);

        TransportShip ship = TransportShipMaker.MakeTransportShip(
            TransportShipDefOf.Ship_Shuttle,
            null,
            shuttle);
        ship.TransporterComp.innerContainer.TryAddRangeOrTransfer(pawns.Cast<Thing>(), true, false);

        ShipJob_Arrive arrive = (ShipJob_Arrive)ShipJobMaker.MakeShipJob(ShipJobDefOf.Arrive);
        arrive.mapParent = map.Parent;
        arrive.factionForArrival = faction ?? Faction.OfPlayer;
        ship.AddJob(arrive);
        ship.AddJob(ShipJobDefOf.Unload);
        // No WaitForever — leave immediately after unload (hospitality drop-off).
        ship.AddJob(ShipJobDefOf.FlyAway);
        ship.Start();

        // Drop-off only: do not track as the return/pickup ship.
        this.ClearContractShuttle();
        this.pickupShuttleSpawned = false;
        this.LogRoyaltyDebug("Delivery transport started (Arrive→Unload→FlyAway, no park).");
    }
#endif

    private void EjectClientsFromCaskets(Map map, List<Pawn> pawns)
    {
        if (map == null || pawns == null)
        {
            return;
        }

        foreach (Building_CryoRegenesis casket in map.listerBuildings.AllBuildingsColonistOfClass<Building_CryoRegenesis>())
        {
            if (casket.ContainedThing is Pawn contained && pawns.Contains(contained))
            {
                casket.EjectContents();
            }
        }
    }

    private void DestroyClientsOffMap(IEnumerable<Pawn> pawns)
    {
        foreach (Pawn pawn in pawns.Where(p => p != null && !p.Destroyed))
        {
            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }

            if (!pawn.Destroyed)
            {
                pawn.Destroy(DestroyMode.Vanish);
            }
        }
    }
}
