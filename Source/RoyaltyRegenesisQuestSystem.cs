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

public partial class RoyaltyRegenesisQuestSystem : GameComponent
{
    private const int CheckIntervalTicks = 2500;
    private const int HalfYearTicks = GenDate.TicksPerYear / 2;
    private const int RecruitGoodwillPenalty = -35;
    private const int DeathTrustGoodwillPenalty = -75;
    private const int MinContractDays = 2;
    private const int ContractHandlingBufferDays = 1;
    private const int RegressionTicksPerGameTick = 500;
    private const int MajorResetMinDays = 60;
    private const int MajorResetMaxDays = 90;
    /// Vanilla hospitality pickup grace period (Script_Hospitality_Worker shuttleLeaveDelayTicks = 3*60000).
    /// Used as a short buffer after the real deadline, not as the early-ready stay time.
    private const int ShuttleLeaveDelayDays = 3;

    /// Delay before a follow-up pickup after a partial return (0 = next ship as soon as the pad is free).
    private const int WavePickupDelayDays = 0;

    /// Minimum how long a "someone is ready" pickup remains parked for manual loading.
    private const int ReadyPickupMinStayDays = 15;

    private RoyaltyRegenesisStage stage = RoyaltyRegenesisStage.NotStarted;
    private RoyaltyRegenesisStage activeContractStage = RoyaltyRegenesisStage.NotStarted;
    private int nextEventTick = -1;
    private int rulerContractsCompleted;
    private int leaderContractsCompleted;
    private int nobleContractsCompleted;
    private bool royalAscentTriggered;
    private int trustBreaks;
    private string lastTrustBreakReason = string.Empty;
    private List<RoyaltyRegenesisClient> activeClients = new List<RoyaltyRegenesisClient>();
    private Quest chainQuest;
    private Quest activeContractQuest;

    /// TicksGame when the current contract was opened.
    private int contractStartTick = -1;

    /// TicksGame when the return/pickup shuttle is due.
    private int contractDeadlineTick = -1;
#if !RIMWORLD12
    /// Active contract shuttle transport (delivery or pickup).
    private TransportShip contractTransportShip;
#endif
    private Thing contractShuttle;

    /// True once the send-off (pickup) shuttle has been spawned for this contract.
    private bool pickupShuttleSpawned;

    /// TicksGame when the pickup shuttle became available for loading.
    private int pickupShuttleSpawnTick = -1;

    /// TicksGame when the current pickup boarding window ends (long-stay early-ready ships).
    private int pickupWindowEndTick = -1;

    /// True once every active client has reached the requested age during pickup.
    private bool pickupAllClientsReady;

    /// True once every client has reached their target age. Success is banked here so
    /// shuttle logistics cannot fail it — the quest is still only finalized on departure.
    private bool contractCompletionLogged;

    /// True once every living client has been map-reachable after delivery unload.
    /// Distinguishes "still arriving" from "left with / after the contract shuttle".
    private bool clientsHaveArrived;

    /// Banked when ages are met (or departure is started after ages met). Survives
    /// client despawn so a scheduled shuttle leave cannot soft-fail a finished treatment.
    private bool departureSuccessBanked;

    /// TicksGame when the next multi-wave pickup shuttle should spawn (-1 = none).
    private int nextWavePickupTick = -1;

    /// Clients who left on a shuttle after reaching their target age (across all waves).
    private int clientsSuccessfullyReturned;

    /// thingIDNumber of pawns that already returned successfully — never re-board or re-spawn them.
    private List<int> returnedClientPawnIds = new List<int>();

    public RoyaltyRegenesisQuestSystem(Game game)
    {
    }

    public IReadOnlyList<RoyaltyRegenesisClient> ActiveClients => this.activeClients;

    public static RoyaltyRegenesisQuestSystem CurrentSystem =>
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>();

    public static bool IsActiveRegenContractPawn(Pawn pawn)
    {
        if (pawn == null)
        {
            return false;
        }

        RoyaltyRegenesisQuestSystem system = CurrentSystem;
        return system != null && system.activeClients.Any(client => client?.pawn == pawn);
    }

    public static RoyaltyRegenesisClient GetActiveClient(Pawn pawn)
    {
        if (pawn == null)
        {
            return null;
        }

        return CurrentSystem?.activeClients.FirstOrDefault(client => client?.pawn == pawn);
    }

    /// True when <paramref name="thing"/> is the parked regen pickup shuttle for the
    /// active contract (not a one-shot delivery drop-off).
    public bool IsRegenPickupShuttle(Thing thing)
    {
        if (thing == null || !this.pickupShuttleSpawned)
        {
            return false;
        }

        Thing shuttle = this.GetContractShuttleThing();
        return shuttle != null && shuttle == thing;
    }

    /// Whether a pawn may board a regen pickup shuttle under colony control.
    /// Active contract clients always may; free colonists only when they have a
    /// non-ex DirectRelation to one of those clients (family / current partners).
    public static bool MayBoardRegenPickupShuttle(Pawn pawn)
    {
        if (pawn == null || pawn.Destroyed || pawn.Dead)
        {
            return false;
        }

        if (IsActiveRegenContractPawn(pawn))
        {
            return true;
        }

        RoyaltyRegenesisQuestSystem system = CurrentSystem;
        if (system == null || system.activeClients == null || !system.activeClients.Any())
        {
            return false;
        }

        IEnumerable<Pawn> clients = system.activeClients
            .Where(client => client?.pawn != null && !client.pawn.Destroyed)
            .Select(client => client.pawn);

        return RoyaltyRegenesisQuestPartners.IsNonExDirectRelationOfAny(pawn, clients);
    }

    /// Called by a CryoRegenesis casket on the exact tick that a pawn reaches its target.
    /// The periodic quest check can otherwise miss that instant after natural aging resumes.
    public static void NotifyRegenesisTargetReached(Pawn pawn)
    {
        RoyaltyRegenesisQuestSystem system = CurrentSystem;
        RoyaltyRegenesisClient client = system?.activeClients
            .FirstOrDefault(candidate => candidate?.pawn == pawn);
        if (client == null || client.everReachedDesiredAge)
        {
            return;
        }

        if (!system.EvaluateAgeAgainstTarget(client, out _, out _))
        {
            return;
        }

        client.everReachedDesiredAge = true;
        system.LogRoyaltyDebug(
            "Client target latched directly from casket: "
            + (pawn?.Name?.ToStringShort ?? "?")
            + " bioTicks=" + (pawn?.ageTracker?.AgeBiologicalTicks ?? -1)
            + " desired=" + client.desiredAgeTicks);
        system.RefreshClientAgeProgress();
    }

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref this.stage, "crRoyalStage", RoyaltyRegenesisStage.NotStarted);
        Scribe_Values.Look(ref this.activeContractStage, "crRoyalActiveContractStage", RoyaltyRegenesisStage.NotStarted);
        Scribe_Values.Look(ref this.nextEventTick, "crRoyalNextEventTick", -1);
        Scribe_Values.Look(ref this.rulerContractsCompleted, "crRoyalRulerContractsCompleted", 0);
        Scribe_Values.Look(ref this.leaderContractsCompleted, "crRoyalLeaderContractsCompleted", 0);
        Scribe_Values.Look(ref this.nobleContractsCompleted, "crRoyalNobleContractsCompleted", 0);
        Scribe_Values.Look(ref this.royalAscentTriggered, "crRoyalAscentTriggered", false);
        Scribe_Values.Look(ref this.trustBreaks, "crRoyalTrustBreaks", 0);
        Scribe_Values.Look(ref this.lastTrustBreakReason, "crRoyalLastTrustBreakReason");
        Scribe_Collections.Look(ref this.activeClients, "crRoyalActiveClients", LookMode.Deep);
        Scribe_References.Look(ref this.chainQuest, "crRoyalChainQuest");
        Scribe_References.Look(ref this.activeContractQuest, "crRoyalActiveContractQuest");
        Scribe_Values.Look(ref this.contractStartTick, "crRoyalContractStartTick", -1);
        Scribe_Values.Look(ref this.contractDeadlineTick, "crRoyalContractDeadlineTick", -1);
        Scribe_Values.Look(ref this.pickupShuttleSpawned, "crRoyalPickupShuttleSpawned", false);
        Scribe_Values.Look(ref this.pickupShuttleSpawnTick, "crRoyalPickupShuttleSpawnTick", -1);
        Scribe_Values.Look(ref this.pickupWindowEndTick, "crRoyalPickupWindowEndTick", -1);
        Scribe_Values.Look(ref this.pickupAllClientsReady, "crRoyalPickupAllClientsReady", false);
        Scribe_Values.Look(ref this.contractCompletionLogged, "crRoyalContractCompletionLogged", false);
        Scribe_Values.Look(ref this.clientsHaveArrived, "crRoyalClientsHaveArrived", false);
        Scribe_Values.Look(ref this.departureSuccessBanked, "crRoyalDepartureSuccessBanked", false);
        Scribe_Values.Look(ref this.nextWavePickupTick, "crRoyalNextWavePickupTick", -1);
        Scribe_Values.Look(ref this.clientsSuccessfullyReturned, "crRoyalClientsSuccessfullyReturned", 0);
        Scribe_Collections.Look(ref this.returnedClientPawnIds, "crRoyalReturnedClientPawnIds", LookMode.Value);
#if !RIMWORLD12
        Scribe_References.Look(ref this.contractTransportShip, "crRoyalContractTransportShip");
#endif
        Scribe_References.Look(ref this.contractShuttle, "crRoyalContractShuttle");
        this.ExposeCompletionRewardData();

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            if (this.activeClients == null)
            {
                this.activeClients = new List<RoyaltyRegenesisClient>();
            }

            if (this.returnedClientPawnIds == null)
            {
                this.returnedClientPawnIds = new List<int>();
            }

            if (this.lastTrustBreakReason == null)
            {
                this.lastTrustBreakReason = string.Empty;
            }

            if (this.activeClients.Any())
            {
                this.EnsureContractDeadline();
                // Older saves lack this flag; treat currently map-reachable clients as arrived.
                if (!this.clientsHaveArrived
                    && this.activeClients.All(c => c?.pawn != null && this.IsClientAvailableOnMap(c.pawn)))
                {
                    this.clientsHaveArrived = true;
                }
            }
        }
    }

    public override void GameComponentTick()
    {
        base.GameComponentTick();

        if (!ModsConfig.RoyaltyActive)
        {
            return;
        }

        if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
        {
            return;
        }

        // Always process existing clients/contracts even if the last casket
        // has been destroyed or uninstalled. Deadlines, deaths, and cleanup
        // must still run.
        this.CheckActiveClients();

        if (this.activeClients.Any())
        {
            return;
        }

        // Casket (and other campaign prerequisites) only gate *starting*
        // or advancing new campaign stages, not the processing of an
        // already-active contract.
        if (!this.CanRunCampaign())
        {
            return;
        }

        int ticksGame = Find.TickManager.TicksGame;
        if (this.stage == RoyaltyRegenesisStage.NotStarted)
        {
            // Other planetary factions send clients first. The Empire only
            // notices after those contracts succeed.
            this.stage = RoyaltyRegenesisStage.RulerPrisoners;
            this.nextEventTick = ticksGame + Rand.RangeInclusive(3, 8) * GenDate.TicksPerDay;
            this.EnsureChainQuest();
        }
        else if (this.stage != RoyaltyRegenesisStage.Completed)
        {
            this.EnsureChainQuest();
        }

        if (this.stage != RoyaltyRegenesisStage.Completed && ticksGame >= this.nextEventTick)
        {
            this.AdvanceCampaign();
        }
    }

    private bool CanRunCampaign()
    {
        return Find.Maps.Any(map =>
            map.IsPlayerHome &&
            map.listerBuildings.AllBuildingsColonistOfClass<Building_CryoRegenesis>().Any());
    }

    private Map GetTargetMap()
    {
        return Find.Maps.FirstOrDefault(map =>
            map.IsPlayerHome &&
            map.listerBuildings.AllBuildingsColonistOfClass<Building_CryoRegenesis>().Any());
    }

    private void AdvanceCampaign()
    {
        Map map = this.GetTargetMap();
        if (map == null)
        {
            this.nextEventTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
            return;
        }

        switch (this.stage)
        {
            case RoyaltyRegenesisStage.RulerPrisoners:
                this.StartRulerPrisonerContract(map);
                break;
            case RoyaltyRegenesisStage.Leaders:
                this.StartLeaderContract(map);
                break;
            case RoyaltyRegenesisStage.LowerNobility:
                this.StartLowerNobilityContract(map);
                break;
            case RoyaltyRegenesisStage.StellarchNotice:
                this.SendTravelNotice(
                    "The Stellarch has taken notice",
                    "Reports of successful CryoRegenesis treatments have reached the Stellarch. An imperial shuttle has departed. It should arrive in several days.");
                this.stage = RoyaltyRegenesisStage.StellarchArrival;
                this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(3, 8) * GenDate.TicksPerDay;
                break;
            case RoyaltyRegenesisStage.StellarchArrival:
                this.StartStellarchContract(map);
                break;
            case RoyaltyRegenesisStage.EmperorNotice:
                int years = Rand.RangeInclusive(1, 10);
                this.SendTravelNotice(
                    "The Emperor is coming",
                    $"The restored Stellarch's report has reached the Emperor. The imperial household has committed to the journey, but interstellar travel will take {years} year(s).");
                this.stage = RoyaltyRegenesisStage.EmperorArrival;
                this.nextEventTick = Find.TickManager.TicksGame + years * GenDate.TicksPerYear;
                break;
            case RoyaltyRegenesisStage.EmperorArrival:
                this.StartEmperorContract(map);
                break;
        }
    }

    public void DebugStartAt(RoyaltyRegenesisStage debugStage)
    {
        if (!ModsConfig.RoyaltyActive)
        {
            Messages.Message("CryoRegenesis Royalty quest chain requires the Royalty DLC.", MessageTypeDefOf.RejectInput);
            return;
        }

        if (this.GetTargetMap() == null)
        {
            Messages.Message("Build a CryoRegenesis casket on a player home map before starting the Royalty quest chain.", MessageTypeDefOf.RejectInput);
            return;
        }

        this.EndActiveContractQuest(QuestEndOutcome.Fail, sendLetter: false);
        this.activeClients.Clear();
        this.ClearContractShuttle();
        this.ClearContractTiming();
        this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
        this.stage = debugStage;
        this.nextEventTick = Find.TickManager.TicksGame;
        this.EnsureChainQuest();
        this.AdvanceCampaign();
    }

    public void DebugTriggerNextEvent()
    {
        if (!ModsConfig.RoyaltyActive)
        {
            Messages.Message("CryoRegenesis Royalty quest chain requires the Royalty DLC.", MessageTypeDefOf.RejectInput);
            return;
        }

        if (this.GetTargetMap() == null)
        {
            Messages.Message("Build a CryoRegenesis casket on a player home map before triggering the next Royalty quest event.", MessageTypeDefOf.RejectInput);
            return;
        }

        this.nextEventTick = Find.TickManager.TicksGame;
        this.AdvanceCampaign();
    }

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
        Faction sender = this.RandomPlanetarySenderFaction();
        if (sender == null)
        {
            this.ScheduleRetry(
                "No eligible factions",
                "No non-Empire, non-Ancient planetary leaders are available for a CryoRegenesis stay. Retrying later.");
            return;
        }

        Pawn leader = this.GetFactionLeader(sender, 31);
        if (leader == null)
        {
            this.ScheduleRetry(
                "No eligible faction ruler",
                $"{sender.Name}'s ruler is unavailable for a CryoRegenesis stay. Retrying later.");
            return;
        }

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

        Pawn emperor = this.GetFactionLeader(empire, 21);
        if (emperor == null)
        {
            this.ScheduleRetry("Emperor unavailable", "The Empire's leader is unavailable for a CryoRegenesis stay. Retrying later.");
            return;
        }

        List<Pawn> party = new List<Pawn> { emperor };
        long targetTicks = 20L * GenDate.TicksPerYear;
        int contractDays = MinContractDays;
        this.PrepareClient(emperor, targetTicks, "emperor", empire, isPrisoner: false, contractDays: contractDays, triggerRoyalAscent: true);
        party.AddRange(this.PrepareRomanticPartners(
            emperor,
            empire,
            "imperial companion",
            isPrisoner: false,
            contractDays: contractDays));
        contractDays = this.CalculateContractDays();
        this.SetActiveContractDeadlineDays(contractDays);

        this.activeContractStage = RoyaltyRegenesisStage.EmperorArrival;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Emperor has arrived by shuttle for CryoRegenesis and will regress to age 20."
            + RoyaltyRegenesisQuestPartners.CompanionArrivalText(party.Count - 1)
            + $" Treat them by {returnText}. A pickup shuttle arrives when treatment is finished.";
        this.DeliverClientsByShuttle(
            map,
            party,
            empire,
            "The Emperor has arrived",
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

        this.ApplyGuestOrPrisonerStatus(pawn, isPrisoner);
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
        this.activeClients.Add(client);
        this.completionRewardClientCount = this.activeClients.Count;

        this.EnsureContractDeadline();
    }

    /// Syncs <see cref="contractDeadlineTick"/> from client return ticks.
    /// Only repairs clearly invalid (≤ 0) deadlines — does not push a live timer forward.
    private void EnsureContractDeadline()
    {
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
        this.returnedClientPawnIds.Clear();
        this.ResetCompletionRewardState();
    }

    private void ApplyGuestOrPrisonerStatus(Pawn pawn, bool isPrisoner)
    {
        if (pawn?.guest == null)
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
            owningFaction: null,
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
            owningFaction: null,
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

    private void CheckActiveClients()
    {
        if (!this.activeClients.Any())
        {
            return;
        }

        // Refresh latches before any success/fail decisions (including destroy/leave).
        this.RefreshClientAgeProgress();
        this.UpdateClientsArrivedFlag();

        // Death always hard-resets trust (before shuttle-leave accounting, which may
        // destroy boarding passengers).
        if (this.activeClients.Any(client => client.pawn != null && client.pawn.Dead))
        {
            Pawn dead = this.activeClients.First(client => client.pawn != null && client.pawn.Dead).pawn;
            Faction sender = this.activeClients.FirstOrDefault()?.sourceFaction;
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
            // or the deadline forces a last call.
            if (allDone || deadlineReached)
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

            if (this.IsClientAvailableOnMap(client.pawn))
            {
                remaining.Add(client);
            }
            else
            {
                // Aboard the leaving shuttle, skyfaller, or other off-map transport.
                departed.Add(client);
            }
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
                        && !this.WasSuccessfullyReturned(c.pawn)
                        && this.IsClientAvailableOnMap(c.pawn))
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
    /// (active clients and non-ex DirectRelations only).
    private void ConfigureContractShuttleEmbarkRules(CompShuttle compShuttle)
    {
        if (compShuttle == null)
        {
            return;
        }

        // Quest lodgers embark only if they are requiredPawns or acceptColonists is true.
        // acceptColonists also opens the door to free colonists — Patch_CompShuttle_IsAllowed
        // keeps unrelated colonists off regen pickups.
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
            required = ready;
        }
        else
        {
            // Nobody ready yet: keep full map-held list so the delivery ship cannot be
            // Sent away empty mid-contract.
            required = this.GetMapHeldContractPawns();
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
                : " map-held (nobody ready yet)."));
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

            // Re-latch if the casket notification was missed so ready nobles are not locked out.
            if (!client.everReachedDesiredAge
                && this.EvaluateAgeAgainstTarget(client, out _, out _))
            {
                client.everReachedDesiredAge = true;
            }

            if (!client.everReachedDesiredAge)
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

    /// Vanilla Royalty hospitality guests are temporary player-faction pawns whose
    /// original faction is retained by QuestPart_ExtraFaction. A guest tracker alone
    /// only creates an uncontrolled visitor, with no bed assignment or Operations UI.
    private void EnsureGuestClientsAreQuestLodgers()
    {
        if (this.pickupShuttleSpawned || this.activeContractQuest == null || this.activeContractQuest.Historical)
        {
            return;
        }

        foreach (IGrouping<Faction, RoyaltyRegenesisClient> factionClients in this.activeClients
                     .Where(client => client != null && !client.isPrisoner && client.pawn != null)
                     .GroupBy(client => client.sourceFaction))
        {
            Faction homeFaction = factionClients.Key;
            List<Pawn> pawns = factionClients.Select(client => client.pawn).Distinct().ToList();
            QuestPart_ExtraFaction extraFactionPart = this.activeContractQuest.PartsListForReading
                .OfType<QuestPart_ExtraFaction>()
                .FirstOrDefault(part =>
                    part.extraFaction != null &&
                    part.extraFaction.faction == homeFaction &&
                    part.extraFaction.factionType == ExtraFactionType.HomeFaction);

            if (extraFactionPart == null)
            {
                extraFactionPart = new QuestPart_ExtraFaction
                {
                    extraFaction = new ExtraFaction(homeFaction, ExtraFactionType.HomeFaction),
                    affectedPawns = new List<Pawn>(pawns)
                };
                this.activeContractQuest.AddPart(extraFactionPart);
            }
            else
            {
                extraFactionPart.affectedPawns.AddRange(
                    pawns.Where(pawn => !extraFactionPart.affectedPawns.Contains(pawn)));
            }

            foreach (Pawn pawn in pawns)
            {
                this.ReleaseFromCurrentLord(pawn);
                if (pawn.Faction != Faction.OfPlayer)
                {
                    pawn.SetFaction(Faction.OfPlayer);
                }
            }
        }
    }

    /// Returns guest lodgers to their home faction after the contract ends.
    /// Must run while client references still exist (before <see cref="activeClients"/> is cleared).
    ///
    /// Do NOT call this at boarding time: SetFaction away from the player turns a temporary
    /// colonist-lodger (e.g. a planetary ruler) into a foreign Town Councilman mid-leave,
    /// which breaks ExitOnShuttle / CompShuttle loading and soft-fails successful contracts.
    /// Vanilla hospitality keeps ExtraFaction lodgers as OfPlayer until the quest cleans up.
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
    /// Success if ages were banked/latched or multi-wave returns covered the full party;
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
        // their target. Do not let a final-wave banked latch wipe an earlier unfinished leaver.
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
            bool latchedSuccess = this.IsDepartureSuccessBanked()
                || (this.activeClients.Any()
                    && this.activeClients.All(c => c != null && c.everReachedDesiredAge));
            if (latchedSuccess != success)
            {
                this.LogRoyaltyDebug(
                    "Finish success override: arg=" + success + " → latched=" + latchedSuccess);
                success = latchedSuccess;
            }
        }

        RoyaltyRegenesisStage finishedStage = this.activeContractStage;
        bool triggerEndgame = this.activeClients.Any(client => client != null && client.triggerRoyalAscent);

        this.LogRoyaltyDebug(
            "FinishContractAfterDeparture success=" + success
            + " stage=" + finishedStage
            + " clients=" + this.activeClients.Count
            + " triggerRoyalAscent=" + triggerEndgame
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
            this.LogRoyaltyDebug("Outcome: SUCCESS + Royal Ascent endgame (shuttle departed).");
            this.activeContractStage = finishedStage;
            this.GrantCompletionRewardIfEligible(finishedStage);
            this.EndActiveContractQuest(QuestEndOutcome.Success);
            this.stage = RoyaltyRegenesisStage.Completed;
            this.royalAscentTriggered = true;
            this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
            this.CompleteChainQuest();
            this.TryMakeRoyalAscentAvailable();
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
            longStay
                ? "A shuttle has arrived because at least one CryoRegenesis client finished treatment. "
                  + "It will stay for many days — load ready clients when you want and Send. "
                  + "Unfinished clients can keep regenerating."
                : "A shuttle has arrived for finished CryoRegenesis clients. "
                  + "Load ready clients onto it yourself (unfinished clients can stay for a later pickup).",
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
            longStay
                ? "A shuttle has arrived because at least one CryoRegenesis client finished treatment. "
                  + "It will stay for many days — load ready clients when you want and Send. "
                  + "Unfinished clients can keep regenerating."
                : "A shuttle has arrived for finished CryoRegenesis clients. "
                  + "Load ready clients onto it yourself (unfinished clients can stay for a later pickup).",
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

    /// Recompute age latches for every living client and update <see cref="pickupAllClientsReady"/>.
    /// Once a client has ever reached the contracted target, that fact is sticky for the contract.
    private void RefreshClientAgeProgress()
    {
        if (!this.activeClients.Any())
        {
            return;
        }

        bool anyNew = false;
        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client == null)
            {
                continue;
            }

            if (client.everReachedDesiredAge)
            {
                continue;
            }

            if (this.EvaluateAgeAgainstTarget(client, out _, out _))
            {
                client.everReachedDesiredAge = true;
                anyNew = true;
                Pawn p = client.pawn;
                this.LogRoyaltyDebug(
                    "Client FIRST reached target: "
                    + (p?.Name?.ToStringShort ?? "?")
                    + " bioTicks=" + (p?.ageTracker?.AgeBiologicalTicks ?? -1)
                    + " desired=" + client.desiredAgeTicks
                    + " years=" + (p?.ageTracker?.AgeBiologicalYears.ToString() ?? "?")
                    + "→" + (client.desiredAgeTicks / GenDate.TicksPerYear));

                if (p != null && !p.Destroyed)
                {
                    Find.LetterStack.ReceiveLetter(
                        "Regenesis client ready",
                        p.Name.ToStringShort + " has reached the contracted target age of "
                        + ((float)client.desiredAgeTicks / GenDate.TicksPerYear).ToString("0.#")
                        + ". Their part of the contract is fulfilled.",
                        LetterDefOf.PositiveEvent,
                        new LookTargets(p));
                }
            }
        }

        bool allEver = this.activeClients.All(c => c != null && c.everReachedDesiredAge);
        if (allEver && !this.IsDepartureSuccessBanked())
        {
            this.LogRoyaltyDebug("All clients have latched everReachedDesiredAge → banking departure success.");
            this.MarkContractQuestCompleted();
        }
        else if (anyNew)
        {
            this.LogRoyaltyDebug(
                "Age progress: ready "
                + this.activeClients.Count(c => c.everReachedDesiredAge)
                + "/" + this.activeClients.Count
                + " banked=" + this.departureSuccessBanked
                + " pickupAllClientsReady=" + this.pickupAllClientsReady);
        }
    }

    /// Banks success the moment every client has reached their target age.
    /// The quest stays open until the shuttle departs — then it is finalized as success.
    private void MarkContractQuestCompleted()
    {
        bool alreadyBanked = this.IsDepartureSuccessBanked();
        this.BankDepartureSuccess("all clients reached target age (stage=" + this.activeContractStage + ")");

        if (alreadyBanked)
        {
            return;
        }

        this.LogRoyaltyDebug(
            "All clients reached target age → success banked. Quest finalizes when the shuttle departs.");

        Find.LetterStack.ReceiveLetter(
            "CryoRegenesis treatment complete",
            "Every CryoRegenesis client has reached their requested age. "
            + "The contract will succeed once their shuttle departs with them.",
            LetterDefOf.PositiveEvent);
    }

    /// True when this client has ever met the contracted age (sticky), or currently meets it.
    private bool ClientReachedDesiredAge(RoyaltyRegenesisClient client)
    {
        if (client == null)
        {
            return false;
        }

        if (client.everReachedDesiredAge)
        {
            return true;
        }

        if (this.EvaluateAgeAgainstTarget(client, out _, out _))
        {
            client.everReachedDesiredAge = true;
            return true;
        }

        return false;
    }

    /// Current biological age is at or below the contracted target, forgiving natural
    /// aging since the contract began.
    ///
    /// The casket ejects a client the same tick it clamps them at the exact target, and
    /// they age +1 tick per tick from then on — so an exact comparison can only succeed
    /// on that single tick. The casket latches that instant via
    /// <see cref="NotifyRegenesisTargetReached"/>, but if it is ever missed (pawn already
    /// ejected on load, older build, power loss on the boundary tick) an exact check can
    /// never latch again and the contract is unwinnable. Since a pawn ages at most
    /// (now - contractStart) ticks during the contract, anyone who ever reached the
    /// target still satisfies current <= target + elapsed, while a client whose
    /// regression was never finished remains years over the allowance.
    private bool EvaluateAgeAgainstTarget(
        RoyaltyRegenesisClient client,
        out long currentTicks,
        out long allowedTicks)
    {
        currentTicks = -1;
        allowedTicks = -1;

        if (client?.pawn?.ageTracker == null)
        {
            return false;
        }

        long agingAllowance = CheckIntervalTicks;
        if (this.contractStartTick >= 0)
        {
            agingAllowance += Math.Max(0, Find.TickManager.TicksGame - this.contractStartTick);
        }

        currentTicks = client.pawn.ageTracker.AgeBiologicalTicks;
        allowedTicks = client.desiredAgeTicks + agingAllowance;
        return currentTicks <= allowedTicks;
    }

    private void LogRoyaltyDebug(string message)
    {
        Log.Message("[CryoRegenesis Royalty] t=" + Find.TickManager.TicksGame + " " + message);
    }

    private void LogClientAgeSnapshot(string context)
    {
        int now = Find.TickManager.TicksGame;
        long elapsed = this.contractStartTick >= 0 ? Math.Max(0, now - this.contractStartTick) : -1;
        this.LogRoyaltyDebug(
            "Age snapshot (" + context + "): clients=" + this.activeClients.Count
            + " start=" + this.contractStartTick
            + " deadline=" + this.contractDeadlineTick
            + " elapsedTicks=" + elapsed
            + " elapsedDays=" + (elapsed >= 0 ? (elapsed / (float)GenDate.TicksPerDay).ToString("0.00") : "?")
            + " pickupSpawned=" + this.pickupShuttleSpawned
            + " allReady=" + this.pickupAllClientsReady);

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client == null)
            {
                this.LogRoyaltyDebug("  • (null client entry)");
                continue;
            }

            Pawn pawn = client.pawn;
            if (pawn == null || pawn.Destroyed)
            {
                this.LogRoyaltyDebug(
                    "  • " + (client.role ?? "?")
                    + " pawn=null/destroyed everReached=" + client.everReachedDesiredAge
                    + " desiredTicks=" + client.desiredAgeTicks);
                continue;
            }

            bool meets = this.EvaluateAgeAgainstTarget(client, out long cur, out long allowed);
            string holder = pawn.ParentHolder != null ? pawn.ParentHolder.GetType().Name : "none";
            this.LogRoyaltyDebug(
                "  • " + pawn.Name.ToStringShort
                + " role=" + (client.role ?? "?")
                + " bioYears=" + pawn.ageTracker.AgeBiologicalYearsFloat.ToString("0.000")
                + " targetYears=" + ((double)client.desiredAgeTicks / GenDate.TicksPerYear).ToString("0.000")
                + " bioTicks=" + cur
                + " desiredTicks=" + client.desiredAgeTicks
                + " allowedTicks=" + allowed
                + " overBy=" + (cur - client.desiredAgeTicks)
                + " meetsNow=" + meets
                + " everReached=" + client.everReachedDesiredAge
                + " dead=" + pawn.Dead
                + " spawned=" + pawn.Spawned
                + " onMap=" + this.IsClientAvailableOnMap(pawn)
                + " inTransport=" + this.IsClientInReturnTransport(pawn)
                + " holder=" + holder);
        }
    }

    private void CompleteActiveContract()
    {
        switch (this.activeContractStage)
        {
            case RoyaltyRegenesisStage.RulerPrisoners:
                this.rulerContractsCompleted++;
                if (this.rulerContractsCompleted < 3)
                {
                    this.stage = RoyaltyRegenesisStage.RulerPrisoners;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(15, 35) * GenDate.TicksPerDay;
                }
                else
                {
                    this.stage = RoyaltyRegenesisStage.Leaders;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(25, 45) * GenDate.TicksPerDay;
                }
                break;
            case RoyaltyRegenesisStage.Leaders:
                this.leaderContractsCompleted++;
                if (this.leaderContractsCompleted < 2)
                {
                    this.stage = RoyaltyRegenesisStage.Leaders;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
                }
                else
                {
                    // Only after other factions succeed does the Empire take interest.
                    this.stage = RoyaltyRegenesisStage.LowerNobility;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
                }
                break;
            case RoyaltyRegenesisStage.LowerNobility:
                this.nobleContractsCompleted++;
                if (this.nobleContractsCompleted < 2)
                {
                    this.stage = RoyaltyRegenesisStage.LowerNobility;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
                }
                else
                {
                    this.stage = RoyaltyRegenesisStage.StellarchNotice;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(15, 30) * GenDate.TicksPerDay;
                }
                break;
            case RoyaltyRegenesisStage.StellarchArrival:
                this.stage = RoyaltyRegenesisStage.EmperorNotice;
                this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 90) * GenDate.TicksPerDay;
                break;
        }

        this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
        Find.LetterStack.ReceiveLetter(
            "CryoRegenesis contract complete",
            "The shuttle has departed with the CryoRegenesis clients after they reached the requested "
            + "regression age — the contract is fulfilled. Check the Quests tab for chain progress.",
            LetterDefOf.PositiveEvent);
    }

    private void ScheduleRetry(string label, string text)
    {
        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NegativeEvent);
        this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
        this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
    }

    private void SendTravelNotice(string label, string text)
    {
        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent);
        this.EnsureChainQuest();
    }

    public void NotifyClientRecruited(Pawn recruitee)
    {
        RoyaltyRegenesisClient client = this.activeClients.FirstOrDefault(c => c?.pawn == recruitee);
        if (client == null)
        {
            return;
        }

        Faction faction = client.sourceFaction;

        // Return any remaining contracted clients; the recruited one stays as a colonist.
        List<Pawn> survivors = this.activeClients
            .Where(c => c?.pawn != null && c.pawn != recruitee && !c.pawn.Dead)
            .Select(c => c.pawn)
            .ToList();
        Map map = survivors.FirstOrDefault(p => p.MapHeld != null)?.MapHeld ?? this.GetTargetMap();
        if (map != null && survivors.Any())
        {
            this.DepartClients(map, survivors, faction);
        }

        this.MajorTrustReset(
            $"{recruitee.Name.ToStringShort} was recruited while under a CryoRegenesis return contract. " +
            $"{(faction?.Name ?? "Their faction")} considers this a betrayal — trust is lost and the chain resets.",
            faction,
            "Contract client recruited");
    }

    public string GetChainProgressDescription()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(this.GetStageLabel(this.stage));
        sb.AppendLine();
        sb.AppendLine("Progress:");
        sb.AppendLine("• Prisoner trials: " + this.rulerContractsCompleted + " / 3");
        sb.AppendLine("• Planetary rulers: " + this.leaderContractsCompleted + " / 2");
        sb.AppendLine("• Imperial nobles: " + this.nobleContractsCompleted + " / 2");
        sb.AppendLine("• Stellarch: " + (this.stage > RoyaltyRegenesisStage.StellarchArrival || this.stage == RoyaltyRegenesisStage.Completed ? "done" : "pending"));
        sb.AppendLine("• Emperor: " + (this.stage == RoyaltyRegenesisStage.Completed ? "done" : "pending"));

        if (this.trustBreaks > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Trust breaks: " + this.trustBreaks);
            if (!this.lastTrustBreakReason.NullOrEmpty())
            {
                sb.AppendLine("Last setback: " + this.lastTrustBreakReason);
            }
        }

        if (this.activeClients.Any())
        {
            sb.AppendLine();
            sb.AppendLine("Active contract clients: " + this.activeClients.Count);
            int deadline = this.contractDeadlineTick > 0
                ? this.contractDeadlineTick
                : this.activeClients.Min(c => c.returnByTick);
            sb.AppendLine("Shuttle departs: " + RoyaltyRegenesisQuestFactory.FormatGameTickDate(deadline));
        }
        else if (this.stage != RoyaltyRegenesisStage.Completed && this.nextEventTick > Find.TickManager.TicksGame)
        {
            int days = (this.nextEventTick - Find.TickManager.TicksGame + GenDate.TicksPerDay - 1) / GenDate.TicksPerDay;
            sb.AppendLine();
            sb.AppendLine("Next event in about " + days + " day(s).");
        }

        return sb.ToString().TrimEnd();
    }

    public string GetContractProgressDescription()
    {
        if (!this.activeClients.Any())
        {
            return "No active CryoRegenesis clients on this contract.";
        }

        StringBuilder sb = new StringBuilder();
        int deadline = this.contractDeadlineTick > 0
            ? this.contractDeadlineTick
            : this.activeClients.Min(c => c.returnByTick);
        int ticksLeft = Math.Max(0, deadline - Find.TickManager.TicksGame);
        if (this.pickupShuttleSpawned)
        {
            int windowEnd = this.pickupWindowEndTick > 0
                ? this.pickupWindowEndTick
                : this.pickupShuttleSpawnTick + this.GetReadyPickupStayTicks();
            int windowLeft = Math.Max(0, windowEnd - Find.TickManager.TicksGame);
            sb.AppendLine(
                "Shuttle: parked for ready clients (no auto-board). Load and Send when you want. "
                + "Boarding window ~" + windowLeft.ToStringTicksToPeriod() + ".");
        }
        else if (this.nextWavePickupTick > 0)
        {
            int waveLeft = Math.Max(0, this.nextWavePickupTick - Find.TickManager.TicksGame);
            sb.AppendLine(
                "Next pickup shuttle: "
                + RoyaltyRegenesisQuestFactory.FormatGameTickDate(this.nextWavePickupTick)
                + " (" + waveLeft.ToStringTicksToPeriod() + ")");
        }
        else
        {
            sb.AppendLine(
                "Contract deadline: " + RoyaltyRegenesisQuestFactory.FormatGameTickDate(deadline)
                + " (" + ticksLeft.ToStringTicksToPeriod() + " remaining)");
            sb.AppendLine("No ship on the pad during treatment. A pickup is called when any client hits target age.");
        }

        if (this.clientsSuccessfullyReturned > 0 || this.completionRewardClientCount > 1)
        {
            sb.AppendLine(
                "Returned ready: " + this.clientsSuccessfullyReturned
                + " / " + Math.Max(this.completionRewardClientCount, this.activeClients.Count
                    + this.clientsSuccessfullyReturned));
        }

        sb.AppendLine();

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client?.pawn == null)
            {
                continue;
            }

            Pawn pawn = client.pawn;
            float currentYears = pawn.ageTracker.AgeBiologicalYearsFloat;
            double targetYears = (double)client.desiredAgeTicks / GenDate.TicksPerYear;
            bool done = this.ClientReachedDesiredAge(client);
            string location = pawn.ParentHolder is Building_CryoRegenesis
                ? "in CryoRegenesis"
                : (pawn.Spawned ? "on map" : "held");
            TrueAgeTracker tracker = pawn.health?.hediffSet?
                .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
            string percent = tracker != null
                ? " (" + tracker.GetContractProgressPercent().ToString("0") + "% there)"
                : string.Empty;

            sb.AppendLine("• " + pawn.Name.ToStringShort
                + " — bio " + currentYears.ToString("0.00") + " → " + targetYears.ToString("0.00")
                + percent
                + (done ? " [ready]" : " [treating]")
                + (client.everReachedDesiredAge && !done ? " [was ready]" : "")
                + " (" + location + ")");
        }

        sb.AppendLine();
        sb.AppendLine(this.IsDepartureSuccessBanked() || this.activeClients.All(this.ClientReachedDesiredAge)
            ? "All remaining clients ready — load them on the shuttle to finish the contract."
            : "Ready clients may leave early on the shuttle; unfinished clients stay for a later pickup.");
        if (this.IsDepartureSuccessBanked())
        {
            sb.AppendLine("Treatment success is banked for remaining clients. The quest finalizes when the last shuttle leaves.");
        }

        return sb.ToString().TrimEnd();
    }

    private void EnsureChainQuest()
    {
        if (this.chainQuest != null && !this.chainQuest.Historical)
        {
            return;
        }

        // Recover an existing ongoing chain quest after load if needed.
        Quest existing = Find.QuestManager.QuestsListForReading.FirstOrDefault(q =>
            q != null &&
            !q.Historical &&
            q.root != null &&
            q.root.defName == RoyaltyRegenesisQuestFactory.ChainQuestDefName);

        if (existing != null)
        {
            this.chainQuest = existing;
            return;
        }

        this.chainQuest = RoyaltyRegenesisQuestFactory.MakeChainQuest();
    }

    private void BeginContractQuest(string title, string roleSummary, Faction sender, bool isPrisoner)
    {
        this.EnsureChainQuest();
        this.EndActiveContractQuest(QuestEndOutcome.Fail, sendLetter: false);

        if (this.contractStartTick < 0)
        {
            this.contractStartTick = Find.TickManager.TicksGame;
        }

        this.EnsureContractDeadline();
        this.pickupShuttleSpawned = false;

        int returnByTick = this.contractDeadlineTick > 0
            ? this.contractDeadlineTick
            : Find.TickManager.TicksGame + MinContractDays * GenDate.TicksPerDay;

        string description = RoyaltyRegenesisQuestFactory.BuildContractDescription(
            roleSummary,
            sender,
            isPrisoner,
            returnByTick,
            this.activeClients.Select(c => c.pawn));

        this.activeContractQuest = RoyaltyRegenesisQuestFactory.MakeContractQuest(
            title,
            description,
            this.chainQuest);

        if (!isPrisoner)
        {
            this.EnsureGuestClientsAreQuestLodgers();
        }
    }

    private void EndActiveContractQuest(QuestEndOutcome outcome, bool sendLetter = true)
    {
        this.RestoreGuestClientFactions();
        RoyaltyRegenesisQuestFactory.EndQuestSafe(this.activeContractQuest, outcome, sendLetter);
        this.activeContractQuest = null;
    }

    private void CompleteChainQuest()
    {
        this.EnsureChainQuest();
        if (this.chainQuest != null && !this.chainQuest.Historical)
        {
            this.chainQuest.description =
                RoyaltyRegenesisQuestFactory.BuildChainDescriptionBody()
                + "\n\nThe Emperor has been rejuvenated. The Imperial Rejuvenation chain is complete.";
            RoyaltyRegenesisQuestFactory.EndQuestSafe(this.chainQuest, QuestEndOutcome.Success);
        }

        this.chainQuest = null;
    }

    /// Death, recruitment, or loss of clients: fail the contract, wipe chain progress,
    /// and force a long cooldown before anyone will trust you again.
    private void MajorTrustReset(string letterText, Faction offendedFaction, string shortReason)
    {
        this.EndActiveContractQuest(QuestEndOutcome.Fail);

        this.ClearContractFlags(this.activeClients);
        this.activeClients.Clear();
        this.ClearContractShuttle();
        this.ClearContractTiming();

        this.trustBreaks++;
        this.lastTrustBreakReason = shortReason;
        this.rulerContractsCompleted = 0;
        this.leaderContractsCompleted = 0;
        this.nobleContractsCompleted = 0;
        this.royalAscentTriggered = false;
        this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
        this.stage = RoyaltyRegenesisStage.RulerPrisoners;
        this.nextEventTick = Find.TickManager.TicksGame
            + Rand.RangeInclusive(MajorResetMinDays, MajorResetMaxDays) * GenDate.TicksPerDay;

        if (offendedFaction != null && offendedFaction != Faction.OfPlayer)
        {
            this.ApplyTrustBreakGoodwillPenalty(offendedFaction);
        }

        this.EnsureChainQuest();
        if (this.chainQuest != null && !this.chainQuest.Historical)
        {
            this.chainQuest.description =
                RoyaltyRegenesisQuestFactory.BuildChainDescriptionBody()
                + "\n\nTRUST BROKEN: " + shortReason
                + "\nAll stage progress has been reset. Foreign and imperial patrons will only return after a long cooling-off period.";
        }

        Find.LetterStack.ReceiveLetter(
            "CryoRegenesis trust lost",
            letterText + "\n\nThe Quests tab shows the chain restarting from the beginning.",
            LetterDefOf.NegativeEvent);
    }

    private string GetStageLabel(RoyaltyRegenesisStage current)
    {
        switch (current)
        {
            case RoyaltyRegenesisStage.NotStarted:
                return "Stage: not started";
            case RoyaltyRegenesisStage.RulerPrisoners:
                return "Stage: foreign prisoner trials (before Empire notice)";
            case RoyaltyRegenesisStage.Leaders:
                return "Stage: planetary rulers (before Empire notice)";
            case RoyaltyRegenesisStage.LowerNobility:
                return "Stage: Empire lower nobility";
            case RoyaltyRegenesisStage.StellarchNotice:
                return "Stage: Stellarch en route";
            case RoyaltyRegenesisStage.StellarchArrival:
                return "Stage: Stellarch contract";
            case RoyaltyRegenesisStage.EmperorNotice:
                return "Stage: Emperor en route";
            case RoyaltyRegenesisStage.EmperorArrival:
                return "Stage: Emperor contract";
            case RoyaltyRegenesisStage.Completed:
                return "Stage: complete";
            default:
                return "Stage: " + current;
        }
    }

    private void ApplyTrustBreakGoodwillPenalty(Faction faction)
    {
        if (faction == null || faction == Faction.OfPlayer)
        {
            return;
        }

#if RIMWORLD12
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            DeathTrustGoodwillPenalty,
            true,
            true,
            "CryoRegenesis trust broken",
            null);
#else
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            DeathTrustGoodwillPenalty,
            true,
            true,
            HistoryEventDefOf.MemberKilled,
            null);
#endif
    }

    private void ApplyRecruitmentGoodwillPenalty(Faction faction, Pawn pawn)
    {
        if (faction == null || faction == Faction.OfPlayer)
        {
            return;
        }

#if RIMWORLD12
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            RecruitGoodwillPenalty,
            true,
            true,
            "Recruited CryoRegenesis contract client",
            pawn);
#else
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            RecruitGoodwillPenalty,
            true,
            true,
            HistoryEventDefOf.MemberCaptured,
            pawn);
#endif
    }

    private Pawn GetFactionLeader(Faction faction, int minimumBiologicalAge)
    {
        Pawn leader = faction?.leader;
        return this.IsAvailableWorldPawn(leader, faction, minimumBiologicalAge, includeFactionLeader: true)
            ? leader
            : null;
    }

    private List<Pawn> GetAvailableWorldPawns(Faction faction, int minimumBiologicalAge)
    {
        if (faction == null || Find.WorldPawns == null)
        {
            return new List<Pawn>();
        }

        return Find.WorldPawns.AllPawnsAlive
            .Where(pawn => this.IsAvailableWorldPawn(pawn, faction, minimumBiologicalAge))
            .ToList();
    }

    private bool IsAvailableWorldPawn(Pawn pawn, Faction faction, int minimumBiologicalAge, bool includeFactionLeader = false)
    {
        return pawn != null
            && !pawn.Dead
            && pawn.Faction == faction
            && (includeFactionLeader || pawn != faction.leader)
            && pawn.ageTracker != null
            && pawn.ageTracker.AgeBiologicalYears >= minimumBiologicalAge
            && !pawn.Spawned
            && !IsActiveRegenContractPawn(pawn)
            && Find.WorldPawns?.AllPawnsAlive.Contains(pawn) == true;
    }

    private PawnKindDef CommonPawnKind(Faction faction = null)
    {
        if (faction?.def?.basicMemberKind != null)
        {
            return faction.def.basicMemberKind;
        }

        return this.NamedPawnKind("Empire_Common_Lodger", this.AnyHumanPawnKind());
    }

    private PawnKindDef NoblePawnKind(Faction faction = null)
    {
        if (faction != null && !this.IsEmpire(faction) && faction.def.basicMemberKind != null)
        {
            // Prefer the faction's own member kinds for non-Empire leaders.
            return faction.def.basicMemberKind;
        }

        return this.NamedPawnKind("Empire_Royal_NobleWimp", this.CommonPawnKind(faction));
    }

    private PawnKindDef LowerNoblePawnKind()
    {
        return this.NamedPawnKind(
            Rand.Element("Empire_Royal_Yeoman", "Empire_Royal_Esquire", "Empire_Royal_Knight"),
            this.NoblePawnKind(this.EmpireFaction()));
    }

    private PawnKindDef NamedPawnKind(string defName, PawnKindDef fallback)
    {
        return DefDatabase<PawnKindDef>.GetNamedSilentFail(defName) ?? fallback;
    }

    private PawnKindDef AnyHumanPawnKind()
    {
        return DefDatabase<PawnKindDef>.AllDefsListForReading
            .FirstOrDefault(def => def.race != null && def.race.race != null && def.race.race.Humanlike);
    }

    private Faction EmpireFaction()
    {
        FactionDef empireDef = DefDatabase<FactionDef>.GetNamedSilentFail("Empire");
        if (empireDef != null)
        {
            Faction empire = Find.FactionManager.FirstFactionOfDef(empireDef);
            if (empire != null)
            {
                return empire;
            }
        }

        return null;
    }

    /// Other planetary factions only — never Ancients, never Empire.
    /// Empire contracts begin only after these succeed.
    private Faction RandomPlanetarySenderFaction()
    {
        return Find.FactionManager.AllFactionsListForReading
            .Where(this.IsEligiblePlanetarySender)
            .RandomElementWithFallback(null);
    }

    private bool IsEligiblePlanetarySender(Faction faction)
    {
        if (faction == null || faction == Faction.OfPlayer || faction.defeated)
        {
            return false;
        }

        if (!faction.def.humanlikeFaction)
        {
            return false;
        }

        if (faction.HostileTo(Faction.OfPlayer))
        {
            return false;
        }

        if (faction.Hidden)
        {
            return false;
        }

        if (this.IsEmpire(faction) || this.IsAncient(faction))
        {
            return false;
        }

        return true;
    }

    private bool IsEmpire(Faction faction)
    {
        if (faction?.def == null)
        {
            return false;
        }

        if (faction.def.defName == "Empire")
        {
            return true;
        }

        try
        {
            return faction.def == FactionDefOf.Empire;
        }
        catch
        {
            return false;
        }
    }

    private bool IsAncient(Faction faction)
    {
        if (faction?.def == null)
        {
            return false;
        }

        string defName = faction.def.defName;
        if (defName == "Ancients" || defName == "AncientsHostile")
        {
            return true;
        }

        try
        {
            return faction.def == FactionDefOf.Ancients || faction.def == FactionDefOf.AncientsHostile;
        }
        catch
        {
            return false;
        }
    }

    private void TrySetRoyalTitle(Pawn pawn, Faction faction, string titleDefName)
    {
        RoyalTitleDef title = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(titleDefName);
        if (title == null || pawn == null || faction == null)
        {
            return;
        }

        object royalty = pawn.GetType().GetField("royalty")?.GetValue(pawn)
            ?? pawn.GetType().GetProperty("royalty")?.GetValue(pawn, null);

        if (royalty == null)
        {
            return;
        }

        MethodInfo method = royalty.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == "SetTitle" &&
                candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(RoyalTitleDef)));

        if (method == null)
        {
            return;
        }

        object[] args = method.GetParameters()
            .Select<ParameterInfo, object>(parameter =>
            {
                if (parameter.ParameterType == typeof(Faction))
                {
                    return faction;
                }

                if (parameter.ParameterType == typeof(RoyalTitleDef))
                {
                    return title;
                }

                if (parameter.ParameterType == typeof(bool))
                {
                    return false;
                }

                return null;
            })
            .ToArray();

        try
        {
            method.Invoke(royalty, args);
        }
        catch (Exception ex)
        {
            Log.Warning($"[CryoRegenesis] Failed to set Royalty title {titleDefName} for {pawn.Name}: {ex.Message}");
        }
    }

    private void TryMakeRoyalAscentAvailable()
    {
        Find.LetterStack.ReceiveLetter(
            "Imperial CryoRegenesis complete",
            "The Emperor has been restored to age 20. Royal Ascent endgame protocols are now being activated.",
            LetterDefOf.PositiveEvent);

        QuestScriptDef questDef = DefDatabase<QuestScriptDef>.GetNamedSilentFail("EndGame_RoyalAscent");
        if (questDef == null)
        {
            Log.Warning("[CryoRegenesis] Could not activate Royal Ascent: EndGame_RoyalAscent was unavailable.");
            return;
        }

        QuestUtility.GenerateQuestAndMakeAvailable(questDef, new Slate());
    }
}

public class RoyaltyRegenesisClient : IExposable
{
    public Pawn pawn;
    public long desiredAgeTicks;
    public string role;
    public bool triggerRoyalAscent;
    public int returnByTick;
    public bool isPrisoner;
    public Faction sourceFaction;

    /// Latched the first time this client reaches the contracted age (the casket
    /// notifies the exact tick; the periodic check forgives natural aging afterwards).
    /// Survives subsequent natural aging so pickup success is not lost.
    public bool everReachedDesiredAge;

    public void ExposeData()
    {
        Scribe_References.Look(ref this.pawn, "pawn");
        Scribe_Values.Look(ref this.desiredAgeTicks, "desiredAgeTicks", 0L);
        Scribe_Values.Look(ref this.role, "role");
        Scribe_Values.Look(ref this.triggerRoyalAscent, "triggerRoyalAscent", false);
        Scribe_Values.Look(ref this.returnByTick, "returnByTick", 0);
        Scribe_Values.Look(ref this.isPrisoner, "isPrisoner", false);
        Scribe_References.Look(ref this.sourceFaction, "sourceFaction");
        Scribe_Values.Look(ref this.everReachedDesiredAge, "everReachedDesiredAge", false);
    }
}

public enum RoyaltyRegenesisStage
{
    NotStarted,
    RulerPrisoners,
    Leaders,
    LowerNobility,
    StellarchNotice,
    StellarchArrival,
    EmperorNotice,
    EmperorArrival,
    Completed,
}

public static class RoyaltyRegenesisDebugActions
{
    [DebugAction("CryoRegenesis", "Start Royalty chain")]
    public static void StartRoyaltyChain()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugStartAt(RoyaltyRegenesisStage.RulerPrisoners);
    }

    [DebugAction("CryoRegenesis", "Start Stellarch arrival")]
    public static void StartStellarchArrival()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugStartAt(RoyaltyRegenesisStage.StellarchArrival);
    }

    [DebugAction("CryoRegenesis", "Start Emperor arrival")]
    public static void StartEmperorArrival()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugStartAt(RoyaltyRegenesisStage.EmperorArrival);
    }

    [DebugAction("CryoRegenesis", "Trigger next Royalty event")]
    public static void TriggerNextRoyaltyEvent()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugTriggerNextEvent();
    }
}

/// No-op root so Royalty Regenesis QuestScriptDefs are valid.
/// Chain and contract quests are created in code via RoyaltyRegenesisQuestFactory, not QuestGen.
public class QuestNode_StartRoyaltyRegenesisChain : QuestNode
{
    protected override bool TestRunInt(Slate slate)
    {
        return true;
    }

    protected override void RunInt()
    {
    }
}
