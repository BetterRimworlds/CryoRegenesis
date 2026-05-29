// ==== Source/CharacterCardAgePatch.cs ====
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

using System;
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

    private static GUIStyle trueAgeLabelStyle;
    private static GUIStyle trueAgeValueStyle;

    private static GUIStyle TrueAgeLabelStyle
    {
        get
        {
            if (trueAgeLabelStyle == null)
            {
                trueAgeLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperLeft,
                    richText = true,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };

                trueAgeLabelStyle.normal.textColor = TrueAgeLabelColor;
            }

            return trueAgeLabelStyle;
        }
    }

    private static GUIStyle TrueAgeValueStyle
    {
        get
        {
            if (trueAgeValueStyle == null)
            {
                trueAgeValueStyle = new GUIStyle(TrueAgeLabelStyle);
                trueAgeValueStyle.normal.textColor = TrueAgeValueColor;
            }

            return trueAgeValueStyle;
        }
    }

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

        TrueAgeTracker tracker =
            pawn.health?.hediffSet?.GetFirstHediffOfDef(TrueAgeDefOf.TrueAgeTracker) as TrueAgeTracker;

        if (tracker == null)
        {
            return;
        }

        float trueAgeYears = tracker.GetTrueAgeYears();
        float chronologicalAgeYears = pawn.ageTracker.AgeChronologicalYearsFloat;
        float cryptosleepYears = Math.Max(0f, chronologicalAgeYears - trueAgeYears);

        string translatedLabel = "BetterRimworlds.CryoRegenesis.CharacterCard.TrueAge".Translate();
        string label = $"<b>{translatedLabel}:</b> ";
        string value = $"<b>{trueAgeYears:F2}</b>";

        GUIStyle labelStyle = TrueAgeLabelStyle;
        GUIStyle valueStyle = TrueAgeValueStyle;

        float lineHeight = labelStyle.CalcHeight(new GUIContent(label), rect.width);
        if (lineHeight <= 0f)
        {
            lineHeight = 24f;
        }

        Rect lineRect = new Rect(rect.x, rect.y + 66f, rect.width, lineHeight);

        Widgets.DrawBoxSolid(lineRect, CoverColor);

        GUI.Label(lineRect, label, labelStyle);

        float labelWidth = labelStyle.CalcSize(new GUIContent(label)).x;
        Rect valueRect = new Rect(
            lineRect.x + labelWidth,
            lineRect.y,
            lineRect.width - labelWidth,
            lineRect.height
        );

        GUI.Label(valueRect, value, valueStyle);

        // Less than ideal. Translates but wraps along the lines.
        // int bioYears, bioQuadrums, bioDays;
        // int chronoYears, chronoQuads, chronoDays;
        // float bioHours = 0f, chronoHours;
        //
        // pawn.ageTracker.AgeBiologicalTicks.TicksToPeriod(out bioYears, out bioQuadrums, out bioDays, out bioHours);
        // pawn.ageTracker.AgeChronologicalTicks.TicksToPeriod(out chronoYears, out chronoQuads, out chronoDays, out chronoHours);
        //
        // TooltipHandler.TipRegion(
        //     lineRect,
        //     $"{translatedLabel}: {"PeriodYears".Translate(trueAgeYears)}\n" +
        //     $"{"AgeBiological".Translate(bioYears, bioQuadrums, bioDays)}\n" +
        //     $"{"AgeChronological".Translate(chronoYears, chronoQuads, chronoDays)}"
        // );

        // Doesn't translate but looks much better.
        TooltipHandler.TipRegion(
            lineRect,
            $"{translatedLabel}: {trueAgeYears:N2} years\n" +
            $"Biological age: {pawn.ageTracker.AgeBiologicalYearsFloat:N2} years\n" +
            $"Cryptosleep: {cryptosleepYears:N2} years\n" +
            $"Chronological age: {chronologicalAgeYears:N2} years"
        );
    }
}
