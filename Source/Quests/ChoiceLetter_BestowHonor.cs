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

[DefOf]
public static class CryoRegenesisLetterDefOf
{
    public static LetterDef CR_BestowHonor;

    static CryoRegenesisLetterDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(CryoRegenesisLetterDefOf));
    }
}

/// Choice letter: pick a colonist to receive Imperial Honor for CryoRegenesis.
public class ChoiceLetter_BestowHonor : ChoiceLetter
{
    public int honorAmount;

    public override bool CanDismissWithRightClick => false;

#if RIMWORLD14 || RIMWORLD15 || RIMWORLD16 || RIMWORLD17
    public override bool ShouldAutomaticallyOpenLetter => true;
#endif

    public override IEnumerable<DiaOption> Choices
    {
        get
        {
            if (this.honorAmount <= 0)
            {
                yield return this.Option_Close;
                yield break;
            }

            List<Pawn> recipients = HonorGrant.HonorRecipients()
                .Where(HonorGrant.IsEligibleHonorRecipient)
                .OrderByDescending(HonorGrant.EmpireSeniorityOf)
                .ThenBy(p => p.LabelShort)
                .ToList();

            if (!recipients.Any())
            {
                DiaOption none = new DiaOption("No colonists can receive Honor");
                none.Disable("No living free colonist with a Royalty tracker is available.");
                yield return none;
            }
            else
            {
                foreach (Pawn pawn in recipients)
                {
                    yield return this.Option_Bestow(pawn);
                }
            }

            yield return this.Option_Postpone;
            yield return this.Option_Decline();
        }
    }

    private DiaOption Option_Bestow(Pawn pawn)
    {
        Faction empire = HonorGrant.EmpireFactionOrNull();
        string name = pawn.Name?.ToStringShort ?? pawn.LabelShort;
        string extra = string.Empty;
        if (pawn.ageTracker != null)
        {
            extra = pawn.ageTracker.AgeBiologicalYears.ToString();
        }

        if (pawn.royalty != null && empire != null)
        {
            RoyalTitleDef title = pawn.royalty.GetCurrentTitle(empire);
            int current = pawn.royalty.GetFavor(empire);
            string honorPart = title != null
                ? title.GetLabelFor(pawn) + ", " + current + " Honor"
                : current + " Honor";
            extra = extra.NullOrEmpty() ? honorPart : extra + ", " + honorPart;
        }

        if (!extra.NullOrEmpty())
        {
            extra = " (" + extra + ")";
        }

        DiaOption option = new DiaOption("Bestow " + this.honorAmount + " Honor on " + name + extra);
        option.action = delegate
        {
            HonorGrant.Bestow(pawn, this.honorAmount);
            Find.LetterStack.RemoveLetter(this);
        };
        option.resolveTree = true;
        return option;
    }

    private DiaOption Option_Decline()
    {
        DiaOption option = new DiaOption("Do not bestow Honor");
        option.action = delegate
        {
            Find.LetterStack.RemoveLetter(this);
        };
        option.resolveTree = true;
        return option;
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref this.honorAmount, "crHonorAmount", 0);
    }
}
