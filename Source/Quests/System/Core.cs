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
    private const int DebugCampaignWaitDays = 5;
    /// Vanilla hospitality pickup grace period (Script_Hospitality_Worker shuttleLeaveDelayTicks = 3*60000).
    /// Used as a short buffer after the real deadline, not as the early-ready stay time.
    private const int ShuttleLeaveDelayDays = 3;

    /// Delay before a follow-up pickup after a partial return (0 = next ship as soon as the pad is free).
    private const int WavePickupDelayDays = 0;

    /// Minimum how long a "someone is ready" pickup remains parked for manual loading.
    private const int ReadyPickupMinStayDays = 15;
    private const int InitialUraniumRequirement = 500;

    /// Campaign pacing is shortened to a fixed five-day wait while debug mode is enabled.
    private int RandomizedCampaignDays(int minimumDays, int maximumDays)
    {
        return CryoRegenesis.Settings?.debugMode == true
            ? DebugCampaignWaitDays
            : Rand.RangeInclusive(minimumDays, maximumDays);
    }

    private int CampaignWaitTicks(int minimumDays, int maximumDays)
    {
        return this.RandomizedCampaignDays(minimumDays, maximumDays) * GenDate.TicksPerDay;
    }

    private int EmperorTravelTicks(out string durationText)
    {
        if (CryoRegenesis.Settings?.debugMode == true)
        {
            durationText = "five days";
            return DebugCampaignWaitDays * GenDate.TicksPerDay;
        }

        int years = Rand.RangeInclusive(1, 10);
        durationText = years + " year(s)";
        return years * GenDate.TicksPerYear;
    }

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

    /// Active contract clients found in this pickup shuttle's transporter at launch.
    /// Kept through container destruction so departure accounting can identify only
    /// passengers that actually boarded this shuttle.
    private List<int> pickupLaunchClientPawnIds = new List<int>();

    /// Labels of free colony colonists last seen aboard the Emperor-stage pickup shuttle.
    /// Snapshotted every tick while the ship is parked so departure (which destroys
    /// container contents) can still fire the Imperial Court endgame.
    /// During assassination evacuation this list also includes colony pets for credits.
    private List<string> emperorShuttleColonistEscapeeLabels = new List<string>();

    /// Once the Imperial shuttle is launching, free-colonist labels must not be
    /// recomputed from the transporter. Stargate handoff empties that container
    /// before the ship leaves the map, and a later tick would wipe the endgame list.
    private bool emperorEscapeeSnapshotSealed;

    /// Humanlike passengers recorded for the assassination shuttle launch statistic.
    /// Pets stay on the label list for credits but must not inflate colonistsLaunched.
    private int emperorAssassinationHumanEscapeeCount;

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

    /// Killer recorded from <see cref="Pawn.Kill"/> while DamageInfo is still available.
    private Pawn pendingEmperorKiller;

    /// Knight-or-higher colonist who assassinated the Emperor under "Keep What You Kill".
    private Pawn emperorAssassin;
    private string emperorAssassinLabel = string.Empty;

    /// True while the assassination evacuation is open: Planetkiller is armed and the
    /// Imperial shuttle will carry the assassin, free colonists, and colony pets.
    private bool emperorAssassinationEvacuationActive;

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

        // Assassination evacuation: free colonists may board freely (assassin must leave before
        // the Planetkiller hits; companions and pets are optional).
        if (system.IsEmperorAssassinationEvacuationActive()
            && system.IsFreeColonyColonistForEmperorEndgame(pawn))
        {
            return true;
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
            "Client target recorded directly from casket: "
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
        Scribe_Collections.Look(ref this.pickupLaunchClientPawnIds, "crRoyalPickupLaunchClientPawnIds", LookMode.Value);
        Scribe_Collections.Look(ref this.emperorShuttleColonistEscapeeLabels, "crRoyalEmperorShuttleColonistEscapees", LookMode.Value);
        Scribe_Values.Look(ref this.emperorEscapeeSnapshotSealed, "crRoyalEmperorEscapeeSnapshotSealed", false);
        Scribe_Values.Look(ref this.emperorAssassinationHumanEscapeeCount, "crRoyalEmperorAssassinationHumanEscapees", 0);
        Scribe_Values.Look(ref this.emperorColonistEndgameTriggered, "crRoyalEmperorColonistEndgameTriggered", false);
        Scribe_Values.Look(ref this.emperorNobleRequirementAnnounced, "crRoyalEmperorNobleRequirementAnnounced", false);
        Scribe_References.Look(ref this.emperor, "crRoyalEmperor");
        Scribe_References.Look(ref this.emperorUsurpationCountCandidate, "crRoyalEmperorUsurpationCount");
        Scribe_Values.Look(ref this.emperorUsurpationCountLabel, "crRoyalEmperorUsurpationCountLabel");
        Scribe_References.Look(ref this.emperorCourtHonoree, "crRoyalEmperorCourtHonoree");
        Scribe_Values.Look(ref this.emperorCourtHonoreeLabel, "crRoyalEmperorCourtHonoreeLabel");
        Scribe_References.Look(ref this.emperorAssassin, "crRoyalEmperorAssassin");
        Scribe_Values.Look(ref this.emperorAssassinLabel, "crRoyalEmperorAssassinLabel");
        Scribe_Values.Look(ref this.emperorAssassinationEvacuationActive, "crRoyalEmperorAssassinationEvac", false);
        Scribe_References.Look(ref this.pendingEmperorKiller, "crRoyalPendingEmperorKiller");
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

            if (this.pickupLaunchClientPawnIds == null)
            {
                this.pickupLaunchClientPawnIds = new List<int>();
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

            if (this.emperorAssassinLabel == null)
            {
                this.emperorAssassinLabel = string.Empty;
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
        if (this.pickupShuttleSpawned
            && (this.IsEmperorRegenContractActive() || this.emperorAssassinationEvacuationActive))
        {
            this.RefreshEmperorShuttleColonistSnapshot();
            if (this.IsEmperorRegenContractActive())
            {
                this.LogImperialShuttleBoardingChanges();
            }
        }

        // Assassination evacuation outlives the contract client list — keep the shuttle
        // open and resolve the ending when it leaves (or when the assassin dies).
        if (this.emperorAssassinationEvacuationActive
            && Find.TickManager.TicksGame % CheckIntervalTicks == 0)
        {
            this.TickAssassinationEvacuation();
        }

        if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
        {
            return;
        }

        // Always process existing clients/contracts even if the last casket
        // has been destroyed or uninstalled. Deadlines, deaths, and cleanup
        // must still run.
        this.CheckActiveClients();

        if (this.activeClients.Any() || this.emperorAssassinationEvacuationActive)
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

        if (this.stage == RoyaltyRegenesisStage.NotStarted && !this.HasInitialCampaignResources())
        {
            return;
        }

        int ticksGame = Find.TickManager.TicksGame;
        if (this.stage == RoyaltyRegenesisStage.NotStarted)
        {
            // Other planetary factions send clients first. The Empire only
            // notices after those contracts succeed.
            this.stage = RoyaltyRegenesisStage.RulerPrisoners;
            this.nextEventTick = ticksGame + this.CampaignWaitTicks(3, 8);
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

    private bool HasInitialCampaignResources()
    {
        return Find.Maps.Any(map =>
            map.IsPlayerHome &&
            map.listerBuildings.AllBuildingsColonistOfClass<Building_CryoRegenesis>().Any() &&
            map.resourceCounter.GetCount(ThingDefOf.Uranium) >= InitialUraniumRequirement);
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
                this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(3, 8);
                break;
            case RoyaltyRegenesisStage.StellarchArrival:
                this.StartStellarchContract(map);
                break;
            case RoyaltyRegenesisStage.EmperorNotice:
                int travelTicks = this.EmperorTravelTicks(out string travelDuration);
                this.SendTravelNotice(
                    "The Emperor is coming",
                    $"The restored Stellarch's report has reached the Emperor. The imperial household has committed to the journey, but interstellar travel will take {travelDuration}.");
                this.stage = RoyaltyRegenesisStage.EmperorArrival;
                this.nextEventTick = Find.TickManager.TicksGame + travelTicks;
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


    public bool IsEmperorContractPawn(Pawn pawn)
    {
        return pawn != null && this.emperor != null && pawn == this.emperor;
    }

    public bool IsEmperorAssassinationEvacuationActive()
    {
        return this.emperorAssassinationEvacuationActive;
    }

    public void NotifyEmperorKillInstigator(Pawn killer)
    {
        this.pendingEmperorKiller = killer;
    }
}
