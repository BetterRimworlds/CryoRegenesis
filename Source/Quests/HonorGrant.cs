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

/// Honor (Imperial favor) granted for CryoRegenesis of Empire nobles.
/// Years removed become decades (years / 10, rounded; 5.0 years counts as one
/// decade), then multiplied by the treated noble's title: Freeholder 1×, Novice 2×,
/// Acolyte 2.5×, Knight 3×, Praetor 5×, Baron 7×, Count 8×, and 10× for every
/// higher rank.
public static class HonorGrant
{
    public const int EmperorCourtHonor = 1000;
    public const int ConsulDefaultHonor = 100;

    public static Faction EmpireFactionOrNull()
    {
        FactionDef empireDef = DefDatabase<FactionDef>.GetNamedSilentFail("Empire");
        if (empireDef == null || Find.FactionManager == null)
        {
            return null;
        }

        return Find.FactionManager.FirstFactionOfDef(empireDef);
    }

    public static RoyalTitleDef EmpireTitleOf(Pawn pawn)
    {
        if (pawn?.royalty == null)
        {
            return null;
        }

        Faction empire = EmpireFactionOrNull();
        if (empire == null)
        {
            return null;
        }

        return pawn.royalty.GetCurrentTitle(empire);
    }

    public static bool IsEmpireNoble(Pawn pawn)
    {
        return EmpireTitleOf(pawn) != null;
    }

    /// Years / 10, rounded to the nearest decade. 5.0 years counts as one decade.
    public static int RoundYearsToDecades(float yearsRegressed)
    {
        if (yearsRegressed <= 0f)
        {
            return 0;
        }

        return (int)Math.Round(yearsRegressed / 10.0, MidpointRounding.AwayFromZero);
    }

    public static float TitleMultiplier(string titleDefName, int seniority)
    {
        if (!titleDefName.NullOrEmpty())
        {
            switch (titleDefName)
            {
                case "Freeholder":
                    return 1f;
                case "Yeoman":
                    return 2f;
                case "Acolyte":
                    return 2.5f;
                case "Knight":
                    return 3f;
                case "Praetor":
                    return 5f;
                case "Baron":
                    return 7f;
                case "Count":
                    return 8f;
                default:
                    return 10f;
            }
        }

        if (seniority < 0)
        {
            return 0f;
        }

        if (seniority < 100)
        {
            return 1f;
        }

        if (seniority < 200)
        {
            return 2f;
        }

        if (seniority < 300)
        {
            return 2.5f;
        }

        if (seniority < 400)
        {
            return 3f;
        }

        if (seniority < 500)
        {
            return 5f;
        }

        if (seniority < 600)
        {
            return 7f;
        }

        if (seniority < 700)
        {
            return 8f;
        }

        return 10f;
    }

    public static int Calculate(string titleDefName, int seniority, float yearsRegressed)
    {
        int decades = RoundYearsToDecades(yearsRegressed);
        if (decades <= 0)
        {
            return 0;
        }

        float multiplier = TitleMultiplier(titleDefName, seniority);
        if (multiplier <= 0f)
        {
            return 0;
        }

        return Math.Max(0, (int)Math.Round(decades * multiplier, MidpointRounding.AwayFromZero));
    }

    public static int CalculateForPawn(Pawn pawn, float yearsRegressed)
    {
        RoyalTitleDef title = EmpireTitleOf(pawn);
        if (title == null)
        {
            return 0;
        }

        return Calculate(title.defName, title.seniority, yearsRegressed);
    }

    public static void ReceiveLetter(Letter letter)
    {
        if (letter == null)
        {
            return;
        }

        Find.LetterStack.ReceiveLetter(letter, null);
    }

    public static IEnumerable<Pawn> HonorRecipients()
    {
        if (Find.Maps == null)
        {
            yield break;
        }

        foreach (Map map in Find.Maps)
        {
            if (map?.mapPawns?.FreeColonists == null)
            {
                continue;
            }

            foreach (Pawn pawn in map.mapPawns.FreeColonists)
            {
                if (pawn != null && !pawn.Dead && !pawn.Destroyed && pawn.royalty != null)
                {
                    yield return pawn;
                }
            }
        }
    }

    public static void Bestow(Pawn pawn, int amount)
    {
        if (pawn?.royalty == null || amount <= 0)
        {
            return;
        }

        Faction empire = EmpireFactionOrNull();
        if (empire == null)
        {
            Log.Warning("[CryoRegenesis] Could not bestow Honor: the Empire faction is not present.");
            return;
        }

        pawn.royalty.GainFavor(empire, amount);
        Messages.Message(
            "Bestowed " + amount + " Honor on " + (pawn.Name?.ToStringShort ?? pawn.LabelShort) + ".",
            pawn,
            MessageTypeDefOf.PositiveEvent);
    }
}
