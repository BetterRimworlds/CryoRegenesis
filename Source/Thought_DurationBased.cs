// ==== ./Source/Thought_DurationBased.cs ====
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

public abstract class Thought_DurationBased : Thought_Memory
{
    protected const int TicksPerDay = 60_000;

    protected int DurationDays;
    protected int ExtraDurationTicks;

    protected int TotalDurationTicks => DurationDays * TicksPerDay + ExtraDurationTicks;

    protected int TicksRemaining => TotalDurationTicks - age;

    public override bool ShouldDiscard => age > TotalDurationTicks;

    public abstract string BaseDescription { get; }

    public override string Description
    {
        get
        {
            if (TicksRemaining <= 0)
            {
                return BaseDescription;
            }

            string expiresIn = TicksRemaining.ToStringTicksToPeriod();

            return BaseDescription + "\n\nExpires in: " + $"<color=#00FFFF>{expiresIn}</color>";
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref DurationDays, "durationDays");
        Scribe_Values.Look(ref ExtraDurationTicks, "extraDurationTicks");
    }

    public abstract void CalculateDuration();

    public override void Init()
    {
        base.Init();
        CalculateDuration();
        ExtraDurationTicks = BetterRandom.pick(1200, 60000);
    }
}