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

/// Spouses, fiancés, and lovers who accompany royalty-chain clients
/// (planetary rulers, imperial nobles, Stellarch, Emperor) on CryoRegenesis
/// contracts. Husbands regress to age 45; wives and girlfriends to age 20.
public static class RoyaltyRegenesisQuestPartners
{
    public const int HusbandTargetAgeYears = 45;
    public const int WifeOrGirlfriendTargetAgeYears = 20;

    /// Target biological age for a companion: 45 for males (husbands), 20 for
    /// females (wives / girlfriends). Non-male defaults to the wife/girlfriend age.
    public static int TargetAgeYearsFor(Pawn partner)
    {
        if (partner != null && partner.gender == Gender.Male)
        {
            return HusbandTargetAgeYears;
        }

        return WifeOrGirlfriendTargetAgeYears;
    }

    public static bool IsRomanticPartnerRelation(PawnRelationDef def)
    {
        return def == PawnRelationDefOf.Spouse
            || def == PawnRelationDefOf.Lover
            || def == PawnRelationDefOf.Fiance;
    }

    /// Former romantic partners — still DirectRelations, but never allowed as
    /// boarding companions on regen pickup shuttles.
    public static bool IsExRelation(PawnRelationDef def)
    {
        return def == PawnRelationDefOf.ExSpouse
            || def == PawnRelationDefOf.ExLover;
    }

    /// True when <paramref name="pawn"/> has any non-ex DirectRelation to at least
    /// one of <paramref name="others"/> (spouse, parent, child, sibling, lover, …).
    public static bool IsNonExDirectRelationOfAny(Pawn pawn, IEnumerable<Pawn> others)
    {
        if (pawn?.relations?.DirectRelations == null || others == null)
        {
            return false;
        }

        HashSet<Pawn> targets = new HashSet<Pawn>();
        foreach (Pawn other in others)
        {
            if (other != null && !other.Destroyed)
            {
                targets.Add(other);
            }
        }

        if (targets.Count == 0)
        {
            return false;
        }

        foreach (DirectPawnRelation relation in pawn.relations.DirectRelations)
        {
            if (relation?.otherPawn == null || IsExRelation(relation.def))
            {
                continue;
            }

            if (targets.Contains(relation.otherPawn))
            {
                return true;
            }
        }

        return false;
    }

    /// Short arrival-letter blurb when companions are present.
    public static string CompanionArrivalText(int companionCount)
    {
        if (companionCount <= 0)
        {
            return string.Empty;
        }

        return " They brought " + companionCount + " companion(s); husbands regress to age "
            + HusbandTargetAgeYears
            + " and wives or girlfriends to age "
            + WifeOrGirlfriendTargetAgeYears + ".";
    }

    /// Eligible romantic partners of <paramref name="primary"/> who can join the contract.
    /// <paramref name="isEligible"/> receives the partner and the minimum biological age
    /// they must already be (one year above their target) so there is something to regress.
    /// <paramref name="alreadyInParty"/> skips pawns already booked on this shuttle.
    public static List<PartnerArrival> Collect(
        Pawn primary,
        Func<Pawn, int, bool> isEligible,
        ICollection<Pawn> alreadyInParty = null)
    {
        List<PartnerArrival> results = new List<PartnerArrival>();
        if (primary?.relations?.DirectRelations == null || isEligible == null)
        {
            return results;
        }

        HashSet<Pawn> seen = new HashSet<Pawn>();
        foreach (DirectPawnRelation relation in primary.relations.DirectRelations)
        {
            if (relation == null || !IsRomanticPartnerRelation(relation.def))
            {
                continue;
            }

            Pawn partner = relation.otherPawn;
            if (partner == null || !seen.Add(partner))
            {
                continue;
            }

            if (alreadyInParty != null && alreadyInParty.Contains(partner))
            {
                continue;
            }

            int targetAgeYears = TargetAgeYearsFor(partner);
            if (!isEligible(partner, targetAgeYears + 1))
            {
                continue;
            }

            results.Add(new PartnerArrival(partner, targetAgeYears));
        }

        return results;
    }

    /// Partners for every primary in the party, de-duplicated across the whole group.
    public static List<PartnerArrival> CollectForParty(
        IEnumerable<Pawn> primaries,
        Func<Pawn, int, bool> isEligible,
        ICollection<Pawn> alreadyInParty = null)
    {
        List<PartnerArrival> results = new List<PartnerArrival>();
        if (primaries == null)
        {
            return results;
        }

        HashSet<Pawn> party = alreadyInParty != null
            ? new HashSet<Pawn>(alreadyInParty)
            : new HashSet<Pawn>();

        foreach (Pawn primary in primaries)
        {
            if (primary == null)
            {
                continue;
            }

            party.Add(primary);
            foreach (PartnerArrival partner in Collect(primary, isEligible, party))
            {
                results.Add(partner);
                party.Add(partner.pawn);
            }
        }

        return results;
    }

    public sealed class PartnerArrival
    {
        public PartnerArrival(Pawn pawn, int targetAgeYears)
        {
            this.pawn = pawn;
            this.targetAgeYears = targetAgeYears;
        }

        public readonly Pawn pawn;
        public readonly int targetAgeYears;

        public long TargetAgeTicks => this.targetAgeYears * (long)GenDate.TicksPerYear;
    }
}
