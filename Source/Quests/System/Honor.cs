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

        this.RefreshClientTitleSnapshot(client);

        int seniority = client.empireTitleSeniority;
        if (seniority < 0)
        {
            return;
        }

        float years = this.YearsRegressedForClient(client);
        if (seniority > this.honorBasisSeniority)
        {
            this.honorBasisSeniority = seniority;
            this.honorBasisTitleDefName = client.empireTitleDefName ?? string.Empty;
            this.honorBasisTitleLabel = client.empireTitleLabel ?? string.Empty;
            this.honorBasisPawnLabel = this.ClientHonorName(client);
            this.honorBasisYears = years;
        }
        else if (seniority == this.honorBasisSeniority && years > this.honorBasisYears)
        {
            this.honorBasisYears = years;
            this.honorBasisPawnLabel = this.ClientHonorName(client);
        }
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
        if (honorAmount <= 0 || body.NullOrEmpty())
        {
            return;
        }

        if (finishedStage == RoyaltyRegenesisStage.EmperorArrival)
        {
            return;
        }

        this.SendHonorLetter(honorAmount, body, "Bestow Honor");
    }

    private void OfferFixedHonor(int honorAmount, string body)
    {
        if (honorAmount <= 0)
        {
            return;
        }

        this.SendHonorLetter(honorAmount, body, "Bestow Honor");
    }

    private void SendHonorLetter(int honorAmount, string body, string label)
    {
        if (!ModsConfig.RoyaltyActive || honorAmount <= 0 || body.NullOrEmpty())
        {
            return;
        }

        LetterDef def = CryoRegenesisLetterDefOf.CR_BestowHonor
            ?? DefDatabase<LetterDef>.GetNamedSilentFail("CR_BestowHonor");
        if (def == null)
        {
            Log.Warning("[CryoRegenesis] CR_BestowHonor letter def is missing; granting no Honor choice.");
            return;
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
            return;
        }

        letter.honorAmount = honorAmount;
        HonorGrant.ReceiveLetter(letter);
        this.LogRoyaltyDebug("Offered Honor letter for " + honorAmount + " Honor.");
    }

    public void DebugOfferHonorLetter()
    {
        this.SendHonorLetter(
            50,
            "Debug: choose a colonist to receive 50 Honor.",
            "Bestow Honor");
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
