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

/// Honor offers for completed regen contracts and off-contract Empire nobles.
public partial class RoyaltyRegenesisQuestSystem
{
    private int honorBasisSeniority = -1;
    private string honorBasisTitleDefName = string.Empty;
    private string honorBasisTitleLabel = string.Empty;
    private string honorBasisPawnLabel = string.Empty;
    private float honorBasisYears;
    private bool honorOfferedThisContract;

    public bool HasActiveRegenContract
    {
        get
        {
            if (this.activeClients != null && this.activeClients.Any())
            {
                return true;
            }

            switch (this.activeContractStage)
            {
                case RoyaltyRegenesisStage.RulerPrisoners:
                case RoyaltyRegenesisStage.Leaders:
                case RoyaltyRegenesisStage.LowerNobility:
                case RoyaltyRegenesisStage.StellarchArrival:
                case RoyaltyRegenesisStage.EmperorArrival:
                    return true;
                default:
                    return false;
            }
        }
    }

    private void ExposeHonorData()
    {
        Scribe_Values.Look(ref this.honorBasisSeniority, "crRoyalHonorBasisSeniority", -1);
        Scribe_Values.Look(ref this.honorBasisTitleDefName, "crRoyalHonorBasisTitleDefName");
        Scribe_Values.Look(ref this.honorBasisTitleLabel, "crRoyalHonorBasisTitleLabel");
        Scribe_Values.Look(ref this.honorBasisPawnLabel, "crRoyalHonorBasisPawnLabel");
        Scribe_Values.Look(ref this.honorBasisYears, "crRoyalHonorBasisYears", 0f);
        Scribe_Values.Look(ref this.honorOfferedThisContract, "crRoyalHonorOfferedThisContract", false);

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            if (this.honorBasisTitleDefName == null)
            {
                this.honorBasisTitleDefName = string.Empty;
            }

            if (this.honorBasisTitleLabel == null)
            {
                this.honorBasisTitleLabel = string.Empty;
            }

            if (this.honorBasisPawnLabel == null)
            {
                this.honorBasisPawnLabel = string.Empty;
            }
        }
    }

    private void ResetHonorBasis()
    {
        this.honorBasisSeniority = -1;
        this.honorBasisTitleDefName = string.Empty;
        this.honorBasisTitleLabel = string.Empty;
        this.honorBasisPawnLabel = string.Empty;
        this.honorBasisYears = 0f;
        this.honorOfferedThisContract = false;
    }

    private void SnapshotClientEmpireTitle(RoyaltyRegenesisClient client)
    {
        if (client?.pawn == null)
        {
            return;
        }

        client.contractStartAgeTicks = client.pawn.ageTracker?.AgeBiologicalTicks ?? 0L;
        RoyalTitleDef title = HonorGrant.EmpireTitleOf(client.pawn);
        if (title == null)
        {
            client.empireTitleDefName = string.Empty;
            client.empireTitleLabel = string.Empty;
            client.empireTitleSeniority = -1;
            return;
        }

        client.empireTitleDefName = title.defName;
        client.empireTitleLabel = title.GetLabelFor(client.pawn).CapitalizeFirst();
        client.empireTitleSeniority = title.seniority;
    }

    private void RecordHonorBasisFromClient(RoyaltyRegenesisClient client)
    {
        if (client == null || !client.everReachedDesiredAge)
        {
            return;
        }

        int amount = this.HonorAmountForClient(client);
        if (amount <= 0)
        {
            return;
        }

        int current = this.ComputeContractHonorAmount();
        if (amount < current)
        {
            return;
        }

        if (amount == current && client.empireTitleSeniority <= this.honorBasisSeniority)
        {
            return;
        }

        this.ApplyHonorBasisFromClient(client);
    }

    private void RecordHonorBasisFromActiveClients()
    {
        if (this.activeClients == null)
        {
            return;
        }

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            this.RecordHonorBasisFromClient(client);
        }
    }

    private void RefreshClientTitleSnapshot(RoyaltyRegenesisClient client)
    {
        if (client?.pawn == null || client.pawn.Destroyed)
        {
            return;
        }

        RoyalTitleDef title = HonorGrant.EmpireTitleOf(client.pawn);
        if (title == null)
        {
            return;
        }

        client.empireTitleDefName = title.defName;
        client.empireTitleLabel = title.GetLabelFor(client.pawn).CapitalizeFirst();
        client.empireTitleSeniority = title.seniority;
    }

    private float YearsRegressedForClient(RoyaltyRegenesisClient client)
    {
        if (client == null)
        {
            return 0f;
        }

        long startTicks = client.contractStartAgeTicks;
        if (startTicks <= 0 && client.pawn != null && !client.pawn.Destroyed)
        {
            TrueAgeTracker tracker = client.pawn.health?.hediffSet?
                .GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
            startTicks = tracker?.contractStartAgeTicks ?? 0L;
        }

        if (startTicks <= 0)
        {
            return 0f;
        }

        long removed = startTicks - client.desiredAgeTicks;
        if (removed <= 0)
        {
            return 0f;
        }

        return (float)removed / GenDate.TicksPerYear;
    }

    private string ClientHonorName(RoyaltyRegenesisClient client)
    {
        if (client?.pawn != null && !client.pawn.Destroyed)
        {
            return client.pawn.Name?.ToStringShort ?? client.pawn.LabelShort;
        }

        return "the client";
    }

    private int HonorAmountForClient(RoyaltyRegenesisClient client)
    {
        if (client == null)
        {
            return 0;
        }

        this.RefreshClientTitleSnapshot(client);
        if (client.empireTitleSeniority < 0)
        {
            return 0;
        }

        return HonorGrant.Calculate(
            client.empireTitleDefName,
            client.empireTitleSeniority,
            this.YearsRegressedForClient(client));
    }

    private void ApplyHonorBasisFromClient(RoyaltyRegenesisClient client)
    {
        this.RefreshClientTitleSnapshot(client);
        this.honorBasisSeniority = client.empireTitleSeniority;
        this.honorBasisTitleDefName = client.empireTitleDefName ?? string.Empty;
        this.honorBasisTitleLabel = client.empireTitleLabel ?? string.Empty;
        this.honorBasisPawnLabel = this.ClientHonorName(client);
        this.honorBasisYears = this.YearsRegressedForClient(client);
    }

    /// Client whose title and years restored yield the most Honor. Does not require
    /// that they have reached the contracted age yet.
    private RoyaltyRegenesisClient FindHighestHonorClient(out int honor)
    {
        honor = 0;
        RoyaltyRegenesisClient best = null;
        int bestSeniority = -1;
        float bestYears = 0f;
        if (this.activeClients == null)
        {
            return null;
        }

        foreach (RoyaltyRegenesisClient client in this.activeClients)
        {
            if (client == null)
            {
                continue;
            }

            int amount = this.HonorAmountForClient(client);
            if (amount <= 0)
            {
                continue;
            }

            float years = this.YearsRegressedForClient(client);
            int seniority = client.empireTitleSeniority;
            if (amount < honor)
            {
                continue;
            }

            if (amount == honor
                && (seniority < bestSeniority
                    || (seniority == bestSeniority && years <= bestYears)))
            {
                continue;
            }

            honor = amount;
            best = client;
            bestSeniority = seniority;
            bestYears = years;
        }

        return best;
    }

    /// Offer Honor as soon as the highest-Honor client on this contract reaches
    /// their target age. Do not wait for the last shuttle.
    private void TryOfferHonorWhenHighestHonorClientReady()
    {
        if (this.honorOfferedThisContract
            || this.activeContractStage == RoyaltyRegenesisStage.EmperorArrival)
        {
            return;
        }

        RoyaltyRegenesisClient best = this.FindHighestHonorClient(out int honor);
        if (best == null || honor <= 0 || !best.everReachedDesiredAge)
        {
            return;
        }

        this.ApplyHonorBasisFromClient(best);
        this.OfferContractHonorIfEligible(
            this.activeContractStage,
            honor,
            this.BuildContractHonorLetterBody(honor));
    }

    private int ComputeContractHonorAmount()
    {
        if (this.honorBasisSeniority < 0)
        {
            return 0;
        }

        return HonorGrant.Calculate(
            this.honorBasisTitleDefName,
            this.honorBasisSeniority,
            this.honorBasisYears);
    }

    private string BuildContractHonorLetterBody(int honorAmount)
    {
        int decades = HonorGrant.RoundYearsToDecades(this.honorBasisYears);
        string noble = this.honorBasisTitleLabel.NullOrEmpty()
            ? this.honorBasisPawnLabel
            : this.honorBasisTitleLabel + " " + this.honorBasisPawnLabel;
        return "The Empire recognizes the CryoRegenesis of " + noble
            + " (" + this.honorBasisYears.ToString("0.#") + " years restored, counted as "
            + decades + " decade" + (decades == 1 ? "" : "s") + ").\n\n"
            + "You may bestow " + honorAmount + " Honor on one of your colonists.";
    }

    private void OfferContractHonorIfEligible(
        RoyaltyRegenesisStage finishedStage,
        int honorAmount,
        string body)
    {
        if (this.honorOfferedThisContract || honorAmount <= 0 || body.NullOrEmpty())
        {
            return;
        }

        if (finishedStage == RoyaltyRegenesisStage.EmperorArrival)
        {
            return;
        }

        if (this.SendHonorLetter(honorAmount, body, "Bestow Honor"))
        {
            this.honorOfferedThisContract = true;
        }
    }

    private void OfferFixedHonor(int honorAmount, string body)
    {
        if (honorAmount <= 0)
        {
            return;
        }

        this.SendHonorLetter(honorAmount, body, "Bestow Honor");
    }

    private bool SendHonorLetter(int honorAmount, string body, string label)
    {
        if (!ModsConfig.RoyaltyActive || honorAmount <= 0 || body.NullOrEmpty())
        {
            return false;
        }

        LetterDef def = CryoRegenesisLetterDefOf.CR_BestowHonor
            ?? DefDatabase<LetterDef>.GetNamedSilentFail("CR_BestowHonor");
        if (def == null)
        {
            Log.Warning("[CryoRegenesis] CR_BestowHonor letter def is missing; granting no Honor choice.");
            return false;
        }

        ChoiceLetter made = LetterMaker.MakeLetter(
            label,
            body,
            def,
            HonorGrant.EmpireFactionOrNull(),
            null);
        ChoiceLetter_BestowHonor letter = made as ChoiceLetter_BestowHonor;
        if (letter == null)
        {
            Log.Warning("[CryoRegenesis] Bestow Honor letter used the wrong letter class.");
            return false;
        }

        letter.honorAmount = honorAmount;
        HonorGrant.ReceiveLetter(letter);
        this.LogRoyaltyDebug("Offered Honor letter for " + honorAmount + " Honor.");
        return true;
    }

    /// Off-contract: a titled Empire noble finished a CryoRegenesis treatment.
    public static void NotifyNobleRegenesisCompleted(Pawn pawn, float yearsRegressed)
    {
        if (pawn == null || yearsRegressed <= 0f || !ModsConfig.RoyaltyActive)
        {
            return;
        }

        RoyaltyRegenesisQuestSystem system = CurrentSystem;
        if (system == null || system.HasActiveRegenContract)
        {
            return;
        }

        if (!HonorGrant.IsEmpireNoble(pawn))
        {
            return;
        }

        int honor = HonorGrant.CalculateForPawn(pawn, yearsRegressed);
        if (honor <= 0)
        {
            return;
        }

        RoyalTitleDef title = HonorGrant.EmpireTitleOf(pawn);
        string name = pawn.Name?.ToStringShort ?? pawn.LabelShort;
        string titleLabel = title != null
            ? title.GetLabelFor(pawn).CapitalizeFirst()
            : "Noble";
        int decades = HonorGrant.RoundYearsToDecades(yearsRegressed);
        string body = "The Empire recognizes the CryoRegenesis of " + titleLabel + " " + name
            + " (" + yearsRegressed.ToString("0.#") + " years restored, counted as "
            + decades + " decade" + (decades == 1 ? "" : "s") + ").\n\n"
            + "You may bestow " + honor + " Honor on one of your colonists.";
        system.SendHonorLetter(honor, body, "Bestow Honor");
    }
}
