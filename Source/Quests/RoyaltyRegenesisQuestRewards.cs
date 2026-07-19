/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Completion rewards for the planetary-ruler and imperial portions of the
/// CryoRegenesis quest chain. Each reward includes uranium and Luciferium,
/// with one additional cache selected at random for its contract tier.
public partial class RoyaltyRegenesisQuestSystem
{
    /// Uranium paid per treated client on contract completion (inclusive range).
    private const int UraniumPerPersonMin = 250;
    private const int UraniumPerPersonMax = 300;
    private bool completionRewardGranted;

    /// Party size of the current contract, recorded as clients are prepared.
    /// Read at grant time, when activeClients may already be cleared/emptied.
    private int completionRewardClientCount;

    private void ExposeCompletionRewardData()
    {
        Scribe_Values.Look(ref this.completionRewardGranted, "crRoyalCompletionRewardGranted", false);
        Scribe_Values.Look(ref this.completionRewardClientCount, "crRoyalCompletionRewardClientCount", 0);
    }

    private void ResetCompletionRewardState()
    {
        this.completionRewardGranted = false;
    }

    private void GrantCompletionRewardIfEligible(RoyaltyRegenesisStage contractStage)
    {
        if (this.completionRewardGranted || !TryGetRewardTier(contractStage, out CompletionRewardTier tier))
        {
            return;
        }

        Map map = this.GetTargetMap();
        if (map == null)
        {
            Log.Warning("[CryoRegenesis] Could not deliver the contract reward because no player home map is available.");
            return;
        }

        // Nobles arrive 2-10 per contract; luciferium/bonus for that tier pay out per client.
        // Uranium always scales with every treated client (250-300 each).
        int treatedClients = Math.Max(1, this.completionRewardClientCount);
        int payoutMultiplier = contractStage == RoyaltyRegenesisStage.LowerNobility
            ? treatedClients
            : 1;

        RewardEntry bonus = tier.bonuses.RandomElement();
        int luciferiumCount = tier.luciferium * payoutMultiplier;
        int uraniumPerPerson = Rand.RangeInclusive(UraniumPerPersonMin, UraniumPerPersonMax);
        int uraniumCount = uraniumPerPerson * treatedClients;
        int bonusCount = bonus.count * payoutMultiplier;
        List<RewardEntry> rewards = new List<RewardEntry>
        {
            new RewardEntry(ThingDefOf.Luciferium, luciferiumCount, "Luciferium"),
            new RewardEntry(ThingDefOf.Uranium, uraniumCount, "uranium"),
            new RewardEntry(bonus.def, bonusCount, bonus.label),
        };

        List<Thing> delivered = new List<Thing>();
        foreach (RewardEntry reward in rewards)
        {
            this.SpawnReward(map, reward, delivered);
        }

        this.completionRewardGranted = true;
        string clientCountText = treatedClients > 1
            ? " for " + treatedClients + " clients"
            : string.Empty;
        Find.LetterStack.ReceiveLetter(
            "CryoRegenesis contract reward",
            "In gratitude for completing the " + tier.name + " contract" + clientCountText
            + ", the client has delivered "
            + luciferiumCount + " Luciferium, " + uraniumCount + " uranium, and a random bonus cache: "
            + bonusCount + " " + bonus.label + ".",
            LetterDefOf.PositiveEvent,
            delivered.Any() ? new LookTargets(delivered) : null);
    }

    private void SpawnReward(Map map, RewardEntry reward, List<Thing> delivered)
    {
        int remaining = reward.count;
        IntVec3 dropCell = DropCellFinder.TradeDropSpot(map);
        if (!dropCell.IsValid)
        {
            dropCell = DropCellFinder.RandomDropSpot(map);
        }

        while (remaining > 0)
        {
            Thing stack = ThingMaker.MakeThing(reward.def);
            stack.stackCount = Math.Min(remaining, reward.def.stackLimit);
            remaining -= stack.stackCount;

            if (GenPlace.TryPlaceThing(stack, dropCell, map, ThingPlaceMode.Near))
            {
                delivered.Add(stack);
            }
        }
    }

    private static bool TryGetRewardTier(RoyaltyRegenesisStage stage, out CompletionRewardTier tier)
    {
        switch (stage)
        {
            case RoyaltyRegenesisStage.Leaders:
                tier = new CompletionRewardTier("planetary ruler", 10, new List<RewardEntry>
                {
                    new RewardEntry(ThingDefOf.ComponentIndustrial, 20, "components"),
                    new RewardEntry(ThingDefOf.Plasteel, 250, "plasteel"),
                    new RewardEntry(ThingDefOf.Gold, 75, "gold"),
                });
                return true;
            case RoyaltyRegenesisStage.LowerNobility:
                tier = new CompletionRewardTier("imperial noble", 15, new List<RewardEntry>
                {
                    new RewardEntry(ThingDefOf.ComponentSpacer, 15, "advanced components"),
                    new RewardEntry(ThingDefOf.Plasteel, 500, "plasteel"),
                    new RewardEntry(ThingDefOf.Gold, 150, "gold"),
                });
                return true;
            case RoyaltyRegenesisStage.StellarchArrival:
                tier = new CompletionRewardTier("Stellarch", 20, new List<RewardEntry>
                {
                    new RewardEntry(ThingDefOf.ComponentSpacer, 30, "advanced components"),
                    new RewardEntry(ThingDefOf.Plasteel, 800, "plasteel"),
                    new RewardEntry(ThingDefOf.Gold, 250, "gold"),
                });
                return true;
            case RoyaltyRegenesisStage.EmperorArrival:
                tier = new CompletionRewardTier("Emperor", 25, new List<RewardEntry>
                {
                    new RewardEntry(ThingDefOf.ComponentSpacer, 50, "advanced components"),
                    new RewardEntry(ThingDefOf.Plasteel, 1500, "plasteel"),
                    new RewardEntry(ThingDefOf.Gold, 400, "gold"),
                });
                return true;
            default:
                tier = null;
                return false;
        }
    }

    private sealed class CompletionRewardTier
    {
        public CompletionRewardTier(string name, int luciferium, List<RewardEntry> bonuses)
        {
            this.name = name;
            this.luciferium = luciferium;
            this.bonuses = bonuses;
        }

        public readonly string name;
        public readonly int luciferium;
        public readonly List<RewardEntry> bonuses;
    }

    private sealed class RewardEntry
    {
        public RewardEntry(ThingDef def, int count, string label)
        {
            this.def = def;
            this.count = count;
            this.label = label;
        }

        public readonly ThingDef def;
        public readonly int count;
        public readonly string label;
    }
}
