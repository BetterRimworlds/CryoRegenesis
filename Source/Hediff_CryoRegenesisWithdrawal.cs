using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis
{
    public class Hediff_CryoRegenesisWithdrawal : HediffWithComps
    {
        // Stage cutoff points (severity is from 0 to 1)
        // These three ranges mimic the nonlinear phases
        private const float Stage1End = 0.70f;   // Acute Disintegration
        private const float Stage2End = 0.40f;   // Aberrant Homeostasis
        // Stage 3 runs to 0.00                   // Residual Cataclysm

        public override void PostTick()
        {
            base.PostTick();

            if (pawn == null || pawn.Dead)
                return;

            // Only run every 250 ticks (~4 times per hour)
            if (Find.TickManager.TicksGame % 250 != 0)
                return;

            float decayAmount = GetDailySeverityLoss() / 240f; 
            // 240 intervals per day (250 ticks * 240 ≈ 60k ticks)

            Severity -= decayAmount;

            // Clamp
            if (Severity < 0f)
                Severity = 0f;
        }

        private float GetDailySeverityLoss()
        {
            // Nonlinear decay based on severity stage

            if (Severity >= Stage1End)
            {
                // Stage 1 — Acute Disintegration: fast crash (≈3–5 days)
                return 0.06f;   // -6% per day
            }
            else if (Severity >= Stage2End)
            {
                // Stage 2 — Instability: medium decay (~20–30 days)
                return 0.015f;  // -1.5% per day
            }
            else
            {
                // Stage 3 — Chronic Decay (~80–90 days)
                return 0.005f;  // -0.5% per day
            }
        }

        // public override bool CauseRemoved(Hediff cause)
        // {
        //     // Explicitly assure the engine this isn't an artificial body part.
        //     // This prevents Body Purist debuffs.
        //     return false;
        // }

        public override bool Visible => true;

        public override void ExposeData()
        {
            base.ExposeData();
            // Nothing custom to save right now.
        }
    }
}

