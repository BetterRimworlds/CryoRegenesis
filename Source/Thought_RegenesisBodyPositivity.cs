// ==== ./Source/Thought_RegenesisBodyPositivity.cs ====
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
        base.Init();
        this._moodBonus = BetterRandom.pick(-15, 15);
    }


    public override int CurStageIndex
    {
        get
        {
            // Messages.Message("Years Younger: " + this._yearsReversed, MessageTypeDefOf.PositiveEvent);
            if (_yearsReversed >= 60) return 4;
            if (_yearsReversed >= 40) return 3;
            if (_yearsReversed >= 20) return 2;
            if (_yearsReversed >= 10) return 1;
            return 0;
        }
    }

    public override string LabelCap => CurStageIndex switch
    {
        4 => "Fully regenerated",
        3 => "Majorly regenerated",
        2 => "Substantially regenerated",
        1 => "Mildly regenerated",
        _ => "Regenerated"
    };

    public override string BaseDescription => CurStageIndex switch
    {
        4 => "My body feels like it did in my earliest adulthood - fresh, strong, and full of potential!",
        3 => "60 years gone! I recognize my younger self in the mirror - the vigor of my prime is returning!",
        2 => "40 years erased! My joints move smoothly again and my energy is returning.",
        1 => "I feel 20 years younger! The weight of time has been lifted from my body.",
        _ => "I feel a few years younger."
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

        bool isBodyPurist = pawn.story?.traits?.HasTrait(TraitDefOf.BodyPurist) ?? false;

        bool recentlyResurrected = pawn.health?.hediffSet?.HasHediff(HediffDefOf.ResurrectionSickness) ?? false;

        if (recentlyResurrected)
        {
            DurationDays = BetterRandom.pick(60, 600);
        }
        else if (isBodyPurist)
        {
            DurationDays = BetterRandom.pick(30, 300) * BetterRandom.pick(1, 3);
            this._moodBonus = BetterRandom.pick(1, 2);
        }
        else
        {
            DurationDays = BetterRandom.pick(30, 300);
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref _yearsReversed, "yearsReversed", 0);
    }
}