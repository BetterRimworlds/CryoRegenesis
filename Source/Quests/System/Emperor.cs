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

/// Emperor-stage boarding rules, colonist endgame, usurpation, and planetkiller.
public partial class RoyaltyRegenesisQuestSystem
{
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
}
