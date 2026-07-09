/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

#if RIMWORLD15 || RIMWORLD16
using LudeonTK;
#endif

namespace BetterRimworlds.CryoRegenesis;

public class RoyaltyRegenesisQuestSystem : GameComponent
{
    private const int CheckIntervalTicks = 2500;
    private const int HalfYearTicks = GenDate.TicksPerYear / 2;

    private RoyaltyRegenesisStage stage = RoyaltyRegenesisStage.NotStarted;
    private RoyaltyRegenesisStage activeContractStage = RoyaltyRegenesisStage.NotStarted;
    private int nextEventTick = -1;
    private int rulerContractsCompleted;
    private int leaderContractsCompleted;
    private int nobleContractsCompleted;
    private bool royalAscentTriggered;
    private List<RoyaltyRegenesisClient> activeClients = new List<RoyaltyRegenesisClient>();

    public RoyaltyRegenesisQuestSystem(Game game)
    {
    }

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref this.stage, "crRoyalStage", RoyaltyRegenesisStage.NotStarted);
        Scribe_Values.Look(ref this.activeContractStage, "crRoyalActiveContractStage", RoyaltyRegenesisStage.NotStarted);
        Scribe_Values.Look(ref this.nextEventTick, "crRoyalNextEventTick", -1);
        Scribe_Values.Look(ref this.rulerContractsCompleted, "crRoyalRulerContractsCompleted", 0);
        Scribe_Values.Look(ref this.leaderContractsCompleted, "crRoyalLeaderContractsCompleted", 0);
        Scribe_Values.Look(ref this.nobleContractsCompleted, "crRoyalNobleContractsCompleted", 0);
        Scribe_Values.Look(ref this.royalAscentTriggered, "crRoyalAscentTriggered", false);
        Scribe_Collections.Look(ref this.activeClients, "crRoyalActiveClients", LookMode.Deep);

        if (Scribe.mode == LoadSaveMode.PostLoadInit && this.activeClients == null)
        {
            this.activeClients = new List<RoyaltyRegenesisClient>();
        }
    }

    public override void GameComponentTick()
    {
        base.GameComponentTick();

        if (!ModsConfig.RoyaltyActive)
        {
            return;
        }

        if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
        {
            return;
        }

        if (!this.CanRunCampaign())
        {
            return;
        }

        this.CheckActiveClients();

        if (this.activeClients.Any())
        {
            return;
        }

        int ticksGame = Find.TickManager.TicksGame;
        if (this.stage == RoyaltyRegenesisStage.NotStarted)
        {
            this.stage = RoyaltyRegenesisStage.RulerPrisoners;
            this.nextEventTick = ticksGame + Rand.RangeInclusive(3, 8) * GenDate.TicksPerDay;
        }

        if (this.stage != RoyaltyRegenesisStage.Completed && ticksGame >= this.nextEventTick)
        {
            this.AdvanceCampaign();
        }
    }

    private bool CanRunCampaign()
    {
        return Find.Maps.Any(map =>
            map.IsPlayerHome &&
            map.listerBuildings.AllBuildingsColonistOfClass<Building_CryoRegenesis>().Any());
    }

    private Map GetTargetMap()
    {
        return Find.Maps.FirstOrDefault(map =>
            map.IsPlayerHome &&
            map.listerBuildings.AllBuildingsColonistOfClass<Building_CryoRegenesis>().Any());
    }

    private void AdvanceCampaign()
    {
        Map map = this.GetTargetMap();
        if (map == null)
        {
            this.nextEventTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
            return;
        }

        switch (this.stage)
        {
            case RoyaltyRegenesisStage.RulerPrisoners:
                this.StartRulerPrisonerContract(map);
                break;
            case RoyaltyRegenesisStage.Leaders:
                this.StartLeaderContract(map);
                break;
            case RoyaltyRegenesisStage.LowerNobility:
                this.StartLowerNobilityContract(map);
                break;
            case RoyaltyRegenesisStage.StellarchNotice:
                this.SendTravelNotice(
                    "The Stellarch has taken notice",
                    "Reports of successful CryoRegenesis treatments have reached the Stellarch. An imperial shuttle has departed. It should arrive in several days.");
                this.stage = RoyaltyRegenesisStage.StellarchArrival;
                this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(3, 8) * GenDate.TicksPerDay;
                break;
            case RoyaltyRegenesisStage.StellarchArrival:
                this.StartStellarchContract(map);
                break;
            case RoyaltyRegenesisStage.EmperorNotice:
                int years = Rand.RangeInclusive(1, 10);
                this.SendTravelNotice(
                    "The Emperor is coming",
                    $"The restored Stellarch's report has reached the Emperor. The imperial household has committed to the journey, but interstellar travel will take {years} year(s).");
                this.stage = RoyaltyRegenesisStage.EmperorArrival;
                this.nextEventTick = Find.TickManager.TicksGame + years * GenDate.TicksPerYear;
                break;
            case RoyaltyRegenesisStage.EmperorArrival:
                this.StartEmperorContract(map);
                break;
        }
    }

    public void DebugStartAt(RoyaltyRegenesisStage debugStage)
    {
        if (!ModsConfig.RoyaltyActive)
        {
            Messages.Message("CryoRegenesis Royalty quest chain requires the Royalty DLC.", MessageTypeDefOf.RejectInput);
            return;
        }

        if (this.GetTargetMap() == null)
        {
            Messages.Message("Build a CryoRegenesis casket on a player home map before starting the Royalty quest chain.", MessageTypeDefOf.RejectInput);
            return;
        }

        this.activeClients.Clear();
        this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
        this.stage = debugStage;
        this.nextEventTick = Find.TickManager.TicksGame;
        this.AdvanceCampaign();
    }

    public void DebugTriggerNextEvent()
    {
        if (!ModsConfig.RoyaltyActive)
        {
            Messages.Message("CryoRegenesis Royalty quest chain requires the Royalty DLC.", MessageTypeDefOf.RejectInput);
            return;
        }

        if (this.GetTargetMap() == null)
        {
            Messages.Message("Build a CryoRegenesis casket on a player home map before triggering the next Royalty quest event.", MessageTypeDefOf.RejectInput);
            return;
        }

        this.nextEventTick = Find.TickManager.TicksGame;
        this.AdvanceCampaign();
    }

    private void StartRulerPrisonerContract(Map map)
    {
        Faction sender = this.RandomNonPlayerFaction() ?? this.EmpireFaction();
        int pawnCount = Rand.RangeInclusive(1, 2);
        List<Pawn> pawns = new List<Pawn>();

        for (int i = 0; i < pawnCount; i++)
        {
            Pawn pawn = this.GeneratePawn(this.CommonPawnKind(), sender, Rand.RangeInclusive(24, 70));
            int duration = Rand.Element(HalfYearTicks, GenDate.TicksPerYear, GenDate.TicksPerYear * 2);
            long targetTicks = Math.Max(18L * GenDate.TicksPerYear, pawn.ageTracker.AgeBiologicalTicks - duration);
            this.PrepareClient(pawn, targetTicks, "foreign prisoner");
            pawns.Add(pawn);
        }

        this.activeContractStage = RoyaltyRegenesisStage.RulerPrisoners;
        this.DropClients(map, pawns, "CryoRegenesis contract: prisoners",
            $"{(sender?.Name ?? "A planetary ruler")} has sent prisoners and dependents for a trial CryoRegenesis contract. Their requested regression is six months, one year, or two years.");
    }

    private void StartLeaderContract(Map map)
    {
        Faction sender = this.RandomNonPlayerFaction() ?? this.EmpireFaction();
        Pawn leader = this.GeneratePawn(this.NoblePawnKind(), sender, Rand.RangeInclusive(35, 82));
        int yearsToRemove = Rand.RangeInclusive(2, 18);
        long targetTicks = Math.Max(30L * GenDate.TicksPerYear, leader.ageTracker.AgeBiologicalTicks - yearsToRemove * GenDate.TicksPerYear);

        this.PrepareClient(leader, targetTicks, "planetary ruler");
        this.activeContractStage = RoyaltyRegenesisStage.Leaders;
        this.DropClients(map, new List<Pawn> { leader }, "CryoRegenesis contract: ruler",
            $"{leader.Name.ToStringShort}, a ruler over the age of 30, has arrived for a privately negotiated CryoRegenesis stay.");
    }

    private void StartLowerNobilityContract(Map map)
    {
        Faction empire = this.EmpireFaction();
        Pawn pawn = this.GeneratePawn(this.LowerNoblePawnKind(), empire, Rand.RangeInclusive(31, 72));
        int targetAge = Rand.RangeInclusive(21, 40);

        this.PrepareClient(pawn, targetAge * (long)GenDate.TicksPerYear, "lower imperial noble");
        this.activeContractStage = RoyaltyRegenesisStage.LowerNobility;
        this.DropClients(map, new List<Pawn> { pawn }, "Imperial CryoRegenesis contract",
            $"The Empire has sent {pawn.Name.ToStringShort}, a lower noble, to verify your CryoRegenesis process.");
    }

    private void StartStellarchContract(Map map)
    {
        Faction empire = this.EmpireFaction();
        Pawn stellarch = this.GeneratePawn(this.NamedPawnKind("Empire_Royal_Stellarch", this.NoblePawnKind()), empire, Rand.RangeInclusive(45, 85));
        this.TrySetRoyalTitle(stellarch, empire, "Stellarch");

        int targetAge = Rand.RangeInclusive(21, 35);
        List<Pawn> party = new List<Pawn> { stellarch };
        this.PrepareClient(stellarch, targetAge * (long)GenDate.TicksPerYear, "stellarch");
        party.AddRange(this.GetOrGeneratePartners(stellarch, empire, 30, "stellarch companion"));

        this.activeContractStage = RoyaltyRegenesisStage.StellarchArrival;
        this.DropClients(map, party, "The Stellarch has arrived",
            $"The Stellarch has arrived for CryoRegenesis and has chosen to regress to age {targetAge}. Any spouses or lovers in the party have chosen age 30.");
    }

    private void StartEmperorContract(Map map)
    {
        Faction empire = this.EmpireFaction();
        Pawn emperor = this.GeneratePawn(this.NamedPawnKind("Empire_Royal_Stellarch", this.NoblePawnKind()), empire, Rand.RangeInclusive(60, 100));
        this.TrySetRoyalTitle(emperor, empire, "Emperor");

        List<Pawn> party = new List<Pawn> { emperor };
        this.PrepareClient(emperor, 20L * GenDate.TicksPerYear, "emperor", triggerRoyalAscent: true);
        party.AddRange(this.GetOrGeneratePartners(emperor, empire, 21, "imperial companion"));

        this.activeContractStage = RoyaltyRegenesisStage.EmperorArrival;
        this.DropClients(map, party, "The Emperor has arrived",
            "The Emperor has arrived for CryoRegenesis and will regress to age 20. Any spouses or lovers in the party have chosen age 21.");
    }

    private List<Pawn> GetOrGeneratePartners(Pawn noble, Faction faction, int targetAge, string role)
    {
        List<Pawn> partners = noble.relations?.DirectRelations
            ?.Where(relation =>
                relation.def == PawnRelationDefOf.Spouse ||
                relation.def == PawnRelationDefOf.Lover ||
                relation.def == PawnRelationDefOf.Fiance)
            .Select(relation => relation.otherPawn)
            .Where(pawn => pawn != null && !pawn.Dead)
            .Distinct()
            .ToList() ?? new List<Pawn>();

        if (!partners.Any() && Rand.Chance(0.65f))
        {
            Pawn spouse = this.GeneratePawn(this.NoblePawnKind(), faction, Rand.RangeInclusive(35, 80));
            noble.relations.AddDirectRelation(PawnRelationDefOf.Spouse, spouse);
            partners.Add(spouse);
        }

        foreach (Pawn partner in partners)
        {
            if (partner.ageTracker.AgeBiologicalYears < targetAge + 5)
            {
                partner.ageTracker.AgeBiologicalTicks = Rand.RangeInclusive(targetAge + 10, targetAge + 45) * (long)GenDate.TicksPerYear;
            }

            this.PrepareClient(partner, targetAge * (long)GenDate.TicksPerYear, role);
        }

        return partners;
    }

    private void PrepareClient(Pawn pawn, long desiredAgeTicks, string role, bool triggerRoyalAscent = false)
    {
        desiredAgeTicks = Math.Max(0, Math.Min(desiredAgeTicks, pawn.ageTracker.AgeBiologicalTicks));
        TrueAgeTracker tracker = RegenesisThoughts.GetOrAddTrueAgeTracker(pawn, pawn.ageTracker.AgeBiologicalTicks);
        if (tracker != null)
        {
            tracker.desiredAgeTicks = desiredAgeTicks;
        }

        if (!pawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic))
        {
            pawn.health.AddHediff(HediffDefOf.Anesthetic);
        }

        this.activeClients.Add(new RoyaltyRegenesisClient
        {
            pawn = pawn,
            desiredAgeTicks = desiredAgeTicks,
            role = role,
            triggerRoyalAscent = triggerRoyalAscent,
        });
    }

    private void DropClients(Map map, List<Pawn> pawns, string label, string text)
    {
        IntVec3 cell = DropCellFinder.RandomDropSpot(map);
        DropPodUtility.DropThingsNear(cell, map, pawns.Cast<Thing>());
        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent, new LookTargets(pawns));
    }

    private void CheckActiveClients()
    {
        if (!this.activeClients.Any())
        {
            return;
        }

        this.activeClients.RemoveAll(client => client?.pawn == null || client.pawn.Destroyed);
        if (!this.activeClients.Any())
        {
            this.ScheduleRetry("CryoRegenesis contract lost", "The active CryoRegenesis client could no longer be found.");
            return;
        }

        if (this.activeClients.Any(client => client.pawn.Dead))
        {
            this.activeClients.Clear();
            this.ScheduleRetry("CryoRegenesis contract failed", "A Royalty CryoRegenesis client died before the requested age regression was completed.");
            return;
        }

        if (this.activeClients.Any(client => !this.ClientReachedDesiredAge(client)))
        {
            return;
        }

        bool triggerEndgame = this.activeClients.Any(client => client.triggerRoyalAscent);
        this.activeClients.Clear();

        if (triggerEndgame)
        {
            this.stage = RoyaltyRegenesisStage.Completed;
            this.royalAscentTriggered = true;
            this.TryMakeRoyalAscentAvailable();
            return;
        }

        this.CompleteActiveContract();
    }

    private bool ClientReachedDesiredAge(RoyaltyRegenesisClient client)
    {
        return client.pawn.ageTracker.AgeBiologicalTicks <= client.desiredAgeTicks + CheckIntervalTicks;
    }

    private void CompleteActiveContract()
    {
        switch (this.activeContractStage)
        {
            case RoyaltyRegenesisStage.RulerPrisoners:
                this.rulerContractsCompleted++;
                if (this.rulerContractsCompleted < 3)
                {
                    this.stage = RoyaltyRegenesisStage.RulerPrisoners;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(15, 35) * GenDate.TicksPerDay;
                }
                else
                {
                    this.stage = RoyaltyRegenesisStage.Leaders;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(25, 45) * GenDate.TicksPerDay;
                }
                break;
            case RoyaltyRegenesisStage.Leaders:
                this.leaderContractsCompleted++;
                if (this.leaderContractsCompleted < 2)
                {
                    this.stage = RoyaltyRegenesisStage.Leaders;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
                }
                else
                {
                    this.stage = RoyaltyRegenesisStage.LowerNobility;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
                }
                break;
            case RoyaltyRegenesisStage.LowerNobility:
                this.nobleContractsCompleted++;
                if (this.nobleContractsCompleted < 2)
                {
                    this.stage = RoyaltyRegenesisStage.LowerNobility;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
                }
                else
                {
                    this.stage = RoyaltyRegenesisStage.StellarchNotice;
                    this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(15, 30) * GenDate.TicksPerDay;
                }
                break;
            case RoyaltyRegenesisStage.StellarchArrival:
                this.stage = RoyaltyRegenesisStage.EmperorNotice;
                this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 90) * GenDate.TicksPerDay;
                break;
        }

        this.activeContractStage = RoyaltyRegenesisStage.NotStarted;
        Find.LetterStack.ReceiveLetter(
            "CryoRegenesis contract complete",
            "The Royalty CryoRegenesis client has reached the requested regression age.",
            LetterDefOf.PositiveEvent);
    }

    private void ScheduleRetry(string label, string text)
    {
        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NegativeEvent);
        this.nextEventTick = Find.TickManager.TicksGame + Rand.RangeInclusive(30, 60) * GenDate.TicksPerDay;
    }

    private void SendTravelNotice(string label, string text)
    {
        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent);
    }

    private Pawn GeneratePawn(PawnKindDef kind, Faction faction, int biologicalAge)
    {
        PawnGenerationRequest request = new PawnGenerationRequest(kind, faction);
        request.FixedBiologicalAge = biologicalAge;
        request.FixedChronologicalAge = biologicalAge;
        return PawnGenerator.GeneratePawn(request);
    }

    private PawnKindDef CommonPawnKind()
    {
        return this.NamedPawnKind("Empire_Common_Lodger", this.AnyHumanPawnKind());
    }

    private PawnKindDef NoblePawnKind()
    {
        return this.NamedPawnKind("Empire_Royal_NobleWimp", this.CommonPawnKind());
    }

    private PawnKindDef LowerNoblePawnKind()
    {
        return this.NamedPawnKind(
            Rand.Element("Empire_Royal_Yeoman", "Empire_Royal_Esquire", "Empire_Royal_Knight"),
            this.NoblePawnKind());
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

        return this.RandomNonPlayerFaction();
    }

    private Faction RandomNonPlayerFaction()
    {
        return Find.FactionManager.AllFactionsListForReading
            .Where(faction =>
                faction != null &&
                faction != Faction.OfPlayer &&
                !faction.defeated &&
                !faction.HostileTo(Faction.OfPlayer) &&
                faction.def.humanlikeFaction)
            .RandomElementWithFallback(null);
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
            "The Emperor has reached age 20. Royal Ascent shuttle endgame protocols are now being activated.",
            LetterDefOf.PositiveEvent);

        QuestScriptDef questDef = DefDatabase<QuestScriptDef>.GetNamedSilentFail("EndGame_RoyalAscent");
        object questManager = Find.QuestManager;

        if (questDef == null || questManager == null)
        {
            Log.Warning("[CryoRegenesis] Could not activate Royal Ascent: EndGame_RoyalAscent or QuestManager was unavailable.");
            return;
        }

        foreach (MethodInfo method in questManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name != "GenerateAndAddQuest" && method.Name != "GenerateQuestAndMakeAvailable")
            {
                continue;
            }

            object[] args = this.BuildQuestManagerArgs(method, questDef);
            if (args == null)
            {
                continue;
            }

            try
            {
                method.Invoke(questManager, args);
                return;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CryoRegenesis] Royal Ascent activation via {method.Name} failed: {ex.Message}");
            }
        }

        Log.Warning("[CryoRegenesis] Could not find a compatible Royal Ascent quest generation method.");
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

public class RoyaltyRegenesisClient : IExposable
{
    public Pawn pawn;
    public long desiredAgeTicks;
    public string role;
    public bool triggerRoyalAscent;

    public void ExposeData()
    {
        Scribe_References.Look(ref this.pawn, "pawn");
        Scribe_Values.Look(ref this.desiredAgeTicks, "desiredAgeTicks", 0L);
        Scribe_Values.Look(ref this.role, "role");
        Scribe_Values.Look(ref this.triggerRoyalAscent, "triggerRoyalAscent", false);
    }
}

public enum RoyaltyRegenesisStage
{
    NotStarted,
    RulerPrisoners,
    Leaders,
    LowerNobility,
    StellarchNotice,
    StellarchArrival,
    EmperorNotice,
    EmperorArrival,
    Completed,
}

public static class RoyaltyRegenesisDebugActions
{
    [DebugAction("CryoRegenesis", "Start Royalty chain")]
    public static void StartRoyaltyChain()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugStartAt(RoyaltyRegenesisStage.RulerPrisoners);
    }

    [DebugAction("CryoRegenesis", "Start Stellarch arrival")]
    public static void StartStellarchArrival()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugStartAt(RoyaltyRegenesisStage.StellarchArrival);
    }

    [DebugAction("CryoRegenesis", "Start Emperor arrival")]
    public static void StartEmperorArrival()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugStartAt(RoyaltyRegenesisStage.EmperorArrival);
    }

    [DebugAction("CryoRegenesis", "Trigger next Royalty event")]
    public static void TriggerNextRoyaltyEvent()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugTriggerNextEvent();
    }
}

public class QuestNode_StartRoyaltyRegenesisChain : QuestNode
{
    protected override bool TestRunInt(Slate slate)
    {
        return ModsConfig.RoyaltyActive;
    }

    protected override void RunInt()
    {
        Current.Game?.GetComponent<RoyaltyRegenesisQuestSystem>()
            ?.DebugStartAt(RoyaltyRegenesisStage.RulerPrisoners);

        QuestPart_QuestEnd endPart = new QuestPart_QuestEnd
        {
            inSignal = QuestGen.quest.InitiateSignal,
            outcome = QuestEndOutcome.Success,
            sendLetter = false,
        };
        QuestGen.quest.AddPart(endPart);
    }
}
