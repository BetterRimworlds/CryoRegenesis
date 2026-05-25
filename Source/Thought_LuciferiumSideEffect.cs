// ==== Source/CryoRegenesis/Thought_CryoRegenesis_LuciferiumSideEffect.cs ====
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
using Verse;

namespace BetterRimworlds.CryoRegenesis;

public class Thought_CryoRegenesis_LuciferiumSideEffect : Thought_DurationBased
{
    private const int DaysPerYear = 60;

    public override int CurStageIndex => 0;

    public override string LabelCap => "Grateful to be alive";

    public override string BaseDescription =>
        "I'm so happy to be alive. A little luci forever is worth it!";

    public override float MoodOffset() => 45f;

    public override void CalculateDuration()
    {
        DurationDays = BetterRandom.pick(1 * DaysPerYear, 10 * DaysPerYear);
    }
}
