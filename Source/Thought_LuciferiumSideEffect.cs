// ==== Source/CryoRegenesis/Thought_CryoRegenesis_LuciferiumSideEffect.cs ====
using BetterRimworlds.ThermoVoltaicGenerator;
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

public class Thought_CryoRegenesis_LuciferiumSideEffect : Thought_Memory
{
    private const int TicksPerDay = 60_000;
    private const int DaysPerYear = 60;

    private int durationDays;
    private int extraDurationTicks;

    private int TotalDurationTicks =>
        durationDays * TicksPerDay + extraDurationTicks;

    private int TicksRemaining =>
        TotalDurationTicks - age;

    public override int CurStageIndex => 0;

    public override string LabelCap => "Grateful to be alive";

    public override string Description
    {
        get
        {
            string baseDescription =
                "I'm so happy to be alive. A little luci forever is worth it!";

            if (TicksRemaining <= 0)
            {
                return baseDescription;
            }

            string expiresIn = TicksRemaining.ToStringTicksToPeriod();

            return baseDescription + "\n\nExpires in: " + $"<color=#00FFFF>{expiresIn}</color>";
        }
    }

    public override float MoodOffset() => 45f;

    public override bool ShouldDiscard =>
        age > TotalDurationTicks;

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref durationDays, "durationDays");
        Scribe_Values.Look(ref extraDurationTicks, "extraDurationTicks");
    }

    public override void Init()
    {
        base.Init();

        durationDays = BetterRandom.pick(1 * DaysPerYear, 10 * DaysPerYear);
        extraDurationTicks = BetterRandom.pick(1200, 60000);
    }
}