// ==== ./Source/PawnCosmetics.cs ====
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

using RimWorld;
using UnityEngine;
using Verse;
using Random = System.Random;

// ReSharper disable All

namespace BetterRimworlds.CryoRegenesis;

public class Cosmetics
{
    private readonly Random rnd = new Random();

    public bool PossiblyChangeHairColor(Pawn pawn)
    {
        // This only affects human-like pawns.
        if (pawn.RaceProps.Humanlike == false)
        {
            return false;
        }

        // If they have white or gray hair already, definitely change it!
        if (this.HasWhiteOrGrayHair(pawn))
        {
            int pawnAge = pawn.ageTracker.AgeBiologicalYears;
            if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn is {pawn.gender} and {pawnAge} years old.");
            // Substantially reduce the odds if the pawn is over the age of 50 (-10% per year).
            if (pawnAge >= 50)
            {
                if (pawnAge >= 60)
                {
                    if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn age ({pawnAge}) is over 60, not changing hair color.");
                    return false;
                }

                int rangeMax = pawnAge - 50 + 1;
                int randomNum = rnd.Next(0, pawnAge - 50 + 1);
                if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn age ({pawnAge}) is >= 50 < 60, max range: {rangeMax}. Random number = {randomNum}.");

                // 0-1 @ 50 = 50%; 0-2 @ 51 = 33% chance, 0-3 @ 52 = 25% ... 0-10 @ 59 = 9%
                if (randomNum == 0)
                {
                    if (CryoRegenesis.Settings.debugMode) Log.Message("Changing the hair color!!");
                    return this.ChangeHairColorRandomly(pawn);
                }

                return false;
            }
            // If the pawn is male and older than 55, definitely change it.
            // -or-
            // If the pawn is female and older than 30, definitely change it.
            else if (
                (pawn.gender == Gender.Male && pawnAge <= 55) ||
                (pawn.gender == Gender.Female && pawnAge <= 30)
                )
            {
                if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn is {pawnAge} years old and prematurely balding. Changing the hair color!!");
                return this.ChangeHairColorRandomly(pawn);
            }
        }

        var dice1 = rnd.Next(1, 7);
        var dice2 = rnd.Next(1, 7);
        var diceSum = dice1 + dice2;

        if (CryoRegenesis.Settings.debugMode) Log.Message($"Dice rolls: ({dice1}, {dice2}) = {diceSum}");

        // One in Three chance that their hair color will be changed otherwise.
        // Stats taken from https://statweb.stanford.edu/~susan/courses/s60/split/node65.html (http://archive.is/wip/v39lj)
        // 2 = 2.78%, 3 = 5.56%, 4 = 8.33%, 5 = 11.11%, 11 = 5.56% = 33.34%
        if (diceSum <= 5 || diceSum == 11)
        {
            Log.Message("Fate smiles in their favor! Changing the hair color!!");
            return this.ChangeHairColorRandomly(pawn);
        }

        return false;
    }

    private List<Color> GetHairColors()
    {
        var BRIGHTRED   = new Color(237.00f / 256.0f, 41.00f / 256.0f, 57.00f / 256.0f);
        var DARKRED     = new Color(146.00f / 256.0f, 39.00f / 256.0f, 36.00f / 256.0f);
        var HAZEL       = new Color(132.61f / 256.0f, 83.20f / 256.0f, 47.10f / 256.0f);
        var BROWN       = new Color(64.00f / 256.0f, 51.20f / 256.0f, 38.40f / 256.0f);
        var DARKBROWN   = new Color(51.20f / 256.0f, 51.20f / 256.0f, 51.20f / 256.0f);
        var BLACK       = new Color(51.20f / 256.0f, 51.20f / 256.0f, 51.20f / 256.0f);
        var DARKBLACK   = new Color(20.20f / 256.0f, 20.20f / 256.0f, 20.20f / 256.0f);
        var BLONDE      = new Color(222.00f / 256.0f, 188.00f / 256.0f, 153.00f / 256.0f);
        var LIGHTBLONDE = new Color(250.00f / 256.0f, 240.00f / 256.0f, 190.00f / 256.0f);

        var colorList = new List<Color>()
        {
            BRIGHTRED, DARKRED, HAZEL, BLONDE, LIGHTBLONDE
        };

        return colorList;
    }

    private void RerenderPawn(Pawn pawn)
    {
        #if !RIMWORLD15 && !RIMWORLD16
        // Tell the pawn's Drawer that the Person has had a hair-change makeover.
        // This code is from https://github.com/KiameV/rimworld-changedresser/blob/f0b8fcf9073cd1c232fcd26b0b083cb3137924a3/Source/UI/DresserUI.cs
        // Copyright (c) 2017 Travis Offtermatt
        // MIT License
        pawn.Drawer.renderer.graphics.ResolveAllGraphics();
        #else
        pawn.Drawer.renderer.renderTree.SetDirty();
        #endif
        PortraitsCache.SetDirty(pawn);
    }

    private void ChangeHairColor(Pawn pawn, Color hairColor)
    {
        #if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
        pawn.story.HairColor = hairColor;
        #else
        pawn.story.hairColor = hairColor;
        #endif
        this.RerenderPawn(pawn);
    }

    private bool ChangeHairColorRandomly(Pawn pawn)
    {
        if (CryoRegenesis.Settings.debugMode) Log.Message($"Pawn is {pawn.ageTracker.AgeChronologicalYears} chronological years old ({pawn.ageTracker.AgeChronologicalTicks} Ticks)");
        int seed = Convert.ToInt32(pawn.ageTracker.AgeChronologicalTicks > Int32.MaxValue - 1
            ? pawn.ageTracker.AgeChronologicalTicks % Int32.MaxValue
            : pawn.ageTracker.AgeChronologicalTicks);

        var rnd = new Random(seed);
        var colorList = this.GetHairColors();

        this.ChangeHairColor(pawn, colorList[rnd.Next(colorList.Count)]);

        return true;
    }

    private bool HasWhiteOrGrayHair(Pawn pawn)
    {
        string hsv;
        #if RIMWORLD14 || RIMWORLD15 || RIMWORLD16
        Color hairColor = pawn.story.HairColor;
        #else
        Color hairColor = pawn.story.hairColor;
        #endif
        Color.RGBToHSV(hairColor, out float H, out float S, out float V);
        S *= 100;
        V *= 100;
        hsv = string.Format("{0:0.00}°, {1:0.00}%, {2:0.00}%", H, S, V);

        if (CryoRegenesis.Settings.debugMode) Log.Message("Pawn's hair color: " + hairColor + " (" + hsv + " HSV)" + "; body type: " + pawn.story.bodyType);

        // if (H <= 5 && S <= 5 && V >= 40)
        // {
        //     Log.Message("Grey / White hair detected!");
        // }

        return (H < 5 && S <= 5 && V >= 40);
    }
}
