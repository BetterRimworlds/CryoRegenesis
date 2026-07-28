# Architecture — CryoRegenesis source map

Brief guide to every C# source file under `Source/` (excluding `obj/` / `bin/`).

Each section has:

1. A **multitree** of how those files relate (call / ownership / data flow).
2. An **alphabetical** numbered list with one-line descriptions.

Trees use only files in that section (plus a note when work continues in another folder).

---

## Root (`Source/`)

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
│   │       ├── Thought_RegenesisBodyPositivity.cs
│   │       │   └── Thought_DurationBased.cs
│   │       └── Hediff_CryoRegenesisWithdrawal.cs
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
└── Utility
    └── BetterRandom.cs
```

1. `AllowHostedGuestsPatch.cs` — Harmony: float-menu “carry to CryoRegenesis” for guests and valid targets.
2. `BetterRandom.cs` — Small random-helper utilities used across the mod.
3. `CharacterCardAgePatch.cs` — Harmony: shows True Age on the character card when the tracker is present.
4. `Cosmetics.cs` — Chance-based hair-color restoration when a pawn is de-aged.
5. `CryoRegenesis.cs` — Mod entrypoint (Harmony bootstrap, settings UI) and main `Building_CryoRegenesis` casket behavior.
6. `CryoRegenesis.TrueAge.cs` — Partial of the casket: snapshots bio-age on entry and updates the True Age ledger on eject.
7. `CryoRegenesisRecipeInjector.cs` — Injects CryoRegenesis recipes onto humanlike race defs at startup.
8. `Hediff_CryoRegenesisWithdrawal.cs` — Withdrawal hediff applied when regen sedation / cycle effects end.
9. `JobDriver_CarryToCryoRegenesis.cs` — Colonist job: carry a downed / immobile patient into a CryoRegenesis casket.
10. `Recipe_AdministerCryoRegenesisSedation.cs` — Surgery recipe that sedates a pawn and queues them for casket treatment.
11. `RegenesisCycle.cs` — Per-occupant healing / de-aging cycle state machine used by the casket.
12. `RegenesisThoughts.cs` — Grants body-positivity (and related) thoughts after a successful regen eject.
13. `Resurrector.cs` — Dead-body resurrection state machine hosted by the casket.
14. `Settings.cs` — Mod settings (target age, heal-until-healthy, prosthetics, debug) and settings window.
15. `Thought_DurationBased.cs` — Base thought type with explicit duration handling for stacked regen memories.
16. `Thought_LuciferiumSideEffect.cs` — Duration-based thought for Luciferium-related regen side effects.
17. `Thought_RegenesisBodyPositivity.cs` — Stackable body-positivity memory after de-aging.
18. `TrueAgeTracker.cs` — Invisible hediff that tracks True Age (conscious life) and regen-contract flags.

---

## DeadCryoSleep (`Source/DeadCryoSleep/`)

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

## Quests (`Source/Quests/`)

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
├── Quest UI / QuestGen
│   ├── QuestParts.cs                    (status parts + quest factory)
│   └── Nodes.cs                         (no-op QuestScriptDef roots)
│
├── Dev tools
│   └── RoyaltyRegenesisDebugActions.cs  → System (DebugStartAt / next event)
│
└── Shuttle integration (Harmony)
    ├── Patch_CompShuttle_IsAllowed.cs   (who may board regen pickups)
    └── Patch_CompShuttle_SendLaunchedSignals.cs
                                         (same-tick colonist escapee snapshot)
```

1. `Nodes.cs` — No-op QuestGen roots so chain/contract QuestScriptDefs are valid (quests are built in code).
2. `Partners.cs` — Romantic companions who accompany royalty clients (target ages, relation filters).
3. `Patch_CompShuttle_IsAllowed.cs` — Harmony: who may board regen pickup shuttles (clients, relations, Emperor rules).
4. `Patch_CompShuttle_SendLaunchedSignals.cs` — Harmony: snapshot colonist escapees on same-tick Imperial shuttle launch.
5. `QuestParts.cs` — Live Quests-tab status parts plus factory that builds chain/contract `Quest` objects.
6. `RoyaltyEmperor.cs` — Builds the final Imperial party (Emperor, Stellarch, wives, non-regen guards).
7. `RoyaltyRegenesisClient.cs` — Serializable active-contract client record (pawn, target age, role, success latch).
8. `RoyaltyRegenesisDebugActions.cs` — Dev-mode debug actions to jump stages or fire the next quest event.
9. `RoyaltyRegenesisStage.cs` — Campaign stage enum from foreign trials through Emperor completion.
10. `SpawnStoryHuman.cs` — Scripted pawn generator for story humans who are not world pawns (Emperor, wives, …).

---

## Quest system partials (`Source/Quests/System/`)

All of these are partials of one type: `RoyaltyRegenesisQuestSystem` (`GameComponent`).

```
RoyaltyRegenesisQuestSystem
├── Core.cs                fields, public API, ExposeData, Tick, AdvanceCampaign
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
├── Completion.cs          age latches, stage advance, progress text, trust breaks
│   └── Rewards.cs         grant completion caches (uranium / Luciferium / tier loot)
│
└── Helpers.cs             factions, pawn kinds, titles, Royal Ascent unlock
```

1. `ActiveClients.cs` — Active-contract monitoring, multi-wave pickups, and shuttle departure handling.
2. `Completion.cs` — Age-target evaluation, stage advancement, progress text, and trust-break handling.
3. `Contracts.cs` — Stage contract starters, client prep, deadlines, and delivery shuttles.
4. `Core.cs` — Fields, constructor, public API, save/load, tick loop, campaign advance, debug entry points.
5. `Emperor.cs` — Emperor boarding rules, colonist endgames, usurpation, and planetkiller path.
6. `Helpers.cs` — Faction / pawn-kind helpers, royal titles, and Royal Ascent unlock.
7. `Pickup.cs` — Pickup spawn/board, contract finish/fail, and guest-lodger bookkeeping.
8. `Rewards.cs` — Contract completion reward tiers (uranium, Luciferium, random cache).
