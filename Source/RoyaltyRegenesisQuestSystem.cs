/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

#if RIMWORLD15 || RIMWORLD16
using LudeonTK;
#endif

namespace BetterRimworlds.CryoRegenesis;

public class RoyaltyRegenesisQuestSystem : GameComponent
{
    private const int CheckIntervalTicks = 2500;
    private const int HalfYearTicks = GenDate.TicksPerYear / 2;
    private const int RecruitGoodwillPenalty = -35;
    private const int DeathTrustGoodwillPenalty = -75;
    private const int MinContractDays = 3;
    private const int MaxContractDays = 30;
    private const int MajorResetMinDays = 60;
    private const int MajorResetMaxDays = 90;

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

        int pawnCount = Rand.RangeInclusive(1, 2);
        List<Pawn> pawns = new List<Pawn>();
        int contractDays = 0;

        for (int i = 0; i < pawnCount; i++)
        {
            Pawn pawn = this.GeneratePawn(this.CommonPawnKind(sender), sender, Rand.RangeInclusive(24, 70));
            int duration = Rand.Element(HalfYearTicks, GenDate.TicksPerYear, GenDate.TicksPerYear * 2);
            long targetTicks = Math.Max(18L * GenDate.TicksPerYear, pawn.ageTracker.AgeBiologicalTicks - duration);
            int days = this.CalculateContractDays(pawn.ageTracker.AgeBiologicalTicks - targetTicks);
            contractDays = Math.Max(contractDays, days);
            this.PrepareClient(pawn, targetTicks, "foreign prisoner", sender, isPrisoner: true, contractDays: days);
            pawns.Add(pawn);
        }

        this.activeContractStage = RoyaltyRegenesisStage.RulerPrisoners;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"{sender.Name} has sent prisoners for a trial CryoRegenesis contract. " +
            $"Their requested regression is six months, one year, or two years. " +
            $"They arrive and depart by shuttle and must be returned by {returnText}. " +
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

        Pawn leader = this.GeneratePawn(this.NoblePawnKind(sender), sender, Rand.RangeInclusive(35, 82));
        int yearsToRemove = Rand.RangeInclusive(2, 18);
        long targetTicks = Math.Max(30L * GenDate.TicksPerYear, leader.ageTracker.AgeBiologicalTicks - yearsToRemove * GenDate.TicksPerYear);
        int contractDays = this.CalculateContractDays(leader.ageTracker.AgeBiologicalTicks - targetTicks);

        // Leaders come as guests (not prisoners) with a hard return time.
        this.PrepareClient(leader, targetTicks, "planetary ruler", sender, isPrisoner: false, contractDays: contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.Leaders;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"{leader.Name.ToStringShort} of {sender.Name}, a ruler over the age of 30, has arrived by shuttle " +
            $"for a privately negotiated CryoRegenesis stay. The shuttle returns on {returnText}. " +
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

        Pawn pawn = this.GeneratePawn(this.LowerNoblePawnKind(), empire, Rand.RangeInclusive(31, 72));
        int targetAge = Rand.RangeInclusive(21, 40);
        long targetTicks = targetAge * (long)GenDate.TicksPerYear;
        int contractDays = this.CalculateContractDays(pawn.ageTracker.AgeBiologicalTicks - targetTicks);

        this.PrepareClient(pawn, targetTicks, "lower imperial noble", empire, isPrisoner: false, contractDays: contractDays);
        this.activeContractStage = RoyaltyRegenesisStage.LowerNobility;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Empire has sent {pawn.Name.ToStringShort}, a lower noble, by shuttle to verify your CryoRegenesis process. " +
            $"They must return by {returnText}. Do not recruit them — death or recruitment will destroy trust and reset the chain.";
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

        Pawn stellarch = this.GeneratePawn(this.NamedPawnKind("Empire_Royal_Stellarch", this.NoblePawnKind(empire)), empire, Rand.RangeInclusive(45, 85));
        this.TrySetRoyalTitle(stellarch, empire, "Stellarch");

        int targetAge = Rand.RangeInclusive(21, 35);
        long targetTicks = targetAge * (long)GenDate.TicksPerYear;
        List<Pawn> party = new List<Pawn> { stellarch };
        int contractDays = this.CalculateContractDays(stellarch.ageTracker.AgeBiologicalTicks - targetTicks);
        this.PrepareClient(stellarch, targetTicks, "stellarch", empire, isPrisoner: false, contractDays: contractDays);
        party.AddRange(this.GetOrGeneratePartners(stellarch, empire, 30, "stellarch companion", isPrisoner: false, contractDays: contractDays));

        this.activeContractStage = RoyaltyRegenesisStage.StellarchArrival;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Stellarch has arrived by shuttle for CryoRegenesis and has chosen to regress to age {targetAge}. " +
            $"Any spouses or lovers in the party have chosen age 30. The imperial shuttle returns on {returnText}.";
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

        Pawn emperor = this.GeneratePawn(this.NamedPawnKind("Empire_Royal_Stellarch", this.NoblePawnKind(empire)), empire, Rand.RangeInclusive(60, 100));
        this.TrySetRoyalTitle(emperor, empire, "Emperor");

        List<Pawn> party = new List<Pawn> { emperor };
        long targetTicks = 20L * GenDate.TicksPerYear;
        int contractDays = this.CalculateContractDays(emperor.ageTracker.AgeBiologicalTicks - targetTicks);
        this.PrepareClient(emperor, targetTicks, "emperor", empire, isPrisoner: false, contractDays: contractDays, triggerRoyalAscent: true);
        party.AddRange(this.GetOrGeneratePartners(emperor, empire, 21, "imperial companion", isPrisoner: false, contractDays: contractDays));

        this.activeContractStage = RoyaltyRegenesisStage.EmperorArrival;
        string returnText = this.FormatReturnDeadline(contractDays);
        string letterBody =
            $"The Emperor has arrived by shuttle for CryoRegenesis and will regress to age 20. " +
            $"Any spouses or lovers in the party have chosen age 21. The imperial shuttle returns on {returnText}.";
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

    private List<Pawn> GetOrGeneratePartners(Pawn noble, Faction faction, int targetAge, string role, bool isPrisoner, int contractDays)
    {
        List<Pawn> partners = noble.relations?.DirectRelations
            ?.Where(relation =>
                relation.def == PawnRelationDefOf.Spouse ||
                relation.def == PawnRelationDefOf.Lover ||
                relation.def == PawnRelationDefOf.Fiance)
            .Select(relation => relation.otherPawn)
            .Where(pawn => pawn != null && !pawn.Dead)
            .Distinct()
            .ToList() ?? new List<Pawn>();

        if (!partners.Any() && Rand.Chance(0.65f))
        {
            Pawn spouse = this.GeneratePawn(this.NoblePawnKind(faction), faction, Rand.RangeInclusive(35, 80));
            noble.relations.AddDirectRelation(PawnRelationDefOf.Spouse, spouse);
            partners.Add(spouse);
        }

        foreach (Pawn partner in partners)
        {
            if (partner.ageTracker.AgeBiologicalYears < targetAge + 5)
            {
                partner.ageTracker.AgeBiologicalTicks = Rand.RangeInclusive(targetAge + 10, targetAge + 45) * (long)GenDate.TicksPerYear;
            }

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

        int returnByTick = Find.TickManager.TicksGame + Math.Max(MinContractDays, contractDays) * GenDate.TicksPerDay;

        this.activeClients.Add(new RoyaltyRegenesisClient
        {
            pawn = pawn,
            desiredAgeTicks = desiredAgeTicks,
            role = role,
            triggerRoyalAscent = triggerRoyalAscent,
            returnByTick = returnByTick,
            isPrisoner = isPrisoner,
            sourceFaction = sourceFaction,
        });
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

    private int CalculateContractDays(long bioTicksToRemove)
    {
        // Treatment is fast in-casket, but contracts are narrative stays with a hard pickup.
        double years = Math.Max(0.25, (double)bioTicksToRemove / GenDate.TicksPerYear);
        int days = (int)Math.Ceiling(8 + years * 4);
        return Math.Max(MinContractDays, Math.Min(MaxContractDays, days));
    }

    private string FormatReturnDeadline(int contractDays)
    {
        int returnTick = Find.TickManager.TicksGame + contractDays * GenDate.TicksPerDay;
        return GenDate.DateFullStringAt(returnTick, Find.WorldGrid.LongLatOf(Find.CurrentMap?.Tile ?? 0));
    }

    private void DeliverClientsByShuttle(Map map, List<Pawn> pawns, Faction faction, string label, string text)
    {
        if (map == null || pawns == null || !pawns.Any())
        {
            return;
        }

        IntVec3 cell = this.FindShuttleLandingCell(map, faction);

#if RIMWORLD12
        this.DeliverByLegacyShuttle(map, pawns, faction, cell);
#else
        this.DeliverByTransportShip(map, pawns, faction, cell);
#endif

        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent, new LookTargets(pawns));
    }

#if RIMWORLD12
    private void DeliverByLegacyShuttle(Map map, List<Pawn> pawns, Faction faction, IntVec3 cell)
    {
        Thing shuttle = ThingMaker.MakeThing(ThingDefOf.Shuttle);
        if (faction != null)
        {
            shuttle.SetFaction(faction);
        }

        CompTransporter transporter = shuttle.TryGetComp<CompTransporter>();
        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        if (transporter != null)
        {
            transporter.innerContainer.TryAddRangeOrTransfer(pawns.Cast<Thing>().ToList(), true, true);
        }

        if (compShuttle != null)
        {
            compShuttle.dropEverythingOnArrival = true;
            compShuttle.leaveAfterTicks = GenDate.TicksPerHour;
        }

        Skyfaller skyfaller = SkyfallerMaker.MakeSkyfaller(ThingDefOf.ShuttleIncoming, shuttle);
        GenPlace.TryPlaceThing(skyfaller, cell, map, ThingPlaceMode.Near);
    }

    private void DepartByLegacyShuttle(Map map, List<Pawn> pawns, Faction faction)
    {
        this.EjectClientsFromCaskets(map, pawns);

        Thing shuttle = ThingMaker.MakeThing(ThingDefOf.Shuttle);
        if (faction != null)
        {
            shuttle.SetFaction(faction);
        }

        IntVec3 cell = this.FindShuttleLandingCell(map, faction);
        GenPlace.TryPlaceThing(shuttle, cell, map, ThingPlaceMode.Near);

        CompTransporter transporter = shuttle.TryGetComp<CompTransporter>();
        CompShuttle compShuttle = shuttle.TryGetComp<CompShuttle>();
        foreach (Pawn pawn in pawns.Where(p => p != null && !p.Destroyed))
        {
            this.TransferPawnIntoTransporter(pawn, transporter);
        }

        if (compShuttle != null)
        {
            if (transporter != null && !transporter.LoadingInProgressOrReadyToLaunch)
            {
                TransporterUtility.InitiateLoading(Gen.YieldSingle(transporter));
            }

            compShuttle.Send();
        }
    }
#else
    private void DeliverByTransportShip(Map map, List<Pawn> pawns, Faction faction, IntVec3 cell)
    {
        Thing shuttle = ThingMaker.MakeThing(ThingDefOf.Shuttle);
        if (faction != null)
        {
            shuttle.SetFaction(faction);
        }

        TransportShip ship = TransportShipMaker.MakeTransportShip(
            TransportShipDefOf.Ship_Shuttle,
            pawns.Cast<Thing>(),
            shuttle);
        ship.ArriveAt(cell, map.Parent);
        ship.AddJobs(ShipJobDefOf.Unload, ShipJobDefOf.FlyAway);
    }

    private void DepartByTransportShip(Map map, List<Pawn> pawns, Faction faction)
    {
        this.EjectClientsFromCaskets(map, pawns);

        List<Pawn> living = pawns.Where(p => p != null && !p.Destroyed && !p.Dead).ToList();
        if (!living.Any())
        {
            return;
        }

        // Collect clients into the shuttle immediately so pickup is guaranteed.
        List<Thing> cargo = new List<Thing>();
        foreach (Pawn pawn in living)
        {
            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }
            else if (pawn.ParentHolder is Building_CryoRegenesis casket)
            {
                casket.EjectContents();
                if (pawn.Spawned)
                {
                    pawn.DeSpawn(DestroyMode.Vanish);
                }
            }
            else if (pawn.ParentHolder is IThingHolder holder && holder != Find.WorldPawns)
            {
                // Leave other holders if transfer fails later.
            }

            cargo.Add(pawn);
        }

        Thing shuttle = ThingMaker.MakeThing(ThingDefOf.Shuttle);
        if (faction != null)
        {
            shuttle.SetFaction(faction);
        }

        IntVec3 cell = this.FindShuttleLandingCell(map, faction);
        TransportShip ship = TransportShipMaker.MakeTransportShip(
            TransportShipDefOf.Ship_Shuttle,
            cargo,
            shuttle);
        ship.ArriveAt(cell, map.Parent);
        // Already loaded: do not unload; leave immediately.
        ship.AddJob(ShipJobDefOf.FlyAway);
    }
#endif

    private void EjectClientsFromCaskets(Map map, List<Pawn> pawns)
    {
        if (map == null)
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

    private void TransferPawnIntoTransporter(Pawn pawn, CompTransporter transporter)
    {
        if (pawn == null || transporter == null)
        {
            return;
        }

        if (pawn.Spawned)
        {
            pawn.DeSpawn(DestroyMode.Vanish);
        }

        transporter.innerContainer.TryAddOrTransfer(pawn, true);
    }

    private IntVec3 FindShuttleLandingCell(Map map, Faction faction)
    {
        Faction landingFaction = faction ?? Faction.OfPlayer;
#if RIMWORLD12
        return DropCellFinder.TradeDropSpot(map);
#else
        return DropCellFinder.GetBestShuttleLandingSpot(map, landingFaction);
#endif
    }

    private void CheckActiveClients()
    {
        if (!this.activeClients.Any())
        {
            return;
        }

        this.activeClients.RemoveAll(client => client?.pawn == null || client.pawn.Destroyed);
        if (!this.activeClients.Any())
        {
            this.MajorTrustReset(
                "CryoRegenesis clients vanished under contract. Trust is lost — the chain resets.",
                null,
                "Contract clients lost");
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

        bool allDone = this.activeClients.All(this.ClientReachedDesiredAge);
        bool deadlineReached = this.activeClients.Any(client => Find.TickManager.TicksGame >= client.returnByTick);

        if (!allDone && !deadlineReached)
        {
            return;
        }

        bool triggerEndgame = this.activeClients.Any(client => client.triggerRoyalAscent);
        List<Pawn> departing = this.activeClients.Select(client => client.pawn).Where(p => p != null).ToList();
        Faction sourceFaction = this.activeClients.FirstOrDefault()?.sourceFaction;
        Map targetMap = departing.FirstOrDefault(p => p.MapHeld != null)?.MapHeld ?? this.GetTargetMap();

        bool success = allDone;
        this.ClearContractFlags(this.activeClients);

        if (targetMap != null && departing.Any())
        {
            this.DepartClients(targetMap, departing, sourceFaction);
        }

        this.activeClients.Clear();

        if (triggerEndgame && success)
        {
            this.EndActiveContractQuest(QuestEndOutcome.Success);
            this.stage = RoyaltyRegenesisStage.Completed;
            this.royalAscentTriggered = true;
            this.CompleteChainQuest();
            this.TryMakeRoyalAscentAvailable();
            return;
        }

        if (success)
        {
            this.EndActiveContractQuest(QuestEndOutcome.Success);
            this.CompleteActiveContract();
        }
        else
        {
            // Soft failure: contract fails, stage progress kept, no full chain reset.
            this.EndActiveContractQuest(QuestEndOutcome.Fail);
            this.ScheduleRetry(
                "CryoRegenesis contract expired",
                "The pickup shuttle returned before the requested regression was finished. The clients have left. Another attempt may come later.");
        }
    }

    private void DepartClients(Map map, List<Pawn> pawns, Faction faction)
    {
#if RIMWORLD12
        this.DepartByLegacyShuttle(map, pawns, faction);
#else
        this.DepartByTransportShip(map, pawns, faction);
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

    private bool ClientReachedDesiredAge(RoyaltyRegenesisClient client)
    {
        return client.pawn.ageTracker.AgeBiologicalTicks <= client.desiredAgeTicks + CheckIntervalTicks;
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
            "The CryoRegenesis clients reached the requested regression age and have departed by shuttle. Check the Quests tab for chain progress.",
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
            int earliestReturn = this.activeClients.Min(c => c.returnByTick);
            sb.AppendLine("Next return deadline: " + RoyaltyRegenesisQuestFactory.FormatTickDate(earliestReturn));
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
        int earliestReturn = this.activeClients.Min(c => c.returnByTick);
        int ticksLeft = Math.Max(0, earliestReturn - Find.TickManager.TicksGame);
        sb.AppendLine("Return shuttle: " + RoyaltyRegenesisQuestFactory.FormatTickDate(earliestReturn)
            + " (" + ticksLeft.ToStringTicksToPeriod() + " remaining)");
        sb.AppendLine();

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client?.pawn == null)
            {
                continue;
            }

            Pawn pawn = client.pawn;
            int currentYears = pawn.ageTracker.AgeBiologicalYears;
            int targetYears = (int)(client.desiredAgeTicks / GenDate.TicksPerYear);
            bool done = this.ClientReachedDesiredAge(client);
            string location = pawn.ParentHolder is Building_CryoRegenesis
                ? "in CryoRegenesis"
                : (pawn.Spawned ? "on map" : "held");

            sb.AppendLine("• " + pawn.Name.ToStringShort
                + " — bio " + currentYears + " → " + targetYears
                + (done ? " [ready]" : " [treating]")
                + " (" + location + ")");
        }

        sb.AppendLine();
        sb.AppendLine(this.activeClients.All(this.ClientReachedDesiredAge)
            ? "All clients ready for return."
            : "Keep treating until each client reaches their target age before pickup.");
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

        int returnByTick = this.activeClients.Any()
            ? this.activeClients.Max(c => c.returnByTick)
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
    }

    private void EndActiveContractQuest(QuestEndOutcome outcome, bool sendLetter = true)
    {
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

    /// <summary>
    /// Death, recruitment, or loss of clients: fail the contract, wipe chain progress,
    /// and force a long cooldown before anyone will trust you again.
    /// </summary>
    private void MajorTrustReset(string letterText, Faction offendedFaction, string shortReason)
    {
        this.EndActiveContractQuest(QuestEndOutcome.Fail);

        this.ClearContractFlags(this.activeClients);
        this.activeClients.Clear();

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

    private Pawn GeneratePawn(PawnKindDef kind, Faction faction, int biologicalAge)
    {
        PawnGenerationRequest request = new PawnGenerationRequest(kind, faction);
        request.FixedBiologicalAge = biologicalAge;
        request.FixedChronologicalAge = biologicalAge;
        return PawnGenerator.GeneratePawn(request);
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

    /// <summary>
    /// Other planetary factions only — never Ancients, never Empire.
    /// Empire contracts begin only after these succeed.
    /// </summary>
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
            "The Emperor has reached age 20 and departed by shuttle. Royal Ascent endgame protocols are now being activated.",
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

    public void ExposeData()
    {
        Scribe_References.Look(ref this.pawn, "pawn");
        Scribe_Values.Look(ref this.desiredAgeTicks, "desiredAgeTicks", 0L);
        Scribe_Values.Look(ref this.role, "role");
        Scribe_Values.Look(ref this.triggerRoyalAscent, "triggerRoyalAscent", false);
        Scribe_Values.Look(ref this.returnByTick, "returnByTick", 0);
        Scribe_Values.Look(ref this.isPrisoner, "isPrisoner", false);
        Scribe_References.Look(ref this.sourceFaction, "sourceFaction");
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

/// <summary>
/// Regen-contract pawns never recruit voluntarily. If recruitment still happens,
/// punish relations with their sending faction.
/// </summary>
[HarmonyPatch]
/*
 * CRITICAL — TargetMethod() MUST resolve on EVERY supported RimWorld version
 * or this patch (and historically the whole assembly) fails to apply.
 *
 * Incident (2026-07, RW 1.2):
 *   This patch originally looked up only the 1.3+ DoRecruit overloads
 *   (no float recruitChance). On 1.2, AccessTools.Method returned null,
 *   harmony.PatchAll() threw, and *all* mod Harmony patches failed to apply —
 *   including Carry-to-CryoRegenesis float menu orders.
 *
 * Signature shapes:
 *   1.2:     DoRecruit(Pawn, Pawn, float recruitChance, out string, out string, bool, bool)
 *            DoRecruit(Pawn, Pawn, float recruitChance, bool)
 *   1.3–1.6: DoRecruit(Pawn, Pawn, out string, out string, bool, bool)
 *            DoRecruit(Pawn, Pawn, bool)
 *
 * Rules:
 *   - Always try the 1.2 (float) overloads first, then 1.3+ shapes.
 *   - Never return null from TargetMethod without a compile-time version gate;
 *     a null target aborts batch PatchAll (mitigated in CryoRegenesis ctor by
 *     per-type CreateClassProcessor, but a null target still means THIS patch
 *     never runs).
 *   - After any DoRecruit / recruit API change, boot RW 1.2 and confirm no
 *     "[CryoRegenesis] Harmony patch failed on ...Patch_DoRecruit_RegenContract".
 */
public static class Patch_DoRecruit_RegenContract
{
    private static MethodBase TargetMethod()
    {
        // RimWorld 1.2: DoRecruit(..., float recruitChance, out string, out string, bool, bool)
        MethodInfo rw12 = AccessTools.Method(
            typeof(InteractionWorker_RecruitAttempt),
            nameof(InteractionWorker_RecruitAttempt.DoRecruit),
            new[]
            {
                typeof(Pawn),
                typeof(Pawn),
                typeof(float),
                typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType(),
                typeof(bool),
                typeof(bool),
            });

        if (rw12 != null)
        {
            return rw12;
        }

        // RimWorld 1.3+: recruitChance argument was removed.
        MethodInfo full = AccessTools.Method(
            typeof(InteractionWorker_RecruitAttempt),
            nameof(InteractionWorker_RecruitAttempt.DoRecruit),
            new[]
            {
                typeof(Pawn),
                typeof(Pawn),
                typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType(),
                typeof(bool),
                typeof(bool),
            });

        if (full != null)
        {
            return full;
        }

        // Last-resort short overloads.
        MethodInfo short12 = AccessTools.Method(
            typeof(InteractionWorker_RecruitAttempt),
            nameof(InteractionWorker_RecruitAttempt.DoRecruit),
            new[] { typeof(Pawn), typeof(Pawn), typeof(float), typeof(bool) });

        if (short12 != null)
        {
            return short12;
        }

        return AccessTools.Method(
            typeof(InteractionWorker_RecruitAttempt),
            nameof(InteractionWorker_RecruitAttempt.DoRecruit),
            new[] { typeof(Pawn), typeof(Pawn), typeof(bool) });
    }

    public static void Prefix(Pawn recruiter, Pawn recruitee)
    {
        if (!RoyaltyRegenesisQuestSystem.IsActiveRegenContractPawn(recruitee))
        {
            return;
        }

        RoyaltyRegenesisQuestSystem.CurrentSystem?.NotifyClientRecruited(recruitee);
    }
}
