// ==== Source/CryoRegenesis/Thought_CryoRegenesis_LuciferiumSideEffect.cs ====
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