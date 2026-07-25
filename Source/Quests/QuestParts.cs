/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Live progress text for the persistent chain quest in the Quests tab.
public class QuestPart_RoyaltyRegenesisChainStatus : QuestPart
{
    public override string DescriptionPart
    {
        get
        {
            RoyaltyRegenesisQuestSystem system = RoyaltyRegenesisQuestSystem.CurrentSystem;
            return system != null ? system.GetChainProgressDescription() : string.Empty;
        }
    }
}

/// Live progress text for an individual CryoRegenesis contract quest.
public class QuestPart_RoyaltyRegenesisContractStatus : QuestPart
{
    public override string DescriptionPart
    {
        get
        {
            RoyaltyRegenesisQuestSystem system = RoyaltyRegenesisQuestSystem.CurrentSystem;
            return system != null ? system.GetContractProgressDescription() : string.Empty;
        }
    }

    public override IEnumerable<GlobalTargetInfo> QuestLookTargets
    {
        get
        {
            RoyaltyRegenesisQuestSystem system = RoyaltyRegenesisQuestSystem.CurrentSystem;
            if (system == null)
            {
                yield break;
            }

            foreach (RoyaltyRegenesisClient client in system.ActiveClients)
            {
                if (client?.pawn != null && !client.pawn.Destroyed)
                {
                    yield return client.pawn;
                }
            }
        }
    }
}

/// Creates and maintains RimWorld Quest objects for the Regenesis campaign.
public static class RoyaltyRegenesisQuestFactory
{
    public const string ChainQuestDefName = "CR_RoyaltyRegenesisQuestChain";
    public const string ContractQuestDefName = "CR_RoyaltyRegenesisContract";

    public static Quest MakeChainQuest()
    {
        Quest quest = Quest.MakeRaw();
        quest.name = "CryoRegenesis: Imperial Rejuvenation";
        quest.description = BuildChainDescriptionBody();
        quest.root = DefDatabase<QuestScriptDef>.GetNamedSilentFail(ChainQuestDefName);
        quest.challengeRating = 4;
        quest.AddPart(new QuestPart_RoyaltyRegenesisChainStatus());
        Find.QuestManager.Add(quest);
        quest.Accept(null);
        return quest;
    }

    public static Quest MakeContractQuest(string title, string description, Quest parentChain)
    {
        Quest quest = Quest.MakeRaw();
        quest.name = title;
        quest.description = description;
        quest.root = DefDatabase<QuestScriptDef>.GetNamedSilentFail(ContractQuestDefName);
        quest.challengeRating = 3;
#if !RIMWORLD12
        if (parentChain != null)
        {
            quest.parent = parentChain;
        }
#endif
        quest.AddPart(new QuestPart_RoyaltyRegenesisContractStatus());
        Find.QuestManager.Add(quest);
        quest.Accept(null);
        return quest;
    }

    public static void EndQuestSafe(Quest quest, QuestEndOutcome outcome, bool sendLetter = true)
    {
        if (quest == null || quest.Historical)
        {
            return;
        }

        quest.End(outcome, sendLetter);
    }

    public static string BuildChainDescriptionBody()
    {
        return
            "Planetary factions will send CryoRegenesis clients by shuttle with fixed return dates. " +
            "Complete enough foreign contracts and the Empire takes notice — lower nobility, then the Stellarch, then the Emperor.\n\n" +
            "Rules:\n" +
            "• Clients arrive by drop-off shuttle (it leaves after unload).\n" +
            "• Pickup shuttles are called when clients finish regeneration.\n" +
            "• Never recruit them (recruitment destroys trust).\n" +
            "• If a client dies under contract, trust collapses and progress resets.\n\n" +
            "Track live stage progress below.";
    }

    public static string BuildContractDescription(
        string roleSummary,
        Faction sender,
        bool isPrisoner,
        int returnByTick)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(roleSummary);
        sb.AppendLine();
        if (sender != null)
        {
            sb.AppendLine("Sending faction: " + sender.Name);
        }

        sb.AppendLine(isPrisoner
            ? "Status: prisoners under contract (do not recruit)."
            : "Status: guests under contract (do not recruit).");
        sb.AppendLine("Contract deadline: " + FormatGameTickDate(returnByTick));
        sb.AppendLine("Logistics: drop-off leaves after unload; pickups arrive when clients finish (or at the deadline).");
        sb.AppendLine();
        sb.AppendLine("Objectives:");
        sb.AppendLine("1. Place clients in a CryoRegenesis casket.");
        sb.AppendLine("2. Reach their requested regression age before the contract deadline.");
        sb.AppendLine("3. When a client is ready, a pickup shuttle is called — load finished clients and Send.");
        sb.AppendLine("4. Another pickup comes for anyone still treating after a partial leave.");
        sb.AppendLine("5. Death or recruitment of a client under contract resets the whole chain.");
        return sb.ToString().TrimEnd();
    }

    /// Player-facing client label: optional royal title prefix, then name, then
    /// "(Title's Wife)" when the client is a wife of a titled (or role-known) spouse.
    public static string FormatContractClientName(Pawn pawn, string role = null)
    {
        if (pawn == null)
        {
            return "Unknown";
        }

        string name = pawn.Name?.ToStringShort ?? pawn.LabelShortCap;
        string title = TryGetRoyalTitleLabel(pawn);
        if (!title.NullOrEmpty())
        {
            name = title + " " + name;
        }

        string husbandTitle = TryGetHusbandTitleForWifePostfix(pawn, role);
        if (!husbandTitle.NullOrEmpty())
        {
            name += " (" + husbandTitle + "'s Wife)";
        }

        return name;
    }

    /// Highest royal title label for <paramref name="pawn"/>, or null.
    public static string TryGetRoyalTitleLabel(Pawn pawn)
    {
        if (pawn?.royalty == null)
        {
            return null;
        }

        RoyalTitle senior = pawn.royalty.MostSeniorTitle;
        if (senior?.def != null)
        {
            return senior.def.GetLabelCapFor(pawn);
        }

        RoyalTitleDef main = pawn.royalty.MainTitle();
        return main != null ? main.GetLabelCapFor(pawn) : null;
    }

    /// When <paramref name="pawn"/> is a wife (by role or Spouse relation), returns the
    /// husband-side title used in "(Title's Wife)" — e.g. Emperor, Stellarch, Count.
    public static string TryGetHusbandTitleForWifePostfix(Pawn pawn, string role = null)
    {
        if (pawn == null)
        {
            return null;
        }

        // Generated Emperor-stage roles are authoritative even before relations settle.
        if (!role.NullOrEmpty())
        {
            string roleLower = role.ToLowerInvariant();
            if (roleLower.Contains("imperial wife"))
            {
                return "Emperor";
            }

            if (roleLower.Contains("stellarch wife"))
            {
                return "Stellarch";
            }
        }

        // Only wives (Spouse), not lovers or fiancés.
        if (pawn.relations?.DirectRelations == null)
        {
            return null;
        }

        foreach (DirectPawnRelation relation in pawn.relations.DirectRelations)
        {
            if (relation?.def != PawnRelationDefOf.Spouse || relation.otherPawn == null)
            {
                continue;
            }

            // Prefer the titled spouse (Emperor / Stellarch / noble husband).
            Pawn spouse = relation.otherPawn;
            string spouseTitle = TryGetRoyalTitleLabel(spouse);
            if (!spouseTitle.NullOrEmpty())
            {
                return spouseTitle;
            }

            // Planetary rulers and other primaries often have no royal title.
            if (spouse.Faction != null && spouse.Faction.leader == spouse)
            {
                return "Ruler";
            }
        }

        // Companion roles that are wives of an untitled primary (e.g. ruler companion).
        if (!role.NullOrEmpty()
            && pawn.relations.GetFirstDirectRelationPawn(PawnRelationDefOf.Spouse) != null)
        {
            string roleLower = role.ToLowerInvariant();
            if (roleLower.Contains("wife")
                || (pawn.gender == Gender.Female && roleLower.Contains("companion")))
            {
                if (roleLower.Contains("stellarch"))
                {
                    return "Stellarch";
                }

                if (roleLower.Contains("ruler"))
                {
                    return "Ruler";
                }

                if (roleLower.Contains("noble"))
                {
                    return "Noble";
                }
            }
        }

        return null;
    }

    /// Formats a <see cref="TickManager.TicksGame"/> value as a calendar date.
    /// GenDate expects absolute ticks, so convert from game ticks first.
    public static string FormatGameTickDate(int ticksGame)
    {
        int tile = Find.CurrentMap?.Tile ?? Find.AnyPlayerHomeMap?.Tile ?? 0;
        int delta = ticksGame - Find.TickManager.TicksGame;
        int absTick = Find.TickManager.TicksAbs + delta;
        return GenDate.DateFullStringAt(absTick, Find.WorldGrid.LongLatOf(tile));
    }

    public static string FormatTickDate(int absoluteTick)
    {
        int tile = Find.CurrentMap?.Tile ?? Find.AnyPlayerHomeMap?.Tile ?? 0;
        return GenDate.DateFullStringAt(absoluteTick, Find.WorldGrid.LongLatOf(tile));
    }
}
