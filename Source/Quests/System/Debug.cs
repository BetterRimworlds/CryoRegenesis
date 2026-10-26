/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Debug-only helpers of <see cref="RoyaltyRegenesisQuestSystem"/>,
/// exposed through <see cref="RoyaltyRegenesisDebugActions"/>.
public partial class RoyaltyRegenesisQuestSystem
{
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
        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            TrueAgeTracker tracker = client?.pawn?.health?.hediffSet?
                .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
            if (tracker != null)
            {
                tracker.underRegenContract = false;
                tracker.desiredAgeTicks = 0;
            }
        }

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

    public void DebugOfferHonorLetter()
    {
        this.SendHonorLetter(
            50,
            "Debug: choose a colonist to receive 50 Honor.",
            "Bestow Honor");
    }
}
