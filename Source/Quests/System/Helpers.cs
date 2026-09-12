/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Verse.AI.Group;

#if RIMWORLD15 || RIMWORLD16
using LudeonTK;
#endif

namespace BetterRimworlds.CryoRegenesis;

/// Faction/pawn-kind helpers, royal titles, and Royal Ascent unlock.
public partial class RoyaltyRegenesisQuestSystem
{

    private string GetStageLabel(RoyaltyRegenesisStage current)
    {
        switch (current)
        {
            case RoyaltyRegenesisStage.NotStarted:
                return "Stage: not started";
            case RoyaltyRegenesisStage.RulerPrisoners:
                return "Stage: foreign prisoner trials (before Empire notice)";
            case RoyaltyRegenesisStage.Leaders:
                return "Stage: planetary rulers (before Empire notice)";
            case RoyaltyRegenesisStage.LowerNobility:
                return "Stage: Empire lower nobility";
            case RoyaltyRegenesisStage.StellarchNotice:
                return "Stage: Stellarch en route";
            case RoyaltyRegenesisStage.StellarchArrival:
                return "Stage: Stellarch contract";
            case RoyaltyRegenesisStage.EmperorNotice:
                return "Stage: Emperor en route";
            case RoyaltyRegenesisStage.EmperorArrival:
                return "Stage: Emperor contract";
            case RoyaltyRegenesisStage.Completed:
                return "Stage: complete";
            default:
                return "Stage: " + current;
        }
    }

    private void ApplyTrustBreakGoodwillPenalty(Faction faction)
    {
        if (faction == null || faction == Faction.OfPlayer)
        {
            return;
        }

#if RIMWORLD12
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            DeathTrustGoodwillPenalty,
            true,
            true,
            "CryoRegenesis trust broken",
            null);
#else
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            DeathTrustGoodwillPenalty,
            true,
            true,
            HistoryEventDefOf.MemberKilled,
            null);
#endif
    }

    private void ApplyRecruitmentGoodwillPenalty(Faction faction, Pawn pawn)
    {
        if (faction == null || faction == Faction.OfPlayer)
        {
            return;
        }

#if RIMWORLD12
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            RecruitGoodwillPenalty,
            true,
            true,
            "Recruited CryoRegenesis contract client",
            pawn);
#else
        faction.TryAffectGoodwillWith(
            Faction.OfPlayer,
            RecruitGoodwillPenalty,
            true,
            true,
            HistoryEventDefOf.MemberCaptured,
            pawn);
#endif
    }

    private Pawn GetFactionLeader(Faction faction, int minimumBiologicalAge)
    {
        Pawn leader = faction?.leader;
        return this.IsAvailableWorldPawn(leader, faction, minimumBiologicalAge, includeFactionLeader: true)
            ? leader
            : null;
    }

    /// Settled human polities a Stellarch would know: the two vanilla outlander
    /// unions, plus any modded medieval-or-better civ. Currently hostile factions
    /// are skipped — rulers arrive as guests, and vanilla will not host a guest
    /// from a hostile faction. Empire is excluded because the Stellarch has his
    /// own later contract.
    private List<Faction> PlanetaryFactionsForLeaderContracts()
    {
        return Find.FactionManager.AllFactionsListForReading
            .Where(this.IsPlanetaryCivilizationForLeaderContract)
            .ToList();
    }

    private bool IsPlanetaryCivilizationForLeaderContract(Faction faction)
    {
        if (faction == null || faction == Faction.OfPlayer || faction.defeated)
        {
            return false;
        }

        if (faction.def == null || !faction.def.humanlikeFaction)
        {
            return false;
        }

        if (faction.Hidden)
        {
            return false;
        }

        if (this.IsEmpire(faction) || this.IsAncient(faction))
        {
            return false;
        }

        // Pirates (and savage tribes marked unbefriendable) are warbands, not states.
        if (faction.def.permanentEnemy)
        {
            return false;
        }

        // Guest contracts only. Vanilla will not make a hostile pawn a Guest of
        // the colony, and without that status a visiting ruler is not a guest.
        if (faction.HostileTo(Faction.OfPlayer))
        {
            return false;
        }

        // Neolithic = tribals/savages, friendly or hostile. Medieval+ keeps
        // outlanders and modded kingdoms without per-mod checks.
        if (faction.def.techLevel < TechLevel.Medieval)
        {
            return false;
        }

        return true;
    }

    private List<Pawn> GetAvailablePlanetaryLeaders(int minimumBiologicalAge)
    {
        List<Pawn> leaders = new List<Pawn>();
        foreach (Faction faction in this.PlanetaryFactionsForLeaderContracts())
        {
            Pawn leader = this.GetFactionLeader(faction, minimumBiologicalAge);
            if (leader != null)
            {
                leaders.Add(leader);
            }
        }

        return leaders;
    }

    private string BuildPlanetaryLeaderFailureReport(int minimumBiologicalAge)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(
            "No regional faction leaders are available for a CryoRegenesis stay. Retrying later.");
        sb.AppendLine();
        sb.AppendLine("Faction leaders:");

        List<Faction> factions = this.PlanetaryFactionsForLeaderContracts()
            .OrderBy(faction => faction.Name)
            .ToList();
        if (!factions.Any())
        {
            sb.AppendLine("• none (no medieval-or-better planetary civilizations)");
            return sb.ToString();
        }

        foreach (Faction faction in factions)
        {
            sb.AppendLine("• " + this.DescribeFactionLeaderStatus(faction, minimumBiologicalAge));
        }

        return sb.ToString();
    }

    private string DescribeFactionLeaderStatus(Faction faction, int minimumBiologicalAge)
    {
        Pawn leader = faction.leader;
        if (leader == null)
        {
            return faction.Name + ": no faction leader";
        }

        int age = leader.ageTracker != null ? leader.ageTracker.AgeBiologicalYears : 0;
        string name = leader.Name != null ? leader.Name.ToStringShort : leader.LabelShort;
        if (this.GetFactionLeader(faction, minimumBiologicalAge) != null)
        {
            return faction.Name + ": " + name + ", age " + age;
        }

        string reason;
        if (leader.Dead)
        {
            reason = "dead";
        }
        else if (leader.Destroyed)
        {
            reason = "destroyed";
        }
        else if (leader.Faction != faction)
        {
            reason = "left the faction";
        }
        else if (leader.Spawned)
        {
            reason = "on a map";
        }
        else if (leader.ageTracker == null || age < minimumBiologicalAge)
        {
            reason = "need age " + minimumBiologicalAge + "+";
        }
        else if (IsActiveRegenContractPawn(leader))
        {
            reason = "already under contract";
        }
        else if (Find.WorldPawns?.AllPawnsAlive.Contains(leader) != true)
        {
            reason = "not a world pawn";
        }
        else
        {
            reason = "unavailable";
        }

        return faction.Name + ": " + name + ", age " + age + " (" + reason + ")";
    }

    private List<Pawn> GetAvailableWorldPawns(Faction faction, int minimumBiologicalAge)
    {
        if (faction == null || Find.WorldPawns == null)
        {
            return new List<Pawn>();
        }

        return Find.WorldPawns.AllPawnsAlive
            .Where(pawn => this.IsAvailableWorldPawn(pawn, faction, minimumBiologicalAge))
            .ToList();
    }

    private bool IsAvailableWorldPawn(Pawn pawn, Faction faction, int minimumBiologicalAge, bool includeFactionLeader = false)
    {
        return pawn != null
            && !pawn.Dead
            && pawn.Faction == faction
            && (includeFactionLeader || pawn != faction.leader)
            && pawn.ageTracker != null
            && pawn.ageTracker.AgeBiologicalYears >= minimumBiologicalAge
            && !pawn.Spawned
            && !IsActiveRegenContractPawn(pawn)
            && Find.WorldPawns?.AllPawnsAlive.Contains(pawn) == true;
    }

    private PawnKindDef CommonPawnKind(Faction faction = null)
    {
        if (faction?.def?.basicMemberKind != null)
        {
            return faction.def.basicMemberKind;
        }

        return this.NamedPawnKind("Empire_Common_Lodger", this.AnyHumanPawnKind());
    }

    private PawnKindDef NoblePawnKind(Faction faction = null)
    {
        if (faction != null && !this.IsEmpire(faction) && faction.def.basicMemberKind != null)
        {
            // Prefer the faction's own member kinds for non-Empire leaders.
            return faction.def.basicMemberKind;
        }

        return this.NamedPawnKind("Empire_Royal_NobleWimp", this.CommonPawnKind(faction));
    }

    private PawnKindDef LowerNoblePawnKind()
    {
        return this.NamedPawnKind(
            Rand.Element("Empire_Royal_Yeoman", "Empire_Royal_Esquire", "Empire_Royal_Knight"),
            this.NoblePawnKind(this.EmpireFaction()));
    }

    private PawnKindDef NamedPawnKind(string defName, PawnKindDef fallback)
    {
        return DefDatabase<PawnKindDef>.GetNamedSilentFail(defName) ?? fallback;
    }

    private PawnKindDef AnyHumanPawnKind()
    {
        return DefDatabase<PawnKindDef>.AllDefsListForReading
            .FirstOrDefault(def => def.race != null && def.race.race != null && def.race.race.Humanlike);
    }

    private Faction EmpireFaction()
    {
        FactionDef empireDef = DefDatabase<FactionDef>.GetNamedSilentFail("Empire");
        if (empireDef != null)
        {
            Faction empire = Find.FactionManager.FirstFactionOfDef(empireDef);
            if (empire != null)
            {
                return empire;
            }
        }

        return null;
    }

    /// Other planetary factions only — never Ancients, never Empire.
    /// Empire contracts begin only after these succeed.
    private Faction RandomPlanetarySenderFaction()
    {
        return Find.FactionManager.AllFactionsListForReading
            .Where(this.IsEligiblePlanetarySender)
            .RandomElementWithFallback(null);
    }

    private bool IsEligiblePlanetarySender(Faction faction)
    {
        if (faction == null || faction == Faction.OfPlayer || faction.defeated)
        {
            return false;
        }

        if (!faction.def.humanlikeFaction)
        {
            return false;
        }

        if (faction.HostileTo(Faction.OfPlayer))
        {
            return false;
        }

        if (faction.Hidden)
        {
            return false;
        }

        if (this.IsEmpire(faction) || this.IsAncient(faction))
        {
            return false;
        }

        return true;
    }

    private bool IsEmpire(Faction faction)
    {
        if (faction?.def == null)
        {
            return false;
        }

        if (faction.def.defName == "Empire")
        {
            return true;
        }

        try
        {
            return faction.def == FactionDefOf.Empire;
        }
        catch
        {
            return false;
        }
    }

    private bool IsAncient(Faction faction)
    {
        if (faction?.def == null)
        {
            return false;
        }

        string defName = faction.def.defName;
        if (defName == "Ancients" || defName == "AncientsHostile")
        {
            return true;
        }

        try
        {
            return faction.def == FactionDefOf.Ancients || faction.def == FactionDefOf.AncientsHostile;
        }
        catch
        {
            return false;
        }
    }

    private void TrySetRoyalTitle(Pawn pawn, Faction faction, string titleDefName)
    {
        RoyalTitleDef title = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(titleDefName);
        if (title == null || pawn == null || faction == null)
        {
            return;
        }

        object royalty = pawn.GetType().GetField("royalty")?.GetValue(pawn)
            ?? pawn.GetType().GetProperty("royalty")?.GetValue(pawn, null);

        if (royalty == null)
        {
            return;
        }

        MethodInfo method = royalty.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == "SetTitle" &&
                candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(RoyalTitleDef)));

        if (method == null)
        {
            return;
        }

        object[] args = method.GetParameters()
            .Select<ParameterInfo, object>(parameter =>
            {
                if (parameter.ParameterType == typeof(Faction))
                {
                    return faction;
                }

                if (parameter.ParameterType == typeof(RoyalTitleDef))
                {
                    return title;
                }

                if (parameter.ParameterType == typeof(bool))
                {
                    return false;
                }

                return null;
            })
            .ToArray();

        try
        {
            method.Invoke(royalty, args);
        }
        catch (Exception ex)
        {
            Log.Warning($"[CryoRegenesis] Failed to set Royalty title {titleDefName} for {pawn.Name}: {ex.Message}");
        }
    }

    private void TryMakeRoyalAscentAvailable()
    {
        Find.LetterStack.ReceiveLetter(
            "Imperial CryoRegenesis complete",
            "The Emperor has been restored to age 30. Royal Ascent endgame protocols are now being activated.",
            LetterDefOf.PositiveEvent);

        QuestScriptDef questDef = DefDatabase<QuestScriptDef>.GetNamedSilentFail("EndGame_RoyalAscent");
        if (questDef == null)
        {
            Log.Warning("[CryoRegenesis] Could not activate Royal Ascent: EndGame_RoyalAscent was unavailable.");
            return;
        }

        QuestUtility.GenerateQuestAndMakeAvailable(questDef, new Slate());
    }

    private object[] BuildQuestManagerArgs(MethodInfo method, QuestScriptDef questDef)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object[] args = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;
            if (parameterType == typeof(QuestScriptDef))
            {
                args[i] = questDef;
            }
            else if (!parameterType.IsValueType)
            {
                args[i] = null;
            }
            else if (parameterType == typeof(int))
            {
                args[i] = 0;
            }
            else if (parameterType == typeof(float))
            {
                args[i] = 0f;
            }
            else if (parameterType == typeof(bool))
            {
                args[i] = false;
            }
            else
            {
                return null;
            }
        }

        return args.Any(arg => arg == questDef) ? args : null;
    }
}
