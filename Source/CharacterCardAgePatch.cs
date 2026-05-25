/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *   GPG Fingerprint: D8EA 6E4D 5952 159D 7759  2BB4 EEB6 CE72 F441 EC41
 *   https://github.com/BetterRimworlds/CryoRegenesis
 *
 * This file is licensed under the MIT License.
 */

using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

[HarmonyPatch]
internal static class CharacterCardAgePatch
{
    private static readonly Color TrueAgeLabelColor = new Color(0.87059f, 0.16078f, 0.06275f);
    // private static readonly Color TrueAgeValueColor = new Color(0.228f, 0.58f, 1f);
    private static readonly Color TrueAgeValueColor = new Color(0.8f, 0.9f, 0.1f);
    private static readonly Color CoverColor = new Color(0.13f, 0.13f, 0.13f);

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CharacterCardUtility), "DrawCharacterCard");
    }

    private static void Postfix(Rect rect, Pawn pawn)
    {
        if (pawn?.ageTracker == null)
        {
            return;
        }

        TrueAgeTracker tracker = pawn.health?.hediffSet?.GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;
        if (tracker == null)
        {
            return;
        }

        float trueAgeYears = tracker.GetTrueAgeYears();
        string label = $"<b>{"BetterRimworlds.CryoRegenesis.CharacterCard.TrueAge".Translate()}:</b> ";
        string value = $"<b>{trueAgeYears:F2}</b>";

        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;

        Rect lineRect = new Rect(rect.x, rect.y + 66f, rect.width, Text.LineHeight);
        Color oldColor = GUI.color;
        Widgets.DrawBoxSolid(lineRect, CoverColor);
        GUI.color = TrueAgeLabelColor;
        Widgets.Label(lineRect, label);

        float labelWidth = Text.CalcSize(label.StripTags()).x;
        Rect valueRect = new Rect(lineRect.x + labelWidth, lineRect.y, lineRect.width - labelWidth, lineRect.height);
        GUI.color = TrueAgeValueColor;
        Widgets.Label(valueRect, value);
        GUI.color = oldColor;

        TooltipHandler.TipRegion(
            lineRect,
            $"{"BetterRimworlds.CryoRegenesis.CharacterCard.TrueAge".Translate()}: {trueAgeYears:F2} years\n" +
            $"{"Biological Age".Translate()}: {pawn.ageTracker.AgeBiologicalYearsFloat:F2} years\n" +
            $"{"Chronological Age".Translate()}: {pawn.ageTracker.AgeChronologicalYearsFloat:F2} years"
        );
    }
}
