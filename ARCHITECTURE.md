# Architecture — CryoRegenesis source map

Brief guide to every C# source file under `Source/` (excluding `obj/` / `bin/`).

Each section has:

1. A **multitree** of how those files relate (call / ownership / data flow).
2. An **alphabetical** numbered list with one-line descriptions.

Trees use only files in that section (plus a note when work continues in another folder).

---

## Root (`Source/`) — 17 root files, 3,135 lines

`Source/` contains 49 C# files and 13,251 lines in total. Counts include comments and blank lines; `obj/` and `bin/` are excluded.

| Directory                    | Files | Lines |
|-----------------------------:|------:|------:|
| `Source/` (root-level files) |    17 | 3,135 |
| `Source/DeadCryoSleep/`      |     5 |   267 |
| `Source/Quests/`             |    27 | 9,849 |
| ↳ `Source/Quests/System/`    |    11 | 7,481 |
| **Total** | **49** | **13,251** |

`System/` is included in `Source/Quests/`, so the total does not double-count it.

```
CryoRegenesis (mod)
├── Bootstrap
│   ├── CryoRegenesis.cs                 (Mod: Harmony, settings shell)
│   └── Settings.cs
│
├── Building_CryoRegenesis
│   ├── CryoRegenesis.cs                 (host: tick, accept, eject, comps)
│   ├── CryoRegenesis.TrueAge.cs         (partial: bio-age snapshot / ledger on eject)
│   │
│   ├── Living occupant
│   │   ├── RegenesisCycle.cs            (heal + de-age state machine)
│   │   ├── Cosmetics.cs                 (hair restore on de-age)
│   │   ├── TrueAgeTracker.cs            (True Age hediff + DefOf)
│   │   └── Eject side effects
│   │       ├── RegenesisThoughts.cs
│   │       └── Thought_RegenesisBodyPositivity.cs
│   │           └── Thought_DurationBased.cs
│   │
│   └── Corpse occupant (resurrection)
│       ├── Resurrector.cs               (fuel stages → resurrect)
│       ├── Thought_LuciferiumSideEffect.cs
│       │   └── Thought_DurationBased.cs
│       └── (corpse intake: see DeadCryoSleep/)
│
├── Living patient intake
│   ├── CryoRegenesisRecipeInjector.cs   (register surgery on races)
│   ├── Recipe_AdministerCryoRegenesisSedation.cs
│   ├── AllowHostedGuestsPatch.cs        (float menu: carry to casket)
│   └── JobDriver_CarryToCryoRegenesis.cs
│
├── UI
│   └── CharacterCardAgePatch.cs         (show True Age on character card)
│
├── Utility
│   └── BetterRandom.cs
│
└── Subdirectories
    ├── DeadCryoSleep/                   (5 files, 267 lines — see DeadCryoSleep/)
    └── Quests/                          (Royalty Regen Quests; 27 files, 9,849 lines — see Regen Quests/)
        └── System/                      (11 files, 7,481 lines — see Quest system partials/)
```

1. `AllowHostedGuestsPatch.cs` — Harmony (RW 1.2–1.5) / float-menu provider (RW 1.6+): “carry to CryoRegenesis” for guests and valid targets.
2. `BetterRandom.cs` — Small random-helper utilities used across the mod.
3. `CharacterCardAgePatch.cs` — Harmony: shows True Age on the character card when the tracker is present.
4. `Cosmetics.cs` — Chance-based hair-color restoration when a pawn is de-aged.
5. `CryoRegenesis.cs` — Mod entrypoint (Harmony bootstrap, settings UI) and main `Building_CryoRegenesis` casket behavior.
6. `CryoRegenesis.TrueAge.cs` — Partial of the casket: snapshots bio-age on entry and updates the True Age ledger on eject.
7. `CryoRegenesisRecipeInjector.cs` — Injects CryoRegenesis recipes onto humanlike race defs at startup.
8. `JobDriver_CarryToCryoRegenesis.cs` — Colonist job: carry a downed / immobile patient into a CryoRegenesis casket.
9. `Recipe_AdministerCryoRegenesisSedation.cs` — Surgery recipe that sedates a pawn and queues them for casket treatment.
10. `RegenesisCycle.cs` — Per-occupant healing / de-aging cycle state machine used by the casket.
11. `RegenesisThoughts.cs` — Grants body-positivity (and related) thoughts after a successful regen eject.
12. `Resurrector.cs` — Dead-body resurrection state machine hosted by the casket.
13. `Settings.cs` — Mod settings (target age, heal-until-healthy, prosthetics, debug) and settings window.
14. `Thought_DurationBased.cs` — Base thought type with explicit duration handling for stacked regen memories.
15. `Thought_LuciferiumSideEffect.cs` — Duration-based thought for Luciferium-related regen side effects.
16. `Thought_RegenesisBodyPositivity.cs` — Stackable body-positivity memory after de-aging.
17. `TrueAgeTracker.cs` — Invisible hediff that tracks True Age (conscious life) and regen-contract flags.

---

## DeadCryoSleep (`Source/DeadCryoSleep/`) — 5 files, 267 lines

Corpse-haul designator / workgiver so colonists can put bodies into CryoRegenesis caskets (Frontier Developments–style dead cryptosleep flow). Continues into Root: `Building_CryoRegenesis` + `Resurrector.cs`.

```
DeadCryoSleep (corpse → casket)
├── Register designator
│   └── Patch_ReverseDesignatorDatabase.cs
│       └── Designator_HaulCryoRegenesis.cs
│           └── DeadCryosleepDefOf.cs
│
└── Execute haul
    └── WorkGiver_HaulCryoRegenesis.cs
        ├── DeadCryosleepDefOf.cs
        └── JobDriver_HaulCorpseToCryptosleepCasket.cs
            └── Building_CryoRegenesis.TryAcceptThing   (Root / CryoRegenesis.cs)
```

1. `DeadCryosleepDefOf.cs` — DefOf bindings for the haul job / designator defs.
2. `Designator_HaulCryoRegenesis.cs` — Player designator to mark corpses for haul into a casket.
3. `JobDriver_HaulCorpseToCryptosleepCasket.cs` — JobDriver that walks a corpse into a CryoRegenesis casket.
4. `Patch_ReverseDesignatorDatabase.cs` — Harmony: registers the haul designator in the reverse-designator database.
5. `WorkGiver_HaulCryoRegenesis.cs` — Workgiver that assigns colonists the corpse-haul job.

---

## Regen Quests (`Source/Quests/`) — 27 files, 9,849 lines (16 here + 11 in `System/`)

Royalty / planetary-ruler CryoRegenesis contract chain (requires Royalty DLC). Campaign brain lives in `System/` (separate section below).

```
Royalty Regenesis chain
├── Campaign brain
│   └── System/                          (RoyaltyRegenesisQuestSystem partials)
│
├── Data
│   ├── RoyaltyRegenesisClient.cs        (active contract client record)
│   └── RoyaltyRegenesisStage.cs         (campaign stage enum)
│
├── Contract parties
│   ├── Partners.cs                      (romantic companions + target ages)
│   ├── RoyaltyEmperor.cs                (final Imperial party composition)
│   │   └── SpawnStoryHuman.cs           (scripted non–world-pawn humans)
│   └── SpawnStoryHuman.cs               (also used outside Emperor build)
│
├── Honor
│   ├── HonorGrant.cs                    (Honor amounts + title multipliers)
│   └── ChoiceLetter_BestowHonor.cs      (player chooses the Honor recipient)
│
├── Hunted Assassin
│   ├── HuntedAssassinDefOf.cs           (mark hediff DefOf)
│   ├── HuntedAssassinUtility.cs         (apply / query / mark inheritance)
│   └── Kill patches
│       ├── Patch_Pawn_Kill_EmperorAssassin.cs
│       └── Patch_Pawn_Kill_HuntedAssassin.cs
│
├── Quest UI / QuestGen
│   ├── QuestParts.cs                    (status parts + quest factory)
│   └── Nodes.cs                         (no-op QuestScriptDef roots)
│
├── Dev tools
│   └── RoyaltyRegenesisDebugActions.cs  → System/Debug.cs (DebugStartAt / next event)
│
└── Shuttle integration (Harmony)
    ├── Patch_CompShuttle_IsAllowed.cs   (who may board regen pickups)
    └── Patch_CompShuttle_SendLaunchedSignals.cs
                                         (same-tick colonist escapee snapshot)
```

1. `ChoiceLetter_BestowHonor.cs` — Choice letter that lets the player pick which colonist receives Imperial Honor.
2. `HonorGrant.cs` — Honor calculation: years regressed to decades, then title multipliers.
3. `HuntedAssassinDefOf.cs` — DefOf binding for the Hunted Assassin mark hediff.
4. `HuntedAssassinUtility.cs` — Applies, queries, and transfers the Hunted Assassin mark.
5. `Nodes.cs` — No-op QuestGen roots so chain/contract QuestScriptDefs are valid (quests are built in code).
6. `Partners.cs` — Romantic companions who accompany royalty clients (target ages, relation filters).
7. `Patch_CompShuttle_IsAllowed.cs` — Harmony: who may board regen pickup shuttles (clients, relations, Emperor rules).
8. `Patch_CompShuttle_SendLaunchedSignals.cs` — Harmony: snapshot colonist escapees on same-tick Imperial shuttle launch.
9. `Patch_Pawn_Kill_EmperorAssassin.cs` — Harmony: records the Emperor's killer before the damage context is lost.
10. `Patch_Pawn_Kill_HuntedAssassin.cs` — Harmony: transfers the mark from a slain Marked One to a worthy killer.
11. `QuestParts.cs` — Live Quests-tab status parts plus factory that builds chain/contract `Quest` objects.
12. `RoyaltyEmperor.cs` — Builds the final Imperial party (Emperor, Stellarch, wives, non-regen guards).
13. `RoyaltyRegenesisClient.cs` — Serializable active-contract client record (pawn, target age, role, one-time completion flag).
14. `RoyaltyRegenesisDebugActions.cs` — Dev-mode debug actions to jump stages or fire the next quest event.
15. `RoyaltyRegenesisStage.cs` — Campaign stage enum from foreign trials through Emperor completion.
16. `SpawnStoryHuman.cs` — Scripted pawn generator for story humans who are not world pawns (Emperor, wives, …).

---

## Quest system partials (`Source/Quests/System/`) — 11 files, 7,481 lines

All of these are partials of one type: `RoyaltyRegenesisQuestSystem` (`GameComponent`).

```
RoyaltyRegenesisQuestSystem
├── Core.cs                fields, public API, ExposeData, Tick, AdvanceCampaign
├── Debug.cs               DebugStartAt, next event, debug Honor letter
│
├── Contracts.cs           start stage contracts, prep clients, deliver
│   └── (uses Partners / RoyaltyEmperor / SpawnStoryHuman from parent folder)
│
├── ActiveClients.cs       monitor ages, multi-wave pickup, departure handling
│
├── Pickup.cs              spawn/board pickup shuttle, finish/fail, lodgers
│
├── Emperor.cs             Imperial boarding, colonist endgames, usurpation, planetkiller
│   └── (hooked by Patch_CompShuttle_* in parent folder)
│
├── Completion.cs          age-target completion flags, stage advance, progress text, trust breaks
│   └── Rewards.cs         grant completion caches (uranium / Luciferium / tier loot)
│
├── Honor.cs               Honor offer timing, selection, and fixed rewards
│
├── StargateShuttleBuffer.cs  optional Stargate handoff for Imperial evacuees
│
└── Helpers.cs             factions, pawn kinds, titles, Royal Ascent unlock
```

1. `ActiveClients.cs` — Active-contract monitoring, multi-wave pickups, and shuttle departure handling.
2. `Completion.cs` — Age-target evaluation, stage advancement, progress text, and trust-break handling.
3. `Contracts.cs` — Stage contract starters, client prep, deadlines, and delivery shuttles.
4. `Core.cs` — Fields, constructor, public API, save/load, tick loop, and campaign advance.
5. `Debug.cs` — Debug-only helpers: jump to a stage, fire the next event, or send a test Honor letter.
6. `Emperor.cs` — Emperor boarding rules, colonist endgames, usurpation, and planetkiller path.
7. `Helpers.cs` — Faction / pawn-kind helpers, royal titles, and Royal Ascent unlock.
8. `Honor.cs` — Honor offer timing, recipient selection, and fixed endgame rewards.
9. `Pickup.cs` — Pickup spawn/board, contract finish/fail, and guest-lodger bookkeeping.
10. `Rewards.cs` — Contract completion reward tiers (uranium, Luciferium, random cache).
11. `StargateShuttleBuffer.cs` — Optional Stargate handoff for free colonists leaving with the Emperor.
