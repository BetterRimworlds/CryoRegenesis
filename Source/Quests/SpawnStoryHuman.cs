/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 *
 * Adapted from BetterRimworlds.Stargate.Utilities.SpawnStoryHuman.
 */

using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Creates fully scripted story humans or lightly-configured dynamic guests.
/// Used by royalty quest content that needs pawns who never exist as world pawns
/// (the Emperor, imperial wives, etc.).
public static class SpawnStoryHuman
{
    public enum StoryGuestStatus
    {
        None = -1,
        Guest = 0,
        Prisoner = 1,
        Slave = 2
    }

    public sealed class Options
    {
        public Faction Faction = Faction.OfPlayer;

        public StoryGuestStatus StoryGuestStatus = StoryGuestStatus.None;

        /// Used when GuestStatus is Guest or Prisoner. Defaults to Faction.OfPlayer.
        public Faction HostFaction = Faction.OfPlayer;

        /// If greater than zero, marks the pawn guilty for this many ticks.
        public int GuiltyTicks = 0;

        /// Clears generated apparel, equipment, inventory, and relations.
        public bool StripGeneratedGear = false;

        /// Optional royal title to apply after generation (defName, e.g. "Emperor").
        public string RoyalTitleDefName;

        /// When false, generation skips auto-relations so callers can marry pawns deliberately.
        public bool CanGeneratePawnRelations = true;
    }

    public sealed class Definition
    {
        public string FirstName;
        public string NickName;
        public string LastName;

        public string ChildhoodBackstory;
        public string AdulthoodBackstory;

        public BodyTypeDef BodyType = BodyTypeDefOf.Male;
#if RIMWORLD12 || RIMWORLD13
        public CrownType CrownType = CrownType.Average;
#endif
        public string HairDefName;
        public Color HairColor = Color.white;
        public float Melanin = 0.5f;
        public string BirthLastName;

        public long AgeBiologicalTicks;
        public long BirthAbsTicks = -1;

        public readonly List<string> Traits = new List<string>();
        public readonly List<SkillSpec> Skills = new List<SkillSpec>();
    }

    public sealed class SkillSpec
    {
        public SkillDef SkillDef;
        public int Level;
        public Passion Passion;
        public float XpSinceLastLevel;

        public SkillSpec(SkillDef skillDef, int level, Passion passion = Passion.None, float xpSinceLastLevel = 0f)
        {
            SkillDef = skillDef;
            Level = level;
            Passion = passion;
            XpSinceLastLevel = xpSinceLastLevel;
        }
    }

    /// Fully scripted pawn from a <see cref="Definition"/> (names, backstories, skills).
    public static Pawn Create(Definition definition, Options options = null)
    {
        if (definition == null)
        {
            Log.Error("[CryoRegenesis] SpawnStoryHuman.Create called with a null definition.");
            return null;
        }

        options ??= new Options();
        Faction faction = options.Faction ?? Faction.OfPlayer;
        PawnKindDef kind = faction.def?.basicMemberKind ?? PawnKindDefOf.Colonist;

        Pawn pawn = GenerateRaw(kind, faction, gender: null, biologicalAgeYears: null, options);
        ApplyDefinition(pawn, definition);
        ApplyOptions(pawn, options);
        return pawn;
    }

    /// Dynamic guest / noble / guard generation. Prefer this for royalty quest NPCs
    /// that do not need a hand-authored name and skill sheet.
    public static Pawn Generate(
        PawnKindDef kind,
        Faction faction,
        Gender? gender = null,
        float? biologicalAgeYears = null,
        Options options = null)
    {
        options ??= new Options();
        options.Faction = faction ?? options.Faction;

        PawnKindDef resolvedKind = kind
            ?? faction?.def?.basicMemberKind
            ?? PawnKindDefOf.Colonist;

        Pawn pawn = GenerateRaw(resolvedKind, options.Faction ?? Faction.OfPlayer, gender, biologicalAgeYears, options);
        ApplyOptions(pawn, options);
        return pawn;
    }

    /// Convenience: generate an imperial humanlike of the given gender and age years.
    public static Pawn GenerateImperial(
        Faction empire,
        Gender gender,
        float biologicalAgeYears,
        string royalTitleDefName = null,
        PawnKindDef kind = null,
        bool guest = true)
    {
        Options options = new Options
        {
            Faction = empire,
            HostFaction = Faction.OfPlayer,
            StoryGuestStatus = guest ? StoryGuestStatus.Guest : StoryGuestStatus.None,
            CanGeneratePawnRelations = false,
            RoyalTitleDefName = royalTitleDefName,
        };

        return Generate(kind, empire, gender, biologicalAgeYears, options);
    }

    private static Pawn GenerateRaw(
        PawnKindDef kind,
        Faction faction,
        Gender? gender,
        float? biologicalAgeYears,
        Options options)
    {
        PawnGenerationRequest request = new PawnGenerationRequest(kind, faction);
        request.ForceGenerateNewPawn = true;
        request.CanGeneratePawnRelations = options?.CanGeneratePawnRelations ?? true;
        request.AllowDead = false;
        request.AllowDowned = false;

        if (gender.HasValue)
        {
            request.FixedGender = gender;
        }

        if (biologicalAgeYears.HasValue)
        {
            request.FixedBiologicalAge = biologicalAgeYears.Value;
            request.FixedChronologicalAge = biologicalAgeYears.Value;
        }

        TrySetFixedTitle(ref request, options?.RoyalTitleDefName, faction);

        return PawnGenerator.GeneratePawn(request);
    }

    /// FixedTitle is available on Royalty installs; set by reflection when present.
    /// PawnGenerationRequest is a struct, so PropertyInfo.SetValue must mutate a
    /// boxed copy and write it back — otherwise the caller's request is unchanged.
    private static void TrySetFixedTitle(ref PawnGenerationRequest request, string titleDefName, Faction faction)
    {
        if (titleDefName.NullOrEmpty())
        {
            return;
        }

        RoyalTitleDef title = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(titleDefName);
        if (title == null)
        {
            return;
        }

        PropertyInfo prop = typeof(PawnGenerationRequest).GetProperty("FixedTitle");
        if (prop != null && prop.CanWrite)
        {
            object boxed = request;
            prop.SetValue(boxed, title, null);
            request = (PawnGenerationRequest)boxed;
        }
    }

    public static void TrySetRoyalTitle(Pawn pawn, Faction faction, string titleDefName)
    {
        if (pawn == null || faction == null || titleDefName.NullOrEmpty())
        {
            return;
        }

        RoyalTitleDef title = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(titleDefName);
        if (title == null || pawn.royalty == null)
        {
            return;
        }

        try
        {
            pawn.royalty.SetTitle(faction, title, grantRewards: false, rewardsOnlyForNewestTitle: false, sendLetter: false);
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[CryoRegenesis] Failed to set royal title {titleDefName} for {pawn.LabelShort}: {ex.Message}");
        }
    }

    public static void Marry(Pawn husband, Pawn wife)
    {
        if (husband?.relations == null || wife?.relations == null)
        {
            return;
        }

        if (!husband.relations.DirectRelationExists(PawnRelationDefOf.Spouse, wife))
        {
            husband.relations.AddDirectRelation(PawnRelationDefOf.Spouse, wife);
        }
    }

    private static void ApplyDefinition(Pawn pawn, Definition definition)
    {
        if (pawn == null)
        {
            return;
        }

        if (!definition.FirstName.NullOrEmpty() || !definition.LastName.NullOrEmpty())
        {
            pawn.Name = new NameTriple(
                definition.FirstName ?? string.Empty,
                definition.NickName ?? definition.FirstName ?? string.Empty,
                definition.LastName ?? string.Empty);
        }

        ApplyStory(pawn, definition);
        ApplyAge(pawn, definition);
        ApplySkills(pawn, definition);
    }

    private static void ApplyStory(Pawn pawn, Definition definition)
    {
        if (pawn.story == null)
        {
            return;
        }

        ApplyBackstories(pawn, definition);

        if (definition.BodyType != null)
        {
            pawn.story.bodyType = definition.BodyType;
        }

#if RIMWORLD12 || RIMWORLD13
        pawn.story.crownType = definition.CrownType;
#endif

        if (!definition.HairDefName.NullOrEmpty())
        {
            HairDef hairDef = DefDatabase<HairDef>.GetNamedSilentFail(definition.HairDefName);
            if (hairDef != null)
            {
                pawn.story.hairDef = hairDef;
            }
            else
            {
                Log.Warning("[CryoRegenesis] HairDef not found: " + definition.HairDefName);
            }
        }

#if RIMWORLD12 || RIMWORLD13
        pawn.story.hairColor = definition.HairColor;
        pawn.story.melanin = definition.Melanin;
#else
        pawn.story.HairColor = definition.HairColor;
        // Skin color is gene-driven on later versions; melanin override is best-effort only.
        try
        {
            PropertyInfo melanin = pawn.story.GetType().GetProperty("melanin")
                ?? pawn.story.GetType().GetProperty("Melanin");
            melanin?.SetValue(pawn.story, definition.Melanin, null);
        }
        catch
        {
            // ignore — cosmetics only
        }
#endif

        if (pawn.story.traits != null)
        {
            // Rebuild the list silently, then invalidate once. GainTrait per trait
            // would notify N times; Clear alone never notifies.
            pawn.story.traits.allTraits.Clear();
            foreach (string traitDefName in definition.Traits)
            {
                AddTraitIfFound(pawn, traitDefName);
            }
            NotifyTraitsRebuilt(pawn);
        }

#if RIMWORLD12 || RIMWORLD13
        if (!definition.BirthLastName.NullOrEmpty())
        {
            pawn.story.birthLastName = definition.BirthLastName;
        }
#endif
    }

    private static void ApplyBackstories(Pawn pawn, Definition definition)
    {
#if RIMWORLD12 || RIMWORLD13
        if (!definition.ChildhoodBackstory.NullOrEmpty())
        {
            Backstory childhood;
            if (BackstoryDatabase.allBackstories.TryGetValue(definition.ChildhoodBackstory, out childhood))
            {
                pawn.story.childhood = childhood;
            }
            else
            {
                Log.Warning("[CryoRegenesis] Backstory not found: " + definition.ChildhoodBackstory);
            }
        }

        if (!definition.AdulthoodBackstory.NullOrEmpty())
        {
            Backstory adulthood;
            if (BackstoryDatabase.allBackstories.TryGetValue(definition.AdulthoodBackstory, out adulthood))
            {
                pawn.story.adulthood = adulthood;
            }
            else
            {
                Log.Warning("[CryoRegenesis] Backstory not found: " + definition.AdulthoodBackstory);
            }
        }
#else
        if (!definition.ChildhoodBackstory.NullOrEmpty())
        {
            BackstoryDef childhood = DefDatabase<BackstoryDef>.GetNamedSilentFail(definition.ChildhoodBackstory);
            if (childhood != null)
            {
                pawn.story.Childhood = childhood;
            }
            else
            {
                Log.Warning("[CryoRegenesis] BackstoryDef not found: " + definition.ChildhoodBackstory);
            }
        }

        if (!definition.AdulthoodBackstory.NullOrEmpty())
        {
            BackstoryDef adulthood = DefDatabase<BackstoryDef>.GetNamedSilentFail(definition.AdulthoodBackstory);
            if (adulthood != null)
            {
                pawn.story.Adulthood = adulthood;
            }
            else
            {
                Log.Warning("[CryoRegenesis] BackstoryDef not found: " + definition.AdulthoodBackstory);
            }
        }
#endif
    }

    private static void ApplyAge(Pawn pawn, Definition definition)
    {
        if (pawn.ageTracker == null || definition.AgeBiologicalTicks <= 0)
        {
            return;
        }

        pawn.ageTracker.AgeBiologicalTicks = definition.AgeBiologicalTicks;
        if (definition.BirthAbsTicks != 0)
        {
            pawn.ageTracker.BirthAbsTicks = definition.BirthAbsTicks;
        }
    }

    private static void ApplySkills(Pawn pawn, Definition definition)
    {
        if (pawn.skills == null || definition.Skills == null)
        {
            return;
        }

        foreach (SkillSpec skillSpec in definition.Skills)
        {
            SetSkill(pawn, skillSpec);
        }
    }

    private static void ApplyOptions(Pawn pawn, Options options)
    {
        if (pawn == null || options == null)
        {
            return;
        }

        if (options.StripGeneratedGear)
        {
            StripGeneratedGear(pawn);
        }

        ApplyGuestStatus(pawn, options);

        if (options.GuiltyTicks > 0 && pawn.guilt != null)
        {
#if RIMWORLD12
            pawn.guilt.Notify_Guilty();
#else
            pawn.guilt.Notify_Guilty(options.GuiltyTicks);
#endif
        }

        // Apply title after generation in case FixedTitle was unavailable.
        if (!options.RoyalTitleDefName.NullOrEmpty())
        {
            TrySetRoyalTitle(pawn, options.Faction, options.RoyalTitleDefName);
        }
    }

    private static void ApplyGuestStatus(Pawn pawn, Options options)
    {
        if (pawn.guest == null || options.StoryGuestStatus == StoryGuestStatus.None)
        {
            return;
        }

        Faction hostFaction = options.HostFaction ?? Faction.OfPlayer;

#if RIMWORLD12
        switch (options.StoryGuestStatus)
        {
            case StoryGuestStatus.Guest:
                pawn.guest.SetGuestStatus(hostFaction, false);
                return;
            case StoryGuestStatus.Prisoner:
            case StoryGuestStatus.Slave:
                pawn.guest.SetGuestStatus(hostFaction, true);
                pawn.guest.interactionMode = PrisonerInteractionModeDefOf.NoInteraction;
                return;
        }
#else
        pawn.guest.SetGuestStatus(hostFaction, (GuestStatus)options.StoryGuestStatus);
        if (options.StoryGuestStatus == StoryGuestStatus.Prisoner)
        {
#if RIMWORLD13 || RIMWORLD14
            pawn.guest.interactionMode = PrisonerInteractionModeDefOf.NoInteraction;
#else
            pawn.guest.SetNoInteraction();
#endif
        }
#endif
    }

    private static void StripGeneratedGear(Pawn pawn)
    {
        pawn.equipment?.DestroyAllEquipment();
        pawn.apparel?.DestroyAll();
        pawn.inventory?.innerContainer?.ClearAndDestroyContents();
        pawn.relations?.ClearAllRelations();
    }

    /// Adds a trait to the list without firing GainTrait side-effects.
    /// Call <see cref="NotifyTraitsRebuilt"/> once after a batch rebuild.
    private static void AddTraitIfFound(Pawn pawn, string traitDefName)
    {
        if (traitDefName.NullOrEmpty() || pawn?.story?.traits == null)
        {
            return;
        }

        TraitDef traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitDefName);
        if (traitDef == null)
        {
            Log.Warning("[CryoRegenesis] TraitDef not found: " + traitDefName);
            return;
        }

        if (pawn.story.traits.HasTrait(traitDef))
        {
            return;
        }

        Trait trait = new Trait(traitDef);
        trait.pawn = pawn;
        pawn.story.traits.allTraits.Add(trait);
    }

    /// Same cache invalidation GainTrait runs after a trait list change, once per rebuild.
    private static void NotifyTraitsRebuilt(Pawn pawn)
    {
        if (pawn == null)
        {
            return;
        }

        pawn.Notify_DisabledWorkTypesChanged();

        if (pawn.skills != null)
        {
            pawn.skills.Notify_SkillDisablesChanged();
#if RIMWORLD15 || RIMWORLD16
            pawn.skills.DirtyAptitudes();
#endif
        }

        if (!pawn.Dead && pawn.RaceProps.Humanlike && pawn.needs?.mood != null)
        {
            pawn.needs.mood.thoughts.situational.Notify_SituationalThoughtsDirty();
        }

        MeditationFocusTypeAvailabilityCache.ClearFor(pawn);

        // Private on TraitSet; name changed across versions (RecacheTraits vs Cache…).
        if (pawn.story?.traits != null)
        {
            MethodInfo recache = typeof(TraitSet).GetMethod(
                "RecacheTraits",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (recache == null)
            {
                recache = typeof(TraitSet).GetMethod(
                    "CacheAnyTraitHasIngestibleOverrides",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }
            recache?.Invoke(pawn.story.traits, null);
        }

        pawn.needs?.AddOrRemoveNeedsAsAppropriate();
    }

    private static void SetSkill(Pawn pawn, SkillSpec skillSpec)
    {
        if (skillSpec?.SkillDef == null || pawn.skills == null)
        {
            return;
        }

        SkillRecord skill = pawn.skills.GetSkill(skillSpec.SkillDef);
        if (skill == null)
        {
            return;
        }

        skill.Level = skillSpec.Level;
        skill.passion = skillSpec.Passion;
        skill.xpSinceLastLevel = skillSpec.XpSinceLastLevel;
    }
}
