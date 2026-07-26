// ==== ./Source/Thought_RegenesisBodyPositivity.cs ====
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
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

public class Thought_RegenesisBodyPositivity : Thought_DurationBased
{
    private int _yearsReversed;
    private int _moodBonus;

    public void SetYearsReversed(int years)
    {
        this._yearsReversed = years;
    }

    public override void Init()
    {
        // Mood bonus before CalculateDuration so BodyPurist can scale it.
        this._moodBonus = BetterRandom.pick(-15, 15);
        base.Init();
    }

    /// Always add a new stack; MemoryThoughtHandler drops the oldest past stackLimit.
    /// Default Thought_Memory merge only Renew()s the oldest and discards new years/duration.
    public override bool TryMergeWithExistingMemory(out bool showBubble)
    {
        showBubble = true;
        return false;
    }

    public override int CurStageIndex
    {
        get
        {
            if (_yearsReversed >= 60) return 4;
            if (_yearsReversed >= 40) return 3;
            if (_yearsReversed >= 20) return 2;
            if (_yearsReversed >= 10) return 1;
            return 0;
        }
    }

    public override string LabelCap => CurStageIndex switch
    {
        4 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Label.Full".Translate(),
        3 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Label.Major".Translate(),
        2 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Label.Substantial".Translate(),
        1 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Label.Mild".Translate(),
        _ => "BetterRimworlds.CryoRegenesis.BodyPositivity.Label.Some".Translate()
    };

    public override string BaseDescription => CurStageIndex switch
    {
        4 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Desc.Full".Translate(),
        3 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Desc.Major".Translate(_yearsReversed),
        2 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Desc.Substantial".Translate(_yearsReversed),
        1 => "BetterRimworlds.CryoRegenesis.BodyPositivity.Desc.Mild".Translate(_yearsReversed),
        _ => "BetterRimworlds.CryoRegenesis.BodyPositivity.Desc.Some".Translate(),
    };

    public override float MoodOffset() => CurStageIndex switch
    {
        4 => 40f + this._moodBonus,
        3 => 35f + this._moodBonus,
        2 => 25f + this._moodBonus,
        1 => 20f + this._moodBonus,
        _ => 15f + this._moodBonus
    };

    public override void CalculateDuration()
    {
        if (pawn == null)
        {
            DurationDays = BetterRandom.pick(5, 60);
            return;
        }

        int years = Math.Max(0, _yearsReversed);
        if (years <= 5)
        {
            DurationDays = BetterRandom.pick(5, 20);
        }
        else if (years <= 15)
        {
            DurationDays = BetterRandom.pick(15, 45);
        }
        else
        {
            // Cumulative True Age removed > 15 years: at least 30–60 days.
            int minDays = BetterRandom.pick(30, 60);
            int maxDays = Math.Min(300, Math.Max(minDays, years * 3));
            DurationDays = BetterRandom.pick(minDays, maxDays);
        }

        bool isBodyPurist = pawn.story?.traits?.HasTrait(TraitDefOf.BodyPurist) ?? false;
        bool recentlyResurrected = pawn.health?.hediffSet?.HasHediff(HediffDefOf.ResurrectionSickness) ?? false;

        if (recentlyResurrected)
        {
            DurationDays = Math.Max(DurationDays, BetterRandom.pick(60, 600));
        }
        else if (isBodyPurist)
        {
            DurationDays *= BetterRandom.pick(1, 3);
            this._moodBonus *= BetterRandom.pick(1, 2);
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref _yearsReversed, "yearsReversed", 0);
        Scribe_Values.Look(ref _moodBonus, "moodBonus", 0);
    }
}
