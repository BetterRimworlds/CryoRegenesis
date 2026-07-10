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
    private const int ContractDaysPerClient = 2;
    private const int MinContractDays = 2;
    private const int MajorResetMinDays = 60;
    private const int MajorResetMaxDays = 90;
    /// Vanilla hospitality pickup grace period (Script_Hospitality_Worker shuttleLeaveDelayTicks = 3*60000).
    private const int ShuttleLeaveDelayDays = 3;

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

    /// True once every active client has reached the requested age during pickup.
    private bool pickupAllClientsReady;

    /// True once the contract quest has been logged as completed (every client reached
    /// their target age). Success is banked here — shuttle logistics can no longer fail it.
    private bool contractCompletionLogged;

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
        Scribe_Values.Look(ref this.pickupAllClientsReady, "crRoyalPickupAllClientsReady", false);
        Scribe_Values.Look(ref this.contractCompletionLogged, "crRoyalContractCompletionLogged", false);
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

            if (this.lastTrustBreakReason == null)
            {
                this.lastTrustBreakReason = string.Empty;
            }

            if (this.activeClients.Any())
            {
                this.EnsureContractDeadline();
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

        if (!this.CanRunCampaign())
        {
            return;
        }

        this.CheckActiveClients();

        if (this.activeClients.Any())
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

        int contractDays = this.CalculateContractDays(pawns.Count);
        this.SetActiveContractDeadlineDays(contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.RulerPrisoners;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"{sender.Name} has sent prisoners for a trial CryoRegenesis contract. " +
            $"Their requested regression is six months, one year, or two years. " +
            $"Their shuttle will stay parked on site and departs with them on {returnText}. " +
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
        int contractDays = this.CalculateContractDays(1);

        // Leaders come as guests (not prisoners) with a hard return time.
        this.PrepareClient(leader, targetTicks, "planetary ruler", sender, isPrisoner: false, contractDays: contractDays);
        this.SetActiveContractDeadlineDays(contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.Leaders;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"{leader.Name.ToStringShort} of {sender.Name}, a ruler over the age of 30, has arrived by shuttle " +
            $"for a privately negotiated CryoRegenesis stay. Their shuttle waits on site and departs on {returnText}. " +
            "Do not recruit them — death or recruitment will destroy trust and reset the chain.";
        this.DeliverClientsByShuttle(
            map,
            new List<Pawn> { leader },
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

        int targetAge = Rand.RangeInclusive(21, 40);
        Pawn pawn = this.GetAvailableWorldPawns(empire, targetAge + 1).RandomElementWithFallback(null);
        if (pawn == null)
        {
            this.ScheduleRetry("No eligible imperial noble", "No eligible Imperial world pawn is available for a CryoRegenesis stay. Retrying later.");
            return;
        }

        long targetTicks = targetAge * (long)GenDate.TicksPerYear;
        int contractDays = this.CalculateContractDays(1);

        this.PrepareClient(pawn, targetTicks, "lower imperial noble", empire, isPrisoner: false, contractDays: contractDays);
        this.SetActiveContractDeadlineDays(contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.LowerNobility;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Empire has sent {pawn.Name.ToStringShort}, a lower noble, by shuttle to verify your CryoRegenesis process. " +
            $"Their shuttle waits on site and departs on {returnText}. Do not recruit them — death or recruitment will destroy trust and reset the chain.";
        this.DeliverClientsByShuttle(
            map,
            new List<Pawn> { pawn },
            empire,
            "Imperial CryoRegenesis contract",
            letterBody);
        this.BeginContractQuest(
            "CryoRegenesis: Imperial noble",
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
        int contractDays = this.CalculateContractDays(party.Count);
        this.PrepareClient(stellarch, targetTicks, "stellarch", empire, isPrisoner: false, contractDays: contractDays);
        party.AddRange(this.GetWorldPawnPartners(stellarch, empire, 30, "stellarch companion", isPrisoner: false, contractDays: contractDays));
        contractDays = this.CalculateContractDays(party.Count);
        this.SetActiveContractDeadlineDays(contractDays);

        this.activeContractStage = RoyaltyRegenesisStage.StellarchArrival;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Stellarch has arrived by shuttle for CryoRegenesis and has chosen to regress to age {targetAge}. " +
            $"Any spouses or lovers in the party have chosen age 30. The imperial shuttle waits on site and departs on {returnText}.";
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
        int contractDays = this.CalculateContractDays(party.Count);
        this.PrepareClient(emperor, targetTicks, "emperor", empire, isPrisoner: false, contractDays: contractDays, triggerRoyalAscent: true);
        party.AddRange(this.GetWorldPawnPartners(emperor, empire, 21, "imperial companion", isPrisoner: false, contractDays: contractDays));
        contractDays = this.CalculateContractDays(party.Count);
        this.SetActiveContractDeadlineDays(contractDays);

        this.activeContractStage = RoyaltyRegenesisStage.EmperorArrival;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Emperor has arrived by shuttle for CryoRegenesis and will regress to age 20. " +
            $"Any spouses or lovers in the party have chosen age 21. The imperial shuttle waits on site and departs on {returnText}.";
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

    private List<Pawn> GetWorldPawnPartners(Pawn noble, Faction faction, int targetAge, string role, bool isPrisoner, int contractDays)
    {
        List<Pawn> partners = noble.relations?.DirectRelations
            ?.Where(relation =>
                relation.def == PawnRelationDefOf.Spouse ||
                relation.def == PawnRelationDefOf.Lover ||
                relation.def == PawnRelationDefOf.Fiance)
            .Select(relation => relation.otherPawn)
            .Where(pawn => this.IsAvailableWorldPawn(pawn, faction, targetAge + 1))
            .Distinct()
            .ToList() ?? new List<Pawn>();

        foreach (Pawn partner in partners)
        {
            this.PrepareClient(partner, targetAge * (long)GenDate.TicksPerYear, role, faction, isPrisoner, contractDays);
        }

        return partners;
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
        this.pickupAllClientsReady = false;
        this.contractCompletionLogged = false;
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

    private int CalculateContractDays(int clientCount)
    {
        return Math.Max(MinContractDays, Math.Max(1, clientCount) * ContractDaysPerClient);
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

    /// Contract shuttle: GenerateShuttle → Arrive → Unload → park for the entire contract.
    /// The same shuttle carries the clients home — <see cref="BoardAndSendShuttle"/> flips it
    /// to leave-when-loaded when treatment completes or the deadline hits.
    /// Faction is left unset so CompShuttle shows Autoload.
    private void DeliverClientsByShuttle(Map map, List<Pawn> pawns, Faction faction, string label, string text)
    {
        if (map == null || pawns == null || !pawns.Any())
        {
            return;
        }

#if RIMWORLD12
        // No WaitForever ship job in 1.2: park far longer than any contract can last.
        this.DeliverByLegacyShuttle(map, pawns, faction, 10 * GenDate.TicksPerYear);
#else
        this.DeliverByTransportShip(map, pawns, faction);
#endif

        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent, new LookTargets(pawns));
    }

#if RIMWORLD12
    /// RimWorld 1.2: <see cref="QuestGen_Shuttle.GenerateShuttle"/> + ShuttleIncoming.
    /// Parks after drop-off for the whole contract with Autoload/Send for the return trip.
    private void DeliverByLegacyShuttle(Map map, List<Pawn> pawns, Faction faction, int stayTicks)
    {
        // No owningFaction: player-facing guest controls (Autoload) require null/player faction.
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: null,
            requiredPawns: pawns,
            leaveImmediatelyWhenSatisfied: false,
            dropEverythingOnArrival: true,
            stayAfterDroppedEverythingOnArrival: true,
            hideControls: false);

        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        if (compShuttle != null)
        {
            compShuttle.leaveAfterTicks = stayTicks;
        }

        CompTransporter transporter = shuttle.TryGetComp<CompTransporter>();
        transporter?.innerContainer.TryAddRangeOrTransfer(pawns.Cast<Thing>(), true, false);

        this.contractShuttle = shuttle;

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
    /// RimWorld 1.3+: contract shuttle (Arrive → Unload → WaitForever with gizmos → FlyAway).
    /// It stays parked until <see cref="BoardAndSendShuttle"/> tells it to leave with the clients.
    private void DeliverByTransportShip(Map map, List<Pawn> pawns, Faction faction)
    {
        // Match Util_TransportShip_Pickup: no owningFaction so Autoload gizmos appear.
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: null,
            requiredPawns: pawns,
            hideControls: false);

        // Never use MakeTransportShip(contents) destroyLeftover path — transfer safely.
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

        ShipJob_Wait wait = (ShipJob_Wait)ShipJobMaker.MakeShipJob(ShipJobDefOf.WaitForever);
        wait.leaveImmediatelyWhenSatisfied = false; // stays parked for the whole contract
        wait.showGizmos = true;
        ship.AddJob(wait);

        ship.AddJob(ShipJobDefOf.FlyAway);
        ship.Start();

        this.contractTransportShip = ship;
        this.contractShuttle = shuttle;
        this.pickupShuttleSpawned = false;
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
                + " allReady=" + this.pickupAllClientsReady);
        }

        if (!this.activeClients.Any())
        {
            if (this.pickupShuttleSpawned)
            {
                this.LogRoyaltyDebug(
                    "All clients gone after pickup spawn → FinishContractAfterDeparture(success="
                    + this.pickupAllClientsReady + ")");
                this.FinishContractAfterDeparture(this.pickupAllClientsReady);
                return;
            }

            if (this.contractCompletionLogged)
            {
                this.LogRoyaltyDebug("Clients gone before pickup, but contract already completed — cleanup only.");
                this.FinishContractAfterDeparture(true);
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

            bool allInTransport = this.activeClients.All(client => this.IsClientInReturnTransport(client.pawn));
            if (allInTransport)
            {
                this.LogRoyaltyDebug(
                    "All clients in return transport → FinishContractAfterDeparture(success="
                    + this.pickupAllClientsReady + ")");
                this.LogClientAgeSnapshot("pre-finish (in transport)");
                this.FinishContractAfterDeparture(this.pickupAllClientsReady);
                return;
            }

            if (this.PickupLoadingWindowExpired())
            {
                this.LogRoyaltyDebug(
                    "Pickup loading window expired. allReady=" + this.pickupAllClientsReady
                    + " onMap=" + this.activeClients.Count(c => this.IsClientAvailableOnMap(c.pawn))
                    + " inTransport=" + this.activeClients.Count(c => this.IsClientInReturnTransport(c.pawn)));
                this.LogClientAgeSnapshot("pickup timeout");
                this.FailContractAfterPickupTimeout();
                return;
            }

            if (this.activeClients.Any(client => !this.IsClientAvailableOnMap(client.pawn)))
            {
                return;
            }
        }

        // Wait until the delivery shuttle has finished unloading before success/deadline checks.
        // Otherwise a brand-new contract can "complete" or soft-fail while clients are still landing.
        if (!this.pickupShuttleSpawned && this.activeClients.Any(client => !this.IsClientAvailableOnMap(client.pawn)))
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

        if (this.activeClients.Any(client => client.pawn.Dead))
        {
            Pawn dead = this.activeClients.First(client => client.pawn.Dead).pawn;
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

        this.EnsureContractDeadline();
        this.RefreshClientAgeProgress();

        bool allDone = this.pickupAllClientsReady;
        if (this.pickupShuttleSpawned)
        {
            return;
        }

        bool deadlineReached = this.IsContractDeadlineReached();

        if (!allDone && !deadlineReached)
        {
            return;
        }

        this.LogRoyaltyDebug(
            "Sending clients to board the contract shuttle. reason="
            + (allDone ? "all clients ready" : "deadline reached")
            + " allDone=" + allDone
            + " deadlineReached=" + deadlineReached
            + " deadlineTick=" + this.contractDeadlineTick
            + " now=" + Find.TickManager.TicksGame
            + " contractStart=" + this.contractStartTick);
        this.LogClientAgeSnapshot(allDone ? "early return (ready)" : "deadline return");

        List<Pawn> departing = this.activeClients.Select(client => client.pawn).Where(p => p != null).ToList();
        Faction sourceFaction = this.activeClients.FirstOrDefault()?.sourceFaction;
        Map targetMap = departing.FirstOrDefault(p => p.MapHeld != null)?.MapHeld ?? this.GetTargetMap();

        if (departing.Any())
        {
            if (targetMap == null)
            {
                this.LogRoyaltyDebug("DepartClients aborted: targetMap is null.");
                return;
            }

            if (!this.DepartClients(targetMap, departing, sourceFaction))
            {
                this.LogRoyaltyDebug("DepartClients returned false (will retry next check).");
                return;
            }

            this.LogRoyaltyDebug(
                "DepartClients succeeded. pickupSpawned=" + this.pickupShuttleSpawned
                + " allReady=" + this.pickupAllClientsReady
                + " shuttle=" + (this.contractShuttle?.LabelCap ?? "null"));
        }
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
                extraFactionPart = this.activeContractQuest.ExtraFaction(
                    homeFaction,
                    pawns,
                    ExtraFactionType.HomeFaction);
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

    private void RestoreGuestClientFactions()
    {
        foreach (RoyaltyRegenesisClient client in this.activeClients.Where(client => client != null && !client.isPrisoner))
        {
            Pawn pawn = client.pawn;
            if (pawn != null && !pawn.Destroyed && client.sourceFaction != null && pawn.Faction == Faction.OfPlayer)
            {
                this.ReleaseFromCurrentLord(pawn);
                pawn.SetFaction(client.sourceFaction);
                this.ApplyGuestOrPrisonerStatus(pawn, isPrisoner: false);
            }
        }
    }

    private void AssignExitOnShuttleLord(Map map, Thing shuttle, List<Pawn> pawns, Faction faction)
    {
        this.RestoreGuestClientFactions();
        this.ReleaseFromCurrentLords(pawns);
        LordMaker.MakeNewLord(
            faction ?? Faction.OfPlayer,
            new LordJob_ExitOnShuttle(shuttle),
            map,
            pawns);
    }

    private void ReleaseFromCurrentLords(IEnumerable<Pawn> pawns)
    {
        foreach (Pawn pawn in pawns)
        {
            this.ReleaseFromCurrentLord(pawn);
        }
    }

    private void ReleaseFromCurrentLord(Pawn pawn)
    {
        pawn?.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ForcedToJoinOtherLord);
    }

    private void FinishContractAfterDeparture(bool success)
    {
        // Final re-evaluation from latched per-client flags (pawns may already be gone).
        this.RefreshClientAgeProgress();
        bool latchedSuccess = this.pickupAllClientsReady
            || (this.activeClients.Any() && this.activeClients.All(c => c.everReachedDesiredAge));
        if (latchedSuccess != success)
        {
            this.LogRoyaltyDebug(
                "Finish success override: arg=" + success + " → latched=" + latchedSuccess);
            success = latchedSuccess;
        }

        this.LogRoyaltyDebug(
            "FinishContractAfterDeparture success=" + success
            + " stage=" + this.activeContractStage
            + " clients=" + this.activeClients.Count
            + " triggerRoyalAscent=" + this.activeClients.Any(c => c.triggerRoyalAscent));
        this.LogClientAgeSnapshot("finish");

        bool triggerEndgame = this.activeClients.Any(client => client.triggerRoyalAscent);
        bool alreadyLogged = this.contractCompletionLogged;
        this.ClearContractFlags(this.activeClients);
        this.activeClients.Clear();
        this.ClearContractShuttle();
        this.ClearContractTiming();

        if (alreadyLogged)
        {
            this.LogRoyaltyDebug("Contract was already logged as completed at target-age; departure is cleanup only.");
            return;
        }

        if (triggerEndgame && success)
        {
            this.LogRoyaltyDebug("Outcome: SUCCESS + Royal Ascent endgame.");
            this.GrantCompletionRewardIfEligible(this.activeContractStage);
            this.EndActiveContractQuest(QuestEndOutcome.Success);
            this.stage = RoyaltyRegenesisStage.Completed;
            this.royalAscentTriggered = true;
            this.CompleteChainQuest();
            this.TryMakeRoyalAscentAvailable();
            return;
        }

        if (success)
        {
            this.LogRoyaltyDebug("Outcome: SUCCESS — CompleteActiveContract.");
            this.GrantCompletionRewardIfEligible(this.activeContractStage);
            this.EndActiveContractQuest(QuestEndOutcome.Success);
            this.CompleteActiveContract();
        }
        else
        {
            // Soft failure: contract fails, stage progress kept, no full chain reset.
            this.LogRoyaltyDebug("Outcome: FAIL — contract expired (regression not finished / ready never latched).");
            this.EndActiveContractQuest(QuestEndOutcome.Fail);
            this.ScheduleRetry(
                "CryoRegenesis contract expired",
                "The contract return deadline was reached before the requested regression was finished. But one or more of the clients have left the map. Another attempt may come later.");
        }
    }

    private void FailContractAfterPickupTimeout()
    {
        if (this.contractCompletionLogged)
        {
            this.LogRoyaltyDebug("Pickup window expired after completion was logged — contract stays completed.");
            this.ClearContractFlags(this.activeClients);
            this.activeClients.Clear();
            this.ClearContractShuttle();
            this.ClearContractTiming();
            Find.LetterStack.ReceiveLetter(
                "Regenesis shuttle left without clients",
                "The shuttle left before every CryoRegenesis client was loaded, "
                + "but they had already reached their target ages — the contract remains completed.",
                LetterDefOf.NeutralEvent);
            return;
        }

        this.LogRoyaltyDebug("FailContractAfterPickupTimeout — clients not loaded in time.");
        this.LogClientAgeSnapshot("pickup timeout fail");
        this.ClearContractFlags(this.activeClients);
        this.activeClients.Clear();
        this.ClearContractShuttle();
        this.ClearContractTiming();
        this.EndActiveContractQuest(QuestEndOutcome.Fail);
        this.ScheduleRetry(
            "CryoRegenesis return missed",
            "The shuttle left before every CryoRegenesis client was loaded. Another attempt may come later.");
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

        return Find.TickManager.TicksGame >= this.pickupShuttleSpawnTick + ShuttleLeaveDelayDays * GenDate.TicksPerDay;
    }

    /// Send clients home aboard the contract shuttle that has been parked on site the whole
    /// stay. A replacement shuttle is spawned only if the parked one was lost (destroyed, etc.).
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

        // Prisoners are kept anesthetized during treatment; wake them so they can walk
        // themselves aboard like any other departing guest.
        foreach (Pawn pawn in living)
        {
            Hediff anesthetic = pawn.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.Anesthetic);
            if (anesthetic != null)
            {
                pawn.health.RemoveHediff(anesthetic);
                this.LogRoyaltyDebug("Removed anesthetic from " + pawn.Name.ToStringShort);
            }
        }

        // Normal path: the contract shuttle has been parked on site since delivery.
        Thing shuttle = this.GetUsableContractShuttle(map);
        if (shuttle != null && !this.pickupShuttleSpawned)
        {
            this.LogRoyaltyDebug("Boarding the parked contract shuttle for departure: " + shuttle.LabelCap);
            this.BoardAndSendShuttle(map, shuttle, living, faction);
            this.LogClientAgeSnapshot("after board-and-send (parked shuttle)");
            return true;
        }

        // Fallback: the parked shuttle was lost — spawn a replacement to carry them home.
        this.LogRoyaltyDebug("Spawning replacement departure shuttle for " + living.Count + " client(s).");
        bool spawned = this.SpawnPickupShuttle(map, living, faction);
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
        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        if (compShuttle != null)
        {
            compShuttle.requiredPawns.Clear();
            compShuttle.requiredPawns.AddRange(living);
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

        this.AssignExitOnShuttleLord(map, shuttle, living, faction);
        this.pickupShuttleSpawned = true;
        this.pickupShuttleSpawnTick = Find.TickManager.TicksGame;
        this.RefreshClientAgeProgress();
        this.LogRoyaltyDebug(
            "BoardAndSendShuttle: pickupSpawned=true tick=" + this.pickupShuttleSpawnTick
            + " requiredPawns=" + living.Count
            + " allReady=" + this.pickupAllClientsReady);
    }

    private bool SpawnPickupShuttle(Map map, List<Pawn> living, Faction faction)
    {
#if RIMWORLD12
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: null,
            requiredPawns: living,
            leaveImmediatelyWhenSatisfied: true,
            hideControls: false);

        CompShuttle compShuttle = shuttle?.TryGetComp<CompShuttle>();
        if (shuttle == null || compShuttle == null)
        {
            Log.Error("[CryoRegenesis] Pickup shuttle generation failed; destroying clients off-map.");
            this.DestroyClientsOffMap(living);
            return true;
        }

        compShuttle.leaveAfterTicks = ShuttleLeaveDelayDays * GenDate.TicksPerDay;
        this.contractShuttle = shuttle;
        this.pickupShuttleSpawned = true;
        this.pickupShuttleSpawnTick = Find.TickManager.TicksGame;

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
            this.DestroyClientsOffMap(living);
            if (!shuttle.Destroyed)
            {
                shuttle.Destroy(DestroyMode.Vanish);
            }

            return true;
        }

        this.AssignExitOnShuttleLord(map, shuttle, living, faction);

        this.RefreshClientAgeProgress();
        this.LogRoyaltyDebug(
            "Pickup shuttle (1.2) placed. leaveAfterTicks=" + compShuttle.leaveAfterTicks
            + " allReady=" + this.pickupAllClientsReady);

        Find.LetterStack.ReceiveLetter(
            "Regenesis Replacement Shuttle",
            "A replacement shuttle has arrived to collect the CryoRegenesis clients. Load them before it leaves.",
            LetterDefOf.NeutralEvent,
            new LookTargets(shuttle));
#else
        Thing shuttle = QuestGen_Shuttle.GenerateShuttle(
            owningFaction: null,
            requiredPawns: living,
            hideControls: false);

        if (shuttle == null)
        {
            Log.Error("[CryoRegenesis] Pickup shuttle generation failed; destroying clients off-map.");
            this.DestroyClientsOffMap(living);
            return true;
        }

        TransportShip ship = TransportShipMaker.MakeTransportShip(
            TransportShipDefOf.Ship_Shuttle,
            null,
            shuttle);

        ShipJob_Arrive arrive = (ShipJob_Arrive)ShipJobMaker.MakeShipJob(ShipJobDefOf.Arrive);
        arrive.mapParent = map.Parent;
        arrive.factionForArrival = faction ?? Faction.OfPlayer;
        if (living[0].MapHeld == map)
        {
            arrive.mapOfPawn = living[0];
        }

        ship.AddJob(arrive);

        ShipJob_WaitTime wait = (ShipJob_WaitTime)ShipJobMaker.MakeShipJob(ShipJobDefOf.WaitTime);
        wait.duration = ShuttleLeaveDelayDays * GenDate.TicksPerDay;
        wait.leaveImmediatelyWhenSatisfied = true;
        wait.showGizmos = true;
        wait.sendAwayIfAllDespawned = living.Cast<Thing>().ToList();
        ship.AddJob(wait);

        ship.AddJob(ShipJobDefOf.FlyAway);
        ship.Start();

        this.contractTransportShip = ship;
        this.contractShuttle = shuttle;
        this.pickupShuttleSpawned = true;
        this.pickupShuttleSpawnTick = Find.TickManager.TicksGame;

        this.AssignExitOnShuttleLord(map, shuttle, living, faction);

        this.RefreshClientAgeProgress();
        this.LogRoyaltyDebug(
            "Pickup shuttle (TransportShip) started. waitDays=" + ShuttleLeaveDelayDays
            + " leaveImmediatelyWhenSatisfied=true"
            + " allReady=" + this.pickupAllClientsReady
            + " ship=" + (ship != null)
            + " shuttleThing=" + (shuttle?.LabelCap ?? "null"));

        Find.LetterStack.ReceiveLetter(
            "Regenesis Replacement Shuttle",
            "A replacement shuttle has arrived to collect the CryoRegenesis clients. Load them before it leaves.",
            LetterDefOf.NeutralEvent,
            new LookTargets(shuttle));
#endif

        return true;
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
    /// Once a client has ever hit the padded target, that fact is sticky for the contract.
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

            if (this.EvaluateAgeAgainstTarget(client, out _, out _, out _))
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
        if (allEver && !this.pickupAllClientsReady)
        {
            this.pickupAllClientsReady = true;
            this.LogRoyaltyDebug("All clients have latched everReachedDesiredAge → pickupAllClientsReady=true");
            this.MarkContractQuestCompleted();
        }
        else if (anyNew)
        {
            this.LogRoyaltyDebug(
                "Age progress: ready "
                + this.activeClients.Count(c => c.everReachedDesiredAge)
                + "/" + this.activeClients.Count
                + " pickupAllClientsReady=" + this.pickupAllClientsReady);
        }
    }

    /// Logs the contract quest as completed the moment every client has reached their
    /// target age. Success is banked here, no matter how long the departure takes —
    /// departure and pickup afterwards are cleanup only.
    private void MarkContractQuestCompleted()
    {
        if (this.contractCompletionLogged)
        {
            return;
        }

        this.contractCompletionLogged = true;
        this.LogRoyaltyDebug(
            "All clients reached target age → contract quest completed now (stage=" + this.activeContractStage + ").");
        this.EndActiveContractQuest(QuestEndOutcome.Success);
        this.GrantCompletionRewardIfEligible(this.activeContractStage);

        if (this.activeClients.Any(client => client != null && client.triggerRoyalAscent))
        {
            this.stage = RoyaltyRegenesisStage.Completed;
            this.royalAscentTriggered = true;
            this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
            this.CompleteChainQuest();
            this.TryMakeRoyalAscentAvailable();
            return;
        }

        this.CompleteActiveContract();
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

        if (this.EvaluateAgeAgainstTarget(client, out _, out _, out _))
        {
            client.everReachedDesiredAge = true;
            return true;
        }

        return false;
    }

    /// Current bio age is at/below contracted target, padded by elapsed contract time so
    /// natural aging while waiting to depart does not fail a finished regen.
    private bool EvaluateAgeAgainstTarget(
        RoyaltyRegenesisClient client,
        out long currentTicks,
        out long allowedTicks,
        out long agePadTicks)
    {
        currentTicks = -1;
        allowedTicks = -1;
        agePadTicks = CheckIntervalTicks;

        if (client?.pawn?.ageTracker == null)
        {
            return false;
        }

        if (this.contractStartTick >= 0)
        {
            agePadTicks += Math.Max(0, Find.TickManager.TicksGame - this.contractStartTick);
        }

        // Also allow the full contracted stay window (deadline - start) so a client who
        // finished on day 1 and ages until a late deadline still counts.
        if (this.contractStartTick >= 0 && this.contractDeadlineTick > this.contractStartTick)
        {
            agePadTicks = Math.Max(agePadTicks, (long)(this.contractDeadlineTick - this.contractStartTick) + CheckIntervalTicks);
        }

        currentTicks = client.pawn.ageTracker.AgeBiologicalTicks;
        allowedTicks = client.desiredAgeTicks + agePadTicks;
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

            bool meets = this.EvaluateAgeAgainstTarget(client, out long cur, out long allowed, out long pad);
            string holder = pawn.ParentHolder != null ? pawn.ParentHolder.GetType().Name : "none";
            this.LogRoyaltyDebug(
                "  • " + pawn.Name.ToStringShort
                + " role=" + (client.role ?? "?")
                + " bioYears=" + pawn.ageTracker.AgeBiologicalYearsFloat.ToString("0.000")
                + " targetYears=" + ((double)client.desiredAgeTicks / GenDate.TicksPerYear).ToString("0.000")
                + " bioTicks=" + cur
                + " desiredTicks=" + client.desiredAgeTicks
                + " padTicks=" + pad
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
            "The CryoRegenesis clients have reached the requested regression age — the contract is fulfilled. "
            + "They will board their shuttle and depart. Check the Quests tab for chain progress.",
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
        sb.AppendLine(this.pickupShuttleSpawned
            ? "Shuttle: boarding — load the clients; it leaves once all are aboard."
            : "Shuttle departs: " + RoyaltyRegenesisQuestFactory.FormatGameTickDate(deadline)
                + " (" + ticksLeft.ToStringTicksToPeriod() + " remaining)");
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
        sb.AppendLine(this.pickupAllClientsReady || this.activeClients.All(this.ClientReachedDesiredAge)
            ? "All clients ready for return."
            : "Keep treating until each client reaches their target age before the shuttle departs.");
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
        object questManager = Find.QuestManager;

        if (questDef == null || questManager == null)
        {
            Log.Warning("[CryoRegenesis] Could not activate Royal Ascent: EndGame_RoyalAscent or QuestManager was unavailable.");
            return;
        }

        foreach (MethodInfo method in questManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name != "GenerateAndAddQuest" && method.Name != "GenerateQuestAndMakeAvailable")
            {
                continue;
            }

            object[] args = this.BuildQuestManagerArgs(method, questDef);
            if (args == null)
            {
                continue;
            }

            try
            {
                method.Invoke(questManager, args);
                return;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CryoRegenesis] Royal Ascent activation via {method.Name} failed: {ex.Message}");
            }
        }

        Log.Warning("[CryoRegenesis] Could not find a compatible Royal Ascent quest generation method.");
    }

    private object[] BuildQuestManagerArgs(MethodInfo method, QuestScriptDef questDef)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object[] args = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;
            if (parameterType == typeof(QuestScriptDef))
            {
                args[i] = questDef;
            }
            else if (!parameterType.IsValueType)
            {
                args[i] = null;
            }
            else if (parameterType == typeof(int))
            {
                args[i] = 0;
            }
            else if (parameterType == typeof(float))
            {
                args[i] = 0f;
            }
            else if (parameterType == typeof(bool))
            {
                args[i] = false;
            }
            else
            {
                return null;
            }
        }

        return args.Any(arg => arg == questDef) ? args : null;
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

    /// Latched the first time this client meets the contracted age (with wait pad).
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
