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

    /// Non-regen escorts (e.g. Emperor-stage Stellic guards). Arrive/leave with the party
    /// but never receive a CryoRegenesis contract.
    private List<Pawn> contractEscorts = new List<Pawn>();

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

    /// Labels of free colony colonists last seen aboard the Emperor-stage pickup shuttle.
    /// Snapshotted every tick while the ship is parked so departure (which destroys
    /// container contents) can still fire the Imperial Court endgame.
    private List<string> emperorShuttleColonistEscapeeLabels = new List<string>();

    /// True once any Emperor-stage victory/defeat endgame has been started for this run.
    private bool emperorColonistEndgameTriggered;

    /// Best Count/Countess candidate snapshotted while the Emperor pickup is parked
    /// (may leave on the shuttle — keep label for credits even if the pawn is destroyed).
    private Pawn emperorUsurpationCountCandidate;
    private string emperorUsurpationCountLabel = string.Empty;

    /// Colonist elevated to Special Consul when the party leaves with the Emperor: the
    /// highest-ranked passenger, and among equal ranks the one holding the most Honor.
    private Pawn emperorCourtHonoree;
    private string emperorCourtHonoreeLabel = string.Empty;

    /// Planetkiller duration after the Emperor is murdered under contract (2 days).
    private const int EmperorDeathPlanetkillerTicks = 60_000 * 2;

    /// Last logged state of the Imperial shuttle's colonist door (log-only, not saved).
    private bool? emperorColonistBoardingOpen;

    /// True once this pickup has told the player which archons must board.
    private bool emperorNobleRequirementAnnounced;

    private Pawn emperor;

    /// The number of the Emperor's wives.
    private int wifeCount = 0;

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
    /// Active contract clients always may. Free colonists may board when they have a
    /// non-ex DirectRelation to a client — or, during the Emperor contract, when the
    /// Imperial shuttle is open to colonists (see
    /// <see cref="MayColonistsBoardImperialShuttle"/>).
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

        // Emperor pickup: free colony colonists may leave with the Imperial shuttle, but only
        // while the Emperor is secured and the rest of the party is done. This is the only rule
        // for colonists during the Emperor stage — relatives of a client get no side door.
        if (system.IsEmperorRegenContractActive() && system.IsFreeColonyColonistForEmperorEndgame(pawn))
        {
            return system.MayColonistsBoardImperialShuttle(out _);
        }

        IEnumerable<Pawn> clients = system.activeClients
            .Where(client => client?.pawn != null && !client.pawn.Destroyed)
            .Select(client => client.pawn);

        if (RoyaltyRegenesisQuestPartners.IsNonExDirectRelationOfAny(pawn, clients))
        {
            return true;
        }

        // Emperor-stage Stellic guards and other non-regen escorts.
        return system.contractEscorts != null && system.contractEscorts.Contains(pawn);
    }

    /// True while the Emperor / High Stellarch regen contract is the active visit.
    public bool IsEmperorRegenContractActive()
    {
        return this.activeContractStage == RoyaltyRegenesisStage.EmperorArrival
            && this.activeClients != null
            && this.activeClients.Any();
    }

    /// A real player colonist (not a temporary quest-lodger client/escort) eligible to
    /// board the Emperor pickup and trigger the Imperial Court victory ending.
    public bool IsFreeColonyColonistForEmperorEndgame(Pawn pawn)
    {
        if (pawn == null || pawn.Destroyed || pawn.Dead)
        {
            return false;
        }

        if (!pawn.IsColonist || pawn.IsQuestLodger())
        {
            return false;
        }

        if (IsActiveRegenContractPawn(pawn))
        {
            return false;
        }

        if (this.contractEscorts != null && this.contractEscorts.Contains(pawn))
        {
            return false;
        }

        // Free colonists only — prisoners / slaves do not count as choosing to leave.
        return pawn.IsFreeColonist;
    }

    /// Whether the Emperor's Imperial shuttle is currently open to free colony colonists.
    /// Any number of them (none through all of them) may leave, but only while both
    /// conditions hold:
    ///   1. The Emperor is alive and either already inside the shuttle or sealed in a
    ///      powered-off CryoRegenesis casket.
    ///   2. Every other contracted guest is already offworld, or inside the shuttle after
    ///      reaching their target age.
    /// Checked live on every embark test, so the door opens the moment the last guest is
    /// aboard and closes again if the Emperor is powered back up or a guest steps out.
    /// Escorting Stellic guards are not contracted guests and do not hold the door shut.
    public bool MayColonistsBoardImperialShuttle(out string blockReason)
    {
        blockReason = null;
        if (!this.IsEmperorRegenContractActive())
        {
            blockReason = "the Emperor contract is not active";
            return false;
        }

        Pawn emperorPawn = this.GetEmperorClient()?.pawn;
        if (emperorPawn == null || emperorPawn.Dead || emperorPawn.Destroyed)
        {
            blockReason = "the Emperor is not alive";
            return false;
        }

        if (!this.IsClientAboardContractShuttle(emperorPawn) && !this.IsEmperorInUnpoweredCryoCasket())
        {
            blockReason = emperorPawn.LabelShort
                + " is neither aboard the shuttle nor sealed in a powered-off CryoRegenesis casket";
            return false;
        }

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client == null || client?.pawn == this.emperor)
            {
                continue;
            }

            Pawn guest = client.pawn;
            if (guest == null || guest.Destroyed)
            {
                // Left with an earlier wave — the shuttle destroys its cargo on leave.
                continue;
            }

            if (guest.Dead)
            {
                blockReason = guest.LabelShort + " is dead";
                return false;
            }

            if (this.IsClientAboardContractShuttle(guest))
            {
                if (!client.everReachedDesiredAge)
                {
                    blockReason = guest.LabelShort + " is aboard but has not reached their target age";
                    return false;
                }

                continue;
            }

            // Already offworld: returned with an earlier wave, or otherwise no longer map-held.
            if (this.WasSuccessfullyReturned(guest) || !this.IsClientAvailableOnMap(guest))
            {
                continue;
            }

            blockReason = guest.LabelShort + " is still on the map";
            return false;
        }

        return true;
    }

    /// Tells the player once per pickup which archons the Imperial shuttle will not leave
    /// without, so a locked Send is never a mystery.
    private void AnnounceImperialNobleRequirement(List<Pawn> nobles)
    {
        if (this.emperorNobleRequirementAnnounced || nobles == null || !nobles.Any())
        {
            return;
        }

        this.emperorNobleRequirementAnnounced = true;
        string names = string.Join(", ", nobles.Select(p => p.LabelShort));
        this.LogRoyaltyDebug("Imperial shuttle now requires colony archon(s): " + names + ".");

        Find.LetterStack.ReceiveLetter(
            "The throne must travel",
            "The Emperor's fate is settled, and the Empire will not leave its own nobility on a "
            + "rimworld. The Imperial shuttle will not launch until " + names + " "
            + (nobles.Count == 1 ? "is" : "are") + " aboard.\n\n"
            + "Whoever is not on that shuttle when it goes is left behind for good.",
            LetterDefOf.NeutralEvent,
            new LookTargets(nobles[0]));
    }

    /// Logs only when the Imperial shuttle opens or closes to colonists, so the reason a
    /// colonist is turned away at the ramp is visible without a per-tick flood.
    private void LogImperialShuttleBoardingChanges()
    {
        bool open = this.MayColonistsBoardImperialShuttle(out string blockReason);
        if (this.emperorColonistBoardingOpen == open)
        {
            return;
        }

        this.emperorColonistBoardingOpen = open;
        this.LogRoyaltyDebug(open
            ? "Imperial shuttle is now open to colonists (Emperor secured, party done)."
            : "Imperial shuttle is closed to colonists — " + blockReason + ".");
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
        Scribe_Collections.Look(ref this.contractEscorts, "crRoyalContractEscorts", LookMode.Reference);
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
        Scribe_Collections.Look(ref this.emperorShuttleColonistEscapeeLabels, "crRoyalEmperorShuttleColonistEscapees", LookMode.Value);
        Scribe_Values.Look(ref this.emperorColonistEndgameTriggered, "crRoyalEmperorColonistEndgameTriggered", false);
        Scribe_Values.Look(ref this.emperorNobleRequirementAnnounced, "crRoyalEmperorNobleRequirementAnnounced", false);
        Scribe_References.Look(ref this.emperor, "crRoyalEmperor");
        Scribe_References.Look(ref this.emperorUsurpationCountCandidate, "crRoyalEmperorUsurpationCount");
        Scribe_Values.Look(ref this.emperorUsurpationCountLabel, "crRoyalEmperorUsurpationCountLabel");
        Scribe_References.Look(ref this.emperorCourtHonoree, "crRoyalEmperorCourtHonoree");
        Scribe_Values.Look(ref this.emperorCourtHonoreeLabel, "crRoyalEmperorCourtHonoreeLabel");
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

            if (this.contractEscorts == null)
            {
                this.contractEscorts = new List<Pawn>();
            }
            else
            {
                this.contractEscorts.RemoveAll(p => p == null || p.Destroyed);
            }

            if (this.returnedClientPawnIds == null)
            {
                this.returnedClientPawnIds = new List<int>();
            }

            if (this.emperorShuttleColonistEscapeeLabels == null)
            {
                this.emperorShuttleColonistEscapeeLabels = new List<string>();
            }

            if (this.emperorUsurpationCountLabel == null)
            {
                this.emperorUsurpationCountLabel = string.Empty;
            }

            if (this.emperorCourtHonoreeLabel == null)
            {
                this.emperorCourtHonoreeLabel = string.Empty;
            }

            if (this.lastTrustBreakReason == null)
            {
                this.lastTrustBreakReason = string.Empty;
            }

            // Older saves do not persist the Emperor cache. Resolve it once after loading.
            this.CacheEmperorClient();

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

        // Every tick while the Emperor pickup is parked: remember free colonists aboard
        // so the endgame still fires after the shuttle destroys its cargo on leave.
        if (this.pickupShuttleSpawned && this.IsEmperorRegenContractActive())
        {
            this.RefreshEmperorShuttleColonistSnapshot();
            this.LogImperialShuttleBoardingChanges();
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
        // destroy boarding passengers) — except Emperor murder, which arms a Planetkiller.
        if (this.activeClients.Any(client => client.pawn != null && client.pawn.Dead))
        {
            RoyaltyRegenesisClient deadClient = this.activeClients
                .First(client => client.pawn != null && client.pawn.Dead);
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

    /// Called from CompShuttle.SendLaunchedSignals just before cargo is destroyed.
    /// Ensures same-tick board-and-launch still records free colonists for the endgame.
    public void NotifyEmperorPickupLaunching(CompShuttle shuttleComp)
    {
        if (shuttleComp?.parent == null || this.emperorColonistEndgameTriggered)
        {
            return;
        }

        if (!this.IsRegenPickupShuttle(shuttleComp.parent) || !this.IsEmperorRegenContractActive())
        {
            return;
        }

        CompTransporter transporter = shuttleComp.Transporter;

        // Boarding conditions can be undone after colonists board — the Emperor's casket
        // powered back up, a guest carried back out. They must never fly off (and be destroyed
        // with the cargo) without the ending they boarded for.
        if (!this.MayColonistsBoardImperialShuttle(out string blockReason))
        {
            this.PutColonistsBackOnMapBeforeLaunch(transporter, blockReason);
        }

        this.CaptureFreeColonistsFromTransporter(transporter);
        this.RefreshEmperorEndgameCandidates(transporter);
    }

    /// Unloads free colony colonists from the Imperial shuttle just before it launches when
    /// the boarding conditions no longer hold. Contract guests and escorts still leave.
    private void PutColonistsBackOnMapBeforeLaunch(CompTransporter transporter, string blockReason)
    {
        Thing shuttle = transporter?.parent;
        if (transporter?.innerContainer == null || shuttle == null)
        {
            return;
        }

        Map map = shuttle.MapHeld ?? this.GetTargetMap();
        if (map == null)
        {
            return;
        }

        List<Pawn> colonists = transporter.innerContainer
            .OfType<Pawn>()
            .Where(this.IsFreeColonyColonistForEmperorEndgame)
            .ToList();
        if (!colonists.Any())
        {
            return;
        }

        IntVec3 dropCell = shuttle.PositionHeld;
        List<Pawn> unloaded = new List<Pawn>();
        foreach (Pawn colonist in colonists)
        {
            if (transporter.innerContainer.TryDrop(
                    colonist,
                    dropCell,
                    map,
                    ThingPlaceMode.Near,
                    out Thing _))
            {
                unloaded.Add(colonist);
            }
        }

        if (!unloaded.Any())
        {
            return;
        }

        this.LogRoyaltyDebug(
            "Unloaded " + unloaded.Count + " colonist(s) before Imperial shuttle launch — "
            + blockReason + ": " + string.Join(", ", unloaded.Select(p => p.LabelShort)));

        Find.LetterStack.ReceiveLetter(
            "Turned away at the ramp",
            "The Imperial shuttle refused to carry your colonists because " + blockReason
            + ". They were put off the ship before it launched.",
            LetterDefOf.NegativeEvent,
            new LookTargets(unloaded[0]));
    }

    /// While the Emperor pickup is parked, record free colony colonists currently aboard.
    /// Labels are stored because the shuttle destroys container contents on launch.
    private void RefreshEmperorShuttleColonistSnapshot()
    {
        if (this.emperorColonistEndgameTriggered)
        {
            return;
        }

        Thing shuttle = this.GetContractShuttleThing();
        if (shuttle == null || shuttle.Destroyed)
        {
            return;
        }

        CompTransporter transporter = shuttle.TryGetComp<CompTransporter>();
        this.CaptureFreeColonistsFromTransporter(transporter);

        // Picking the heir scans the map, so do it once a second rather than every tick.
        // The launch prefix refreshes it again, which covers same-tick board-and-launch.
        if (Find.TickManager.TicksGame % 60 == 0)
        {
            this.RefreshEmperorEndgameCandidates(transporter);
        }
    }

    private void CaptureFreeColonistsFromTransporter(CompTransporter transporter)
    {
        if (transporter?.innerContainer == null)
        {
            return;
        }

        if (this.emperorShuttleColonistEscapeeLabels == null)
        {
            this.emperorShuttleColonistEscapeeLabels = new List<string>();
        }

        this.emperorShuttleColonistEscapeeLabels.Clear();
        foreach (Thing thing in transporter.innerContainer)
        {
            Pawn pawn = thing as Pawn;
            if (pawn != null && this.IsFreeColonyColonistForEmperorEndgame(pawn))
            {
                this.emperorShuttleColonistEscapeeLabels.Add(pawn.LabelCap);
            }
        }
    }

    /// Resolves the Emperor once and caches his pawn for all later identity checks.
    private void CacheEmperorClient()
    {
        if (this.emperor != null || this.activeClients == null)
        {
            return;
        }

        RoyaltyRegenesisClient emperorClient = this.activeClients.FirstOrDefault(client =>
            client != null
            && (client.triggerRoyalAscent
                || (!client.role.NullOrEmpty()
                    && string.Equals(client.role, "emperor", StringComparison.OrdinalIgnoreCase))));

        this.emperor = emperorClient?.pawn;
    }

    private RoyaltyRegenesisClient GetEmperorClient()
    {
        return this.emperor == null
            ? null
            : this.activeClients?.FirstOrDefault(client => client?.pawn == this.emperor);
    }

    private bool IsEmperorWifeClient(RoyaltyRegenesisClient client)
    {
        if (client?.role == null)
        {
            return false;
        }

        return client.role.IndexOf("wife", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// Living Emperor sealed in a flicked-off / unpowered CryoRegenesis pod (normal cryptosleep mode).
    private bool IsEmperorInUnpoweredCryoCasket()
    {
        RoyaltyRegenesisClient emperor = this.GetEmperorClient();
        Pawn pawn = emperor?.pawn;
        if (pawn == null || pawn.Destroyed || pawn.Dead)
        {
            return false;
        }

        Building_CryoRegenesis casket = pawn.ParentHolder as Building_CryoRegenesis;
        return casket != null && casket.IsUnpoweredCryptosleepMode;
    }

    /// True when the living Emperor is still map-reachable (spawned or in a casket).
    private bool IsEmperorStillAvailableOnMap()
    {
        RoyaltyRegenesisClient emperor = this.GetEmperorClient();
        return emperor?.pawn != null && this.IsClientAvailableOnMap(emperor.pawn);
    }

    /// The Empire's archon rank. The defName is "Count" in every supported version; only the
    /// display label changed — "count"/"countess" before 1.6, "archon" from 1.6 on.
    private static RoyalTitleDef ArchonTitleDef()
    {
        return DefDatabase<RoyalTitleDef>.GetNamedSilentFail("Count");
    }

    /// A colonist who could take the Imperial throne: one of our own free colonists holding at
    /// least the archon rank. More senior colony titles (dominus, consul) qualify as well.
    private bool IsImperialNobleColonist(Pawn pawn)
    {
        if (pawn == null || pawn.Dead || pawn.Destroyed || pawn.royalty == null)
        {
            return false;
        }

        if (!this.IsFreeColonyColonistForEmperorEndgame(pawn))
        {
            return false;
        }

        Faction empire = this.EmpireFaction();
        RoyalTitleDef archon = ArchonTitleDef();
        if (empire == null || archon == null)
        {
            return false;
        }

        RoyalTitleDef title = pawn.royalty.GetCurrentTitle(empire);
        return title != null && title.seniority >= archon.seniority;
    }

    /// Every throne-eligible colonist on the shuttle's map: walking around, inside a casket, or
    /// already aboard. Nobody off this map is counted — like any ship launch, whoever is not on
    /// the shuttle when it goes is left behind.
    private List<Pawn> GetImperialNobleColonists(Thing shuttle)
    {
        List<Pawn> nobles = new List<Pawn>();
        Map map = shuttle?.MapHeld ?? this.GetTargetMap();
        if (map != null)
        {
            foreach (Pawn pawn in map.mapPawns.AllPawns)
            {
                if (this.IsImperialNobleColonist(pawn))
                {
                    nobles.Add(pawn);
                }
            }

            // Casket contents are not always listed among map pawns.
            foreach (Building_CryoRegenesis casket in
                map.listerBuildings.AllBuildingsColonistOfClass<Building_CryoRegenesis>())
            {
                if (casket.ContainedThing is Pawn contained && this.IsImperialNobleColonist(contained))
                {
                    nobles.Add(contained);
                }
            }
        }

        CompTransporter transporter = shuttle?.TryGetComp<CompTransporter>();
        if (transporter?.innerContainer != null)
        {
            foreach (Thing thing in transporter.innerContainer)
            {
                if (thing is Pawn aboard && this.IsImperialNobleColonist(aboard))
                {
                    nobles.Add(aboard);
                }
            }
        }

        return nobles.Distinct().ToList();
    }

    /// Throne-eligible colonists who must be aboard before the Imperial shuttle may launch.
    /// Empty until the Emperor's fate is settled — earlier waves must not drag nobility along.
    private List<Pawn> GetRequiredImperialNobleColonists(Thing shuttle)
    {
        if (!this.IsEmperorRegenContractActive() || !this.IsEmperorRegeneratedOrSealed())
        {
            return new List<Pawn>();
        }

        return this.GetImperialNobleColonists(shuttle);
    }

    /// True once the Emperor's fate is settled either way: he is alive and either fully
    /// regenerated or shut inside a powered-off casket. From that moment the colony's archons
    /// leave on the Imperial shuttle — at his side, or over his sleeping body.
    private bool IsEmperorRegeneratedOrSealed()
    {
        RoyaltyRegenesisClient emperor = this.GetEmperorClient();
        Pawn pawn = emperor?.pawn;
        if (pawn == null || pawn.Dead || pawn.Destroyed)
        {
            return false;
        }

        return emperor.everReachedDesiredAge || this.IsEmperorInUnpoweredCryoCasket();
    }

    /// Empire rank of a colonist for endgame selection; 0 when untitled.
    private int ImperialSeniorityOf(Pawn pawn, Faction empire)
    {
        RoyalTitleDef title = pawn?.royalty?.GetCurrentTitle(empire);
        return title?.seniority ?? 0;
    }

    /// Honor the colonist holds with the Empire — the tie-breaker between equal ranks.
    private int ImperialHonorOf(Pawn pawn, Faction empire)
    {
        if (pawn?.royalty == null || empire == null)
        {
            return 0;
        }

        return pawn.royalty.GetFavor(empire);
    }

    private static IEnumerable<Pawn> PawnsAboard(CompTransporter transporter)
    {
        if (transporter?.innerContainer == null)
        {
            yield break;
        }

        foreach (Thing thing in transporter.innerContainer)
        {
            if (thing is Pawn pawn)
            {
                yield return pawn;
            }
        }
    }

    /// Colonist who could take the throne: aboard the shuttle first — the usurper named in the
    /// credits has to be someone who actually leaves — then the highest rank, and where ranks
    /// tie, the one holding the most Honor.
    private Pawn FindColonyArchon(CompTransporter alsoSearch = null)
    {
        Faction empire = this.EmpireFaction();
        if (empire == null || ArchonTitleDef() == null)
        {
            return null;
        }

        HashSet<Pawn> aboard = new HashSet<Pawn>(
            PawnsAboard(alsoSearch).Where(this.IsImperialNobleColonist));

        return this.GetImperialNobleColonists(this.GetContractShuttleThing())
            .Concat(aboard)
            .Distinct()
            .OrderByDescending(p => aboard.Contains(p))
            .ThenByDescending(p => this.ImperialSeniorityOf(p, empire))
            .ThenByDescending(p => this.ImperialHonorOf(p, empire))
            .FirstOrDefault();
    }

    /// The colonist the Empire elevates at the Imperial Court ending: the highest-ranked one
    /// aboard the shuttle, and where several share that rank, the one with the most Honor.
    private Pawn FindImperialCourtHonoree(CompTransporter transporter)
    {
        Faction empire = this.EmpireFaction();
        if (empire == null)
        {
            return null;
        }

        return PawnsAboard(transporter)
            .Where(this.IsFreeColonyColonistForEmperorEndgame)
            .OrderByDescending(p => this.ImperialSeniorityOf(p, empire))
            .ThenByDescending(p => this.ImperialHonorOf(p, empire))
            .FirstOrDefault();
    }

    /// Both endgame honours are chosen while the shuttle is still parked, because launching it
    /// destroys the cargo the choice is made from.
    private void RefreshEmperorEndgameCandidates(CompTransporter transporter = null)
    {
        Pawn archon = this.FindColonyArchon(transporter);
        if (archon != null)
        {
            this.emperorUsurpationCountCandidate = archon;
            this.emperorUsurpationCountLabel = archon.Name?.ToStringShort ?? archon.LabelShort;
        }

        // The honoree must be aboard, so this tracks the manifest both ways — someone stepping
        // back off the ship gives up the title.
        Pawn honoree = this.FindImperialCourtHonoree(transporter);
        if (honoree == this.emperorCourtHonoree)
        {
            return;
        }

        this.emperorCourtHonoree = honoree;
        this.emperorCourtHonoreeLabel = honoree != null
            ? honoree.Name?.ToStringShort ?? honoree.LabelShort
            : string.Empty;

        if (honoree != null)
        {
            Faction empire = this.EmpireFaction();
            this.LogRoyaltyDebug(
                "Imperial Court honoree is " + this.emperorCourtHonoreeLabel
                + " (seniority=" + this.ImperialSeniorityOf(honoree, empire)
                + " honor=" + this.ImperialHonorOf(honoree, empire) + ").");
        }
    }

    /// Name of the colonist elevated at the Imperial Court ending. Falls back to the snapshotted
    /// label because the launching shuttle destroys its passengers.
    private string ResolveCourtHonoreeLabel()
    {
        if (this.emperorCourtHonoree != null && !this.emperorCourtHonoree.Destroyed)
        {
            return this.emperorCourtHonoree.Name?.ToStringShort ?? this.emperorCourtHonoree.LabelShort;
        }

        return this.emperorCourtHonoreeLabel;
    }

    private Pawn ResolveUsurpationCount()
    {
        if (this.emperorUsurpationCountCandidate != null
            && !this.emperorUsurpationCountCandidate.Destroyed
            && !this.emperorUsurpationCountCandidate.Dead)
        {
            return this.emperorUsurpationCountCandidate;
        }

        return this.FindColonyArchon();
    }

    private List<string> GetEmperorWifeLabels(IEnumerable<RoyaltyRegenesisClient> clients = null)
    {
        IEnumerable<RoyaltyRegenesisClient> source = clients ?? this.activeClients;
        List<string> labels = new List<string>();
        foreach (RoyaltyRegenesisClient client in source.Where(this.IsEmperorWifeClient))
        {
            Pawn pawn = client?.pawn;
            string label = pawn != null && !pawn.Destroyed
                ? pawn.LabelShort
                : (client.role ?? "imperial wife");
            if (!label.NullOrEmpty() && !labels.Contains(label))
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    /// Branch 1: shuttle left while the living Emperor is sealed in an unpowered CryoRegenesis
    /// casket, and the colony has a Count/Countess → "You Keep What You Kill" victory.
    private bool TryTriggerYouKeepWhatYouKillEndgame(
        List<RoyaltyRegenesisClient> remaining,
        List<RoyaltyRegenesisClient> departed,
        string reason)
    {
        if (this.emperorColonistEndgameTriggered
            || this.activeContractStage != RoyaltyRegenesisStage.EmperorArrival)
        {
            return false;
        }

        if (!this.IsEmperorInUnpoweredCryoCasket())
        {
            return false;
        }

        // Emperor must still be among remaining map clients (sealed, not aboard).
        if (!remaining.Any(client => client?.pawn == this.emperor))
        {
            return false;
        }

        Pawn count = this.ResolveUsurpationCount();
        bool hasCount = count != null
            || !this.emperorUsurpationCountLabel.NullOrEmpty();
        if (!hasCount)
        {
            this.LogRoyaltyDebug(
                "Emperor sealed in unpowered casket but colony has no Count/Countess — no usurpation ending.");
            return false;
        }

        List<RoyaltyRegenesisClient> allKnown = remaining
            .Concat(departed ?? Enumerable.Empty<RoyaltyRegenesisClient>())
            .Where(c => c != null)
            .ToList();
        this.TriggerYouKeepWhatYouKillEndgame(count, allKnown, reason);
        this.FinishEmperorUsurpationContract(departed);
        return true;
    }

    private static int CountLivingWives(Pawn human)
    {
        if (human?.relations == null)
        {
            return 0;
        }

        int count = 0;
        foreach (DirectPawnRelation rel in human.relations.DirectRelations)
        {
            if (rel.def == PawnRelationDefOf.Spouse
                && rel.otherPawn != null
                && rel.otherPawn.gender == Gender.Female
                && !rel.otherPawn.Dead)
            {
                count++;
            }
        }

        return count;
    }

    /// Victory: Count forges an alliance with the Emperor's wives and is recognized as Emperor.
    private void TriggerYouKeepWhatYouKillEndgame(
        Pawn count,
        List<RoyaltyRegenesisClient> knownClients,
        string reason)
    {
        if (this.emperorColonistEndgameTriggered)
        {
            return;
        }

        if (ShipCountdown.CountingDown)
        {
            return;
        }

        this.emperorColonistEndgameTriggered = true;
        List<string> wifeLabels = this.GetEmperorWifeLabels(knownClients);
        string countName = count != null && !count.Destroyed
            ? (count.Name?.ToStringShort ?? count.LabelShort)
            : (this.emperorUsurpationCountLabel.NullOrEmpty()
                ? "your Count"
                : this.emperorUsurpationCountLabel);
        string wivesText = wifeLabels.Any()
            ? string.Join(", ", wifeLabels)
            : "the Emperor's wives";

        this.LogRoyaltyDebug(
            "You Keep What You Kill endgame: Count " + countName
            + " / wives=[" + wivesText + "] (" + reason + ").");

        Faction empire = this.EmpireFaction();
        if (empire != null && count != null && !count.Destroyed && !count.Dead)
        {
            // Crown the usurper before the credits.
            SpawnStoryHuman.TrySetRoyalTitle(count, empire, "Emperor");
            // Reflection fallback for older title-set paths.
            this.TrySetRoyalTitle(count, empire, "Emperor");
        }

        wifeCount = CountLivingWives(emperor);

        #if RIMWORLD16
        string title = "Archon";
        #else
        string title = "Count";
        #endif

        string intro = "You Keep What You Kill.\n\n";
        string ending =
            $"{title} {countName} forged an alliance with the Emperor's {(wifeCount == 1 ? "wife" : "wives")}. "
            + "In the proud tradition of The Empire: \"You Keep What You Kill\": "
            + $"Now {countName} is seen across The Empire as its latest Emperor.\n\n"
            + "The desposed emperor sleeps in a dark cryptosleep casket in a secret base "
            + "long abandoned. No rescue will ever come.\n\n"
            + "The choice — and the throne — is yours.";

        // Escapee line: the Count (and any free colonists who left with the wives).
        StringBuilder escapees = new StringBuilder();
        if (count != null && !count.Destroyed)
        {
            escapees.AppendLine("   " + count.LabelCap);
        }
        else if (!this.emperorUsurpationCountLabel.NullOrEmpty())
        {
            escapees.AppendLine("   " + this.emperorUsurpationCountLabel);
        }

        if (this.emperorShuttleColonistEscapeeLabels != null)
        {
            foreach (string label in this.emperorShuttleColonistEscapeeLabels)
            {
                if (!label.NullOrEmpty()
                    && !string.Equals(label, countName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(label, count?.LabelCap, StringComparison.OrdinalIgnoreCase))
                {
                    escapees.AppendLine("   " + label);
                }
            }
        }

        if (Find.StoryWatcher?.statsRecord != null)
        {
            int launched = 1 + (this.emperorShuttleColonistEscapeeLabels?.Count ?? 0);
            Find.StoryWatcher.statsRecord.colonistsLaunched += Math.Max(1, launched);
        }

        string credits = GameVictoryUtility.MakeEndCredits(intro, ending, escapees.ToString());
        ShipCountdown.InitiateCountdown(credits);

        this.stage = RoyaltyRegenesisStage.Completed;
        this.royalAscentTriggered = true;
        this.CompleteChainQuest();
    }

    /// After usurpation: wives (and other departed) are gone; seal the Emperor out of the
    /// contract so trust does not break; finish the Emperor visit as a dark success.
    private void FinishEmperorUsurpationContract(List<RoyaltyRegenesisClient> departed)
    {
        RoyaltyRegenesisClient emperor = this.GetEmperorClient();
        Pawn emperorPawn = emperor?.pawn;

        // Credit the departing wave (wives / stellarch / escorts) without requiring the Emperor.
        int readyDeparted = departed?.Count(c => c != null && c.everReachedDesiredAge) ?? 0;
        this.clientsSuccessfullyReturned += readyDeparted;
        if (departed != null)
        {
            this.FinalizeDepartedWaveClients(departed);
        }

        // Emperor remains sealed — drop contract flags so death/leave logic stops tracking him.
        if (emperorPawn != null)
        {
            this.ClearContractFlags(new[] { emperor });
            this.RemoveClientFromContractQuest(emperorPawn);
            // Leave him in the dark casket; do not eject.
        }

        this.GrantCompletionRewardIfEligible(RoyaltyRegenesisStage.EmperorArrival);
        this.EndActiveContractQuest(QuestEndOutcome.Success, sendLetter: false);
        this.activeClients.Clear();
        this.ClearContractShuttle();
        this.ClearContractTiming();
        this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
        this.stage = RoyaltyRegenesisStage.Completed;

        Find.LetterStack.ReceiveLetter(
            "You Keep What You Kill",
            "The Imperial shuttle has fled without the Emperor. He remains sealed in an unpowered "
            + "CryoRegenesis casket — a secret tomb no one will ever find.\n\n"
            + "The Empire recognizes a new Emperor among your Counts.",
            LetterDefOf.PositiveEvent,
            emperorPawn != null ? new LookTargets(emperorPawn) : null);
    }

    /// Branch 2: the Emperor died under contract — survivors evacuate immediately and a
    /// Planetkiller is armed for two days. Trust is destroyed.
    private void HandleEmperorDeathEndgame(Pawn emperor, Faction empire)
    {
        if (this.emperorColonistEndgameTriggered)
        {
            return;
        }

        this.emperorColonistEndgameTriggered = true;
        this.LogRoyaltyDebug(
            "Emperor death endgame: " + (emperor?.Name?.ToStringShort ?? "?")
            + " — arming Planetkiller and evacuating survivors.");

        List<Pawn> survivors = this.activeClients
            .Where(c => c?.pawn != null && !c.pawn.Dead && !c.pawn.Destroyed)
            .Select(c => c.pawn)
            .Concat(this.contractEscorts.Where(p => p != null && !p.Dead && !p.Destroyed))
            .Distinct()
            .ToList();

        Map map = survivors.FirstOrDefault(p => p.MapHeld != null)?.MapHeld
            ?? this.GetTargetMap()
            ?? emperor?.MapHeld;

        if (map != null && survivors.Any())
        {
            // Eject anyone still in caskets and force an immediate imperial evacuation.
            this.ForceImmediateImperialEvacuation(map, survivors, empire ?? this.EmpireFaction());
        }

        this.SchedulePlanetkiller(EmperorDeathPlanetkillerTicks);

        string emperorName = emperor?.Name?.ToStringShort ?? "The Emperor";
        this.MajorTrustReset(
            emperorName + " has been killed under a CryoRegenesis contract. "
            + "The Imperial party flees at once. The Empire's answer is final: "
            + "a Planetkiller has been armed — this world ends in two days.\n\n"
            + "Trust is shattered. The Imperial Rejuvenation chain restarts from the beginning.",
            empire ?? this.EmpireFaction(),
            "Emperor killed under contract");
    }

    /// Board surviving contract guests and force the pickup to leave as soon as possible.
    private void ForceImmediateImperialEvacuation(Map map, List<Pawn> survivors, Faction faction)
    {
        if (map == null || survivors == null || !survivors.Any())
        {
            return;
        }

        List<Pawn> living = survivors
            .Where(p => p != null && !p.Destroyed && !p.Dead)
            .Distinct()
            .ToList();
        if (!living.Any())
        {
            return;
        }

        this.EjectClientsFromCaskets(map, living);

        // Spawn / reuse a short-stay pickup and require every survivor.
        if (!this.pickupShuttleSpawned || this.GetUsableContractShuttle(map) == null)
        {
            this.SpawnPickupShuttle(map, living, faction, longStay: false);
        }

        Thing shuttle = this.GetUsableContractShuttle(map) ?? this.GetContractShuttleThing();
        CompShuttle comp = shuttle?.TryGetComp<CompShuttle>();
        CompTransporter transporter = shuttle?.TryGetComp<CompTransporter>();
        if (comp != null)
        {
            comp.requiredPawns.Clear();
            comp.requiredPawns.AddRange(living);
            this.ConfigureContractShuttleEmbarkRules(comp);
#if RIMWORLD12
            comp.leaveImmediatelyWhenSatisfied = true;
            comp.leaveAfterTicks = GenDate.TicksPerHour;
#endif
        }

#if !RIMWORLD12
        if (this.contractTransportShip != null
            && this.contractTransportShip.curJob is ShipJob_Wait waitJob)
        {
            waitJob.leaveImmediatelyWhenSatisfied = true;
            waitJob.showGizmos = true;
        }
#endif

        // Stuff survivors into the ship immediately so leave-when-satisfied can fire.
        if (transporter != null)
        {
            foreach (Pawn pawn in living)
            {
                if (transporter.innerContainer.Contains(pawn))
                {
                    continue;
                }

                if (pawn.Spawned)
                {
                    pawn.DeSpawn();
                }

                if (!pawn.Destroyed && !transporter.innerContainer.Contains(pawn))
                {
                    transporter.innerContainer.TryAddOrTransfer(pawn, false);
                }
            }
        }

        this.TryPromoteParkedShuttleToLeaveWhenReady();
        this.LogRoyaltyDebug(
            "Forced immediate imperial evacuation for " + living.Count + " survivor(s).");
    }

    private void SchedulePlanetkiller(int durationTicks)
    {
        GameConditionDef planetKillerDef = DefDatabase<GameConditionDef>.GetNamedSilentFail("Planetkiller");
        if (planetKillerDef == null)
        {
            Log.Warning("[CryoRegenesis] Planetkiller GameConditionDef not found.");
            return;
        }

        // Avoid stacking multiple Planetkillers if one is already running.
        if (Find.World?.GameConditionManager != null
            && Find.World.GameConditionManager.ConditionIsActive(planetKillerDef))
        {
            this.LogRoyaltyDebug("Planetkiller already active — not re-registering.");
            return;
        }

        GameCondition planetKillerCondition = GameConditionMaker.MakeCondition(
            planetKillerDef,
            durationTicks);
        Find.World.GameConditionManager.RegisterCondition(planetKillerCondition);
        this.LogRoyaltyDebug("Planetkiller registered for " + durationTicks + " ticks.");

        Find.LetterStack.ReceiveLetter(
            "Planetkiller armed",
            "Imperial retaliation has armed a Planetkiller. This world will be destroyed in "
            + (durationTicks / (float)GenDate.TicksPerDay).ToString("0.#")
            + " day(s). Evacuate if you can.",
            LetterDefOf.ThreatBig);
    }

    /// Branch 3: free colonists left on the Emperor pickup *with* the Emperor → Imperial Court.
    private void TryTriggerEmperorColonistEndgameFromSnapshot(string reason)
    {
        if (this.emperorColonistEndgameTriggered)
        {
            return;
        }

        // Only the Emperor-stage contract may fire this ending.
        if (this.activeContractStage != RoyaltyRegenesisStage.EmperorArrival)
        {
            return;
        }

        if (this.emperorShuttleColonistEscapeeLabels == null
            || !this.emperorShuttleColonistEscapeeLabels.Any())
        {
            return;
        }

        this.TriggerEmperorColonistEndgame(this.emperorShuttleColonistEscapeeLabels, reason);
    }

    /// Victory ending: one or more free colonists left on the Emperor's regen pickup shuttle
    /// while the Emperor himself also departed.
    private void TriggerEmperorColonistEndgame(List<string> escapeeLabels, string reason)
    {
        if (this.emperorColonistEndgameTriggered || escapeeLabels == null || !escapeeLabels.Any())
        {
            return;
        }

        if (ShipCountdown.CountingDown)
        {
            return;
        }

        this.emperorColonistEndgameTriggered = true;
        this.LogRoyaltyDebug(
            "Imperial Court endgame: " + escapeeLabels.Count
            + " colonist(s) left with the Emperor (" + reason + ").");

        StringBuilder escapees = new StringBuilder();
        foreach (string label in escapeeLabels)
        {
            if (!label.NullOrEmpty())
            {
                escapees.AppendLine("   " + label);
            }
        }

        if (Find.StoryWatcher?.statsRecord != null)
        {
            Find.StoryWatcher.statsRecord.colonistsLaunched += escapeeLabels.Count;
        }

        string intro =
            "You've departed with the Emperor on the Imperial shuttle!";
        StringBuilder ending = new StringBuilder();
        ending.Append(
            "The rejuvenated Emperor welcomes your colonists into the Imperial court as honored envoys of the throne.\n\n");

        string honoree = this.ResolveCourtHonoreeLabel();
        if (!honoree.NullOrEmpty())
        {
            ending.Append(
                "For the gift of CryoRegenesis, the most honored of your number is raised above the rest: "
                + honoree + " is granted the special title of Special Consul, and with it Imperial "
                + "leadership over any rimworld they ever set foot on hereafter.\n\n");
        }

        ending.Append(
            "You may remain among the Imperial flotilla, claim titles and privileges earned by your gift of CryoRegenesis, "
            + "or purchase a ship and set a course for home.\n\n");
        ending.Append("The choice is yours.");

        string credits = GameVictoryUtility.MakeEndCredits(intro, ending.ToString(), escapees.ToString());
        ShipCountdown.InitiateCountdown(credits);

        // Completing the Emperor visit this way also finishes the Imperial Rejuvenation chain.
        if (this.stage != RoyaltyRegenesisStage.Completed)
        {
            this.stage = RoyaltyRegenesisStage.Completed;
            this.royalAscentTriggered = true;
            this.CompleteChainQuest();
        }
    }

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

            // Escorts share the clients' home faction for this contract.
            if (homeFaction != null)
            {
                pawns.AddRange(
                    this.contractEscorts.Where(p =>
                        p != null && !p.Destroyed && p.Faction == homeFaction));
            }

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
        // Do not clear emperorShuttleColonistEscapeeLabels here — FinishContractAfterDeparture
        // may still need them after HandleContractShuttleDeparture already nulls the ship ref.
    }

    private void ClearEmperorShuttleColonistSnapshot()
    {
        this.emperorShuttleColonistEscapeeLabels?.Clear();
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
                    string clientLabel = RoyaltyRegenesisQuestFactory.FormatContractClientName(p, client.role);
                    Find.LetterStack.ReceiveLetter(
                        "Regenesis client ready",
                        clientLabel + " has reached the contracted target age of "
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
                // The restored Stellarch reports back the moment the shuttle departs — no waiting
                // period before word reaches the Emperor. Only the interstellar travel itself takes time.
                int emperorTravelYears = Rand.RangeInclusive(1, 10);
                this.SendTravelNotice(
                    "The Emperor is coming",
                    $"The restored Stellarch's report has reached the Emperor. The imperial household has committed to the journey, but interstellar travel will take {emperorTravelYears} year(s).");
                this.stage = RoyaltyRegenesisStage.EmperorArrival;
                this.nextEventTick = Find.TickManager.TicksGame + emperorTravelYears * GenDate.TicksPerYear;
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
            if (this.IsEmperorRegenContractActive())
            {
                sb.AppendLine(
                    "Emperor pickup: any free colonist may board. One colonist leaving with him ends the game "
                    + "as guests of the Imperial court.");
            }
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

            sb.AppendLine(
                "• " + RoyaltyRegenesisQuestFactory.FormatContractClientName(pawn, client.role)
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
            returnByTick);

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
            "The Emperor has been restored to age 30. Royal Ascent endgame protocols are now being activated.",
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
