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

public partial class Building_CryoRegenesis
{
    // Original biological age in ticks when the pawn entered CryoRegenesis.
    // Used to calculate exact de-aging for the TrueAgeTracker ledger.
    private long origBiologicalAgeTicks = -1L;

    private void ExposeTrueAgeData()
    {
        Scribe_Values.Look(ref this.origBiologicalAgeTicks, "OrigBiologicalAgeTicks", -1L);
    }

    private void RecordTrueAgeSnapshot(Pawn pawn)
    {
        if (pawn?.ageTracker == null)
        {
            this.origBiologicalAgeTicks = -1L;
            return;
        }

        this.origBiologicalAgeTicks = pawn.ageTracker.AgeBiologicalTicks;
    }

    private void ApplyTrueAgeOnEject(Pawn pawn)
    {
        // Update the TrueAgeTracker with the exact de-aging that occurred.
        //
        // This is done once, on ejection, instead of every de-aging tick.
        // At this moment the procedure is complete and the final biological
        // age is known.
        if (pawn == null || this.origBiologicalAgeTicks <= 0)
        {
            return;
        }

        long removedAgeTicks = this.origBiologicalAgeTicks - pawn.ageTracker.AgeBiologicalTicks;
        if (removedAgeTicks <= 0)
        {
            return;
        }

        try
        {
            TrueAgeTracker tracker =
                RegenesisThoughts.GetOrAddTrueAgeTracker(pawn, this.origBiologicalAgeTicks);
            tracker?.RecordCryoRegenesisDeAging(removedAgeTicks);
        }
        catch (Exception ex)
        {
            Log.Warning($"[CryoRegenesis] Failed to update true-age tracking for {pawn.Name}; ejecting anyway. {ex}");
        }
    }

    private void ResetTrueAgeTracking()
    {
        this.origBiologicalAgeTicks = -1L;
    }
}
