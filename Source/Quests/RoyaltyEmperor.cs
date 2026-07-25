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
using System.Text;
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Builds the final Imperial Rejuvenation party: High Stellarch + Emperor,
/// each with 1–4 wives, plus Stellic security guards (no regen contracts).
///
/// The Emperor and wives are not world pawns — they are created with
/// <see cref="SpawnStoryHuman"/>. The High Stellarch is the Empire faction leader
/// when available; otherwise a story-human Stellarch is generated.
public static class RoyaltyEmperor
{
    public const int LeaderTargetAgeYears = 30;
    public const int WifeTargetAgeYears = 20;

    /// Men younger than their regen target are aged to a random age above this.
    public const int MaleAgeFloorYears = 35;

    /// Women younger than their regen target are aged randomly under this (and above the wife target).
    public const int FemaleAgeCeilingYears = 35;

    public const int MinWivesPerNoble = 1;
    public const int MaxWivesPerNoble = 4;

    public const int GuardRangedCount = 2;
    public const int GuardMeleeCount = 2;

    public sealed class RegenMember
    {
        public Pawn pawn;
        public string role;
        public long desiredAgeTicks;
        public bool triggerRoyalAscent;
    }

    public sealed class Party
    {
        public readonly List<RegenMember> regenMembers = new List<RegenMember>();
        public readonly List<Pawn> escorts = new List<Pawn>();

        public Pawn highStellarch;
        public Pawn emperor;
        public int wifeCount;
        public int wifeCountEmperor;
        public int guardCount;

        /// Every pawn that arrives on the delivery shuttle (regen + escorts).
        public List<Pawn> AllArrivalPawns
        {
            get
            {
                List<Pawn> all = new List<Pawn>();
                foreach (RegenMember member in this.regenMembers)
                {
                    if (member?.pawn != null)
                    {
                        all.Add(member.pawn);
                    }
                }

                all.AddRange(this.escorts.Where(p => p != null));
                return all;
            }
        }

        public string BuildLetterBody(string returnDeadlineText)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("The High Stellarch and the Emperor have arrived by imperial shuttle for CryoRegenesis. ");
            sb.Append($"Both will regress to age {LeaderTargetAgeYears}. ");
            if (this.wifeCount > 0)
            {
                sb.Append($"They brought {this.wifeCount} {(this.wifeCount == 1 ? "wife" : "wives")} for CryoRegenesis, too. ");
            }

            if (this.guardCount > 0)
            {
                sb.Append($"A detail of {this.guardCount} Stellic security guards accompanies them (no treatment). ");
            }

            sb.Append($"Treat the contracted guests by {returnDeadlineText}. ");
            sb.Append("A pickup shuttle arrives when treatment is finished. ");
            sb.Append("Any number of your colonists may board that shuttle once the Emperor is alive and aboard it ");
            sb.Append("himself (or sealed in a powered-off CryoRegenesis casket) and every other guest is offworld or ");
            sb.Append("aboard at their target age. ");
            sb.Append("If even one colonist leaves with the Emperor, your story ends in victory as guests of the ");
            sb.Append("Imperial court.");
            return sb.ToString();
        }
    }

    /// Assemble the Emperor-stage party. <paramref name="highStellarch"/> may be the
    /// Empire faction leader (world pawn) or null to generate a story Stellarch.
    public static Party Build(Faction empire, Pawn highStellarch = null)
    {
        Party party = new Party();
        if (empire == null)
        {
            return party;
        }

        // --- High Stellarch ---
        Pawn stellarch = highStellarch;
        if (stellarch == null || stellarch.Dead || stellarch.Destroyed)
        {
            stellarch = SpawnStoryHuman.GenerateImperial(
                empire,
                Gender.Male,
                RandomMaleArrivalAgeYears(),
                royalTitleDefName: "Stellarch",
                kind: NamedKind("Empire_Royal_Stellarch", empire));
        }
        else
        {
            EnsureAgeForRegen(stellarch, LeaderTargetAgeYears);
            SpawnStoryHuman.TrySetRoyalTitle(stellarch, empire, "Stellarch");
        }

        party.highStellarch = stellarch;
        party.regenMembers.Add(new RegenMember
        {
            pawn = stellarch,
            role = "high stellarch",
            desiredAgeTicks = LeaderTargetAgeYears * (long)GenDate.TicksPerYear,
            triggerRoyalAscent = false,
        });

        // --- Emperor (always generated; vanilla never has one on the world) ---
        Pawn emperor = SpawnStoryHuman.GenerateImperial(
            empire,
            Gender.Male,
            RandomMaleArrivalAgeYears(),
            royalTitleDefName: "Emperor",
            kind: NamedKind("Empire_Royal_Stellarch", empire));
        SpawnStoryHuman.TrySetRoyalTitle(emperor, empire, "Emperor");

        party.emperor = emperor;
        party.regenMembers.Add(new RegenMember
        {
            pawn = emperor,
            role = "emperor",
            desiredAgeTicks = LeaderTargetAgeYears * (long)GenDate.TicksPerYear,
            triggerRoyalAscent = true,
        });

        // --- Wives (1–4 each for Stellarch and Emperor) ---
        party.wifeCountEmperor = AddWives(party, stellarch, empire, "imperial wife");
        party.wifeCount += party.wifeCountEmperor;
        party.wifeCount += AddWives(party, emperor, empire, "stellarch wife");

        // --- Security guards (no regen contracts) ---
        AddGuards(party, empire);

        return party;
    }

    private static int AddWives(Party party, Pawn husband, Faction empire, string role)
    {
        if (husband == null)
        {
            return 0;
        }

        int desired = Rand.RangeInclusive(MinWivesPerNoble, MaxWivesPerNoble);
        int created = 0;

        for (int i = 0; i < desired; i++)
        {
            Pawn wife = SpawnStoryHuman.GenerateImperial(
                empire,
                Gender.Female,
                RandomFemaleArrivalAgeYears(),
                royalTitleDefName: null,
                kind: NamedKind("Empire_Royal_NobleWimp", empire));

            EnsureAgeForRegen(wife, WifeTargetAgeYears);
            SpawnStoryHuman.Marry(husband, wife);

            party.regenMembers.Add(new RegenMember
            {
                pawn = wife,
                role = role,
                desiredAgeTicks = WifeTargetAgeYears * (long)GenDate.TicksPerYear,
                triggerRoyalAscent = false,
            });
            created++;
        }

        return created;
    }

    private static void AddGuards(Party party, Faction empire)
    {
        PawnKindDef ranged = NamedKind("Empire_Fighter_StellicGuardRanged", empire)
            ?? NamedKind("Empire_Fighter_Janissary", empire)
            ?? NamedKind("Empire_Fighter_Trooper", empire);

        PawnKindDef melee = NamedKind("Empire_Fighter_StellicGuardMelee", empire)
            ?? NamedKind("Empire_Fighter_Champion", empire)
            ?? ranged;

        for (int i = 0; i < GuardRangedCount; i++)
        {
            AddGuard(party, empire, ranged);
        }

        for (int i = 0; i < GuardMeleeCount; i++)
        {
            AddGuard(party, empire, melee);
        }

        party.guardCount = party.escorts.Count;
    }

    private static void AddGuard(Party party, Faction empire, PawnKindDef kind)
    {
        if (kind == null)
        {
            return;
        }

        // Adult combat age; no regen contract.
        float age = Rand.RangeInclusive(22, 45);
        Pawn guard = SpawnStoryHuman.Generate(
            kind,
            empire,
            gender: null,
            biologicalAgeYears: age,
            options: new SpawnStoryHuman.Options
            {
                Faction = empire,
                HostFaction = Faction.OfPlayer,
                StoryGuestStatus = SpawnStoryHuman.StoryGuestStatus.Guest,
                CanGeneratePawnRelations = false,
            });

        if (guard != null)
        {
            party.escorts.Add(guard);
        }
    }

    /// If the pawn is younger than the contracted target age, push them into a
    /// sensible arrival age so there is something to regress.
    /// Men: random ticks above age 35. Women: random age under 35 (above target).
    public static void EnsureAgeForRegen(Pawn pawn, int targetAgeYears)
    {
        if (pawn?.ageTracker == null)
        {
            return;
        }

        if (pawn.ageTracker.AgeBiologicalYears >= targetAgeYears + 1)
        {
            return;
        }

        long newTicks;
        if (pawn.gender == Gender.Male)
        {
            // Strictly above MaleAgeFloorYears (35).
            int years = Rand.RangeInclusive(MaleAgeFloorYears + 1, 90);
            long partial = Rand.Range(0, GenDate.TicksPerYear);
            newTicks = years * (long)GenDate.TicksPerYear + partial;
        }
        else
        {
            // Under 35, but old enough that regressing to the wife target is meaningful.
            int minYears = Math.Max(targetAgeYears + 1, 21);
            int maxYears = FemaleAgeCeilingYears - 1; // 34
            if (maxYears < minYears)
            {
                maxYears = minYears;
            }

            int years = Rand.RangeInclusive(minYears, maxYears);
            long partial = Rand.Range(0, GenDate.TicksPerYear);
            newTicks = years * (long)GenDate.TicksPerYear + partial;
        }

        pawn.ageTracker.AgeBiologicalTicks = newTicks;
    }

    private static float RandomMaleArrivalAgeYears()
    {
        // Always above 35 so leaders have headroom down to age 30.
        return Rand.RangeInclusive(MaleAgeFloorYears + 1, 90) + Rand.Value;
    }

    private static float RandomFemaleArrivalAgeYears()
    {
        // Under 35, above the wife target of 20.
        return Rand.RangeInclusive(WifeTargetAgeYears + 1, FemaleAgeCeilingYears - 1) + Rand.Value;
    }

    private static PawnKindDef NamedKind(string defName, Faction faction)
    {
        PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(defName);
        if (kind != null)
        {
            return kind;
        }

        return faction?.def?.basicMemberKind;
    }
}
