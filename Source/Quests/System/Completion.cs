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

/// Age-target evaluation, stage advancement, progress text, and trust-break handling.
public partial class RoyaltyRegenesisQuestSystem
{
    /// Recompute age records for every living client and update <see cref="pickupAllClientsReady"/>.
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
            this.LogRoyaltyDebug("All clients have recorded everReachedDesiredAge → banking departure success.");
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

    /// Current biological age is at or below the contracted target, with a short
    /// periodic-check allowance only when the casket has recorded sufficient treatment.
    ///
    /// The casket ejects a client the same tick it clamps them at the exact target, and
    /// they age +1 tick per tick from then on — so an exact comparison can only succeed
    /// on that single tick. The casket records that instant via
    /// <see cref="NotifyRegenesisTargetReached"/>, but if it is ever missed (pawn already
    /// ejected on load, older build, power loss on the boundary tick) an exact check can
    /// never record again and the contract is unwinnable. The fallback permits only one
    /// check interval of natural aging and requires the casket to have removed at least
    /// the age needed to reach the target during this contract. Contract time by itself
    /// is never evidence that treatment occurred.
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

        currentTicks = client.pawn.ageTracker.AgeBiologicalTicks;
        allowedTicks = client.desiredAgeTicks + CheckIntervalTicks;
        if (currentTicks <= client.desiredAgeTicks)
        {
            return true;
        }

        if (currentTicks > allowedTicks)
        {
            return false;
        }

        TrueAgeTracker tracker = client.pawn.health?.hediffSet?
            .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
        if (tracker == null || client.contractStartRemovedAgeTicks < 0
            || tracker.contractStartAgeTicks <= client.desiredAgeTicks)
        {
            return false;
        }

        long requiredAgeRemoved = tracker.contractStartAgeTicks - client.desiredAgeTicks;
        long ageRemovedDuringContract = tracker.cryoRegenesisRemovedAgeTicks
            - client.contractStartRemovedAgeTicks;
        return ageRemovedDuringContract >= requiredAgeRemoved;
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
                    this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(15, 35);
                }
                else
                {
                    this.stage = RoyaltyRegenesisStage.Leaders;
                    this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(25, 45);
                }
                break;
            case RoyaltyRegenesisStage.Leaders:
                this.leaderContractsCompleted++;
                if (this.leaderContractsCompleted < 2)
                {
                    this.stage = RoyaltyRegenesisStage.Leaders;
                    this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(30, 60);
                }
                else
                {
                    // Only after other factions succeed does the Empire take interest.
                    this.stage = RoyaltyRegenesisStage.LowerNobility;
                    this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(30, 60);
                }
                break;
            case RoyaltyRegenesisStage.LowerNobility:
                this.nobleContractsCompleted++;
                if (this.nobleContractsCompleted < 2)
                {
                    this.stage = RoyaltyRegenesisStage.LowerNobility;
                    this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(30, 60);
                }
                else
                {
                    this.stage = RoyaltyRegenesisStage.StellarchNotice;
                    this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(15, 30);
                }
                break;
            case RoyaltyRegenesisStage.StellarchArrival:
                // The restored Stellarch reports back the moment the shuttle departs — no waiting
                // period before word reaches the Emperor. Only the interstellar travel itself takes time.
                int emperorTravelTicks = this.EmperorTravelTicks(out string emperorTravelDuration);
                this.SendTravelNotice(
                    "The Emperor is coming",
                    $"The restored Stellarch's report has reached the Emperor. The imperial household has committed to the journey, but interstellar travel will take {emperorTravelDuration}.");
                this.stage = RoyaltyRegenesisStage.EmperorArrival;
                this.nextEventTick = Find.TickManager.TicksGame + emperorTravelTicks;
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
        this.nextEventTick = Find.TickManager.TicksGame + this.CampaignWaitTicks(30, 60);
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
            + this.CampaignWaitTicks(MajorResetMinDays, MajorResetMaxDays);

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
}
