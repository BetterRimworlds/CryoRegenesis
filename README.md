# CryoRegenesis: Forever Young

![CryoRegenesis: Live forever!](https://raw.githubusercontent.com/BetterRimworlds/CryoRegenesis/master/CryoRegenesis/About/Preview.png)

With this Glittertech, all of your colonists (and pets, and even enemies, if you're so inclined!) can live forever young!

CryoRegenesis sarcophagi not only restore your pawns to their youthful vigor, they also cure all age-related infirmities as
well as every physical injury!

 * No more aging. Everyone can be in their 20s!
 * Heals bad backs, permanent scars, even dementia, Alzheimer's and bullets to the brain!
 * Heals all diseases. There is no more need to die from the flu!
 * Luciferium need cannot be cured, nor can psychological addictions, though physical
 * Resurrects dead Pawns that 1) have a brain and 2) haven't been left unrefrigerated for more than 1 day.
 * Humanlike pawns now track a separate True Age, preserving total lived time even after de-aging.

## Inspiration: The Goa'uld Sarcophagus

CryoRegenesis is a RimWorld port of the **Goa'uld sarcophagus** from *Stargate* (the motion picture) and *Stargate SG-1*. In
the show, those devices rapidly heal injuries, reverse aging, extend life, and can even resurrect the recently dead — at a
steep psychological and addictive cost. The Tok'ra refuse to use them for that reason.

This mod maps that artifact onto RimWorld's systems: cryptosleep chassis, glittertech nanites, and **Luciferium** as the
Faustian price of coming back from the dead (the body only keeps working if the mechanites keep flowing).

| Goa'uld sarcophagus | CryoRegenesis |
|---|---|
| Heals injury and disease | Heals hediffs, old-age failures, and wounds |
| Extends life / reverses aging | De-ages pawns (and animals) to youthful prime |
| Resurrects the recently dead | Brain intact; corpse unrefrigerated under 1 day |
| Narcotic / addictive side effects | Permanent Luciferium addiction on resurrection |
| Tok'ra refuse it because it "takes the soul" | Expensive glittertech with a lasting cost, not free immortality |
| Body restored, mind altered | Mood highs and "Grateful to be alive!" after revival |

Part of the [Better Rimworlds](https://github.com/BetterRimworlds) collection — Stargate-inspired tech for multi-century
hub-world colonies.

## Supported RimWorld Versions

- RimWorld 1.2
- RimWorld 1.3
- RimWorld 1.4
- RimWorld 1.5
- RimWorld 1.6

## Power Behavior

- Idle CryoRegenesis caskets no longer require power.
- If power fails, the casket falls back to normal cryptosleep behavior until power is restored.

Stock up on Uranium. You'll need a whole lot of it!

![Cargo](https://github.com/BetterRimworlds/CryoRegenesis/assets/1125541/e76d8ca0-2616-44d6-9f7e-f89fa014a633)

Here's our illustrious pup, Cargo! He's been with the colonists 101 years and is 107 years old! He's lived on more Rimworlds
than most humans!

## Resurrection Costs and Consequences

**Resource Requirements:**
Bringing someone back from the dead requires significant resources:
- **150 Uranium** - Powers the nanite reconstruction process
- **500 Gold** - Essential for the advanced circuitry and nanite construction
- **50 Luciferium** - The mechanites are required to jumpstart cellular regeneration

**The Price of Resurrection:**
- All resurrected pawns will gain a **permanent Luciferium addiction**. Death is not without consequence - the mechanites
  that bring them back require continued doses to maintain cellular cohesion.
- This addiction cannot be cured by the sarcophagus (as noted in features above).

## New Mood Effects

**"Grateful to be alive!" (Resurrection only)**
- Humanlike pawns who are resurrected gain a powerful +45 mood buff
- This thought can last anywhere from 1 to 10 years
- Represents the profound psychological impact of near-death experiences
- Prevents post-resurrection mental breaks and helps colonists readjust to life

**"I feel younger!" (Age Reversal)**
- New staged mood buff based on years regenerated:
  - 1-9 years reversed: around +15 mood - "Regenerated"
  - 10-19 years reversed: around +20 mood - "Mildly regenerated"
  - 20-39 years reversed: around +25 mood - "Substantially regenerated"
  - 40-59 years reversed: around +35 mood - "Majorly regenerated"
  - 60+ years reversed: around +40 mood - "Fully regenerated"
- Exact mood impact varies because the thought includes a randomized bonus or penalty
- Duration is variable rather than fixed:
  - Usually 30 to 300 days
  - 60 to 600 days if the pawn was recently resurrected
  - Body Purists can experience even stronger and longer-lasting effects
- Stacks with resurrection mood bonus when applicable
- Strong rejuvenation can make some prisoners recruitable again

### Balance Notes
The combination of resource costs and permanent Luciferium addiction ensures resurrection remains a serious decision rather
than a casual convenience. Plan accordingly and maintain a steady Luciferium supply for your immortal colonists!

## Imperial Regeneration Quests (Royalty)

With the Royalty DLC, word of your CryoRegenesis quickly spreads as rumor across the galaxy! Planetary rulers and, in time,
the Empire itself may send clients by shuttle under fixed return contracts: regress them to the contracted age, keep them
breathing, and **do not recruit them**.

Load finished clients onto pickup shuttles when they hit target age and send them home. Complete enough contracts and the
chain escalates — lower nobility, the Stellarch, and eventually the highest seats of Imperial power.

Death or recruitment under contract shatters trust: progress resets, and patrons will stay away for a long time. Honor the
bargain and the Empire may eventually open paths that most colonies never see.

### Honor

Successful CryoRegenesis of an Empire noble is worth Imperial Honor (favor). A **Bestow Honor** letter lets you choose any
colonist to receive it.

- **Active regen contract:** one offer when the highest-Honor client reaches the target age, regardless of shuttle
  departure or contract success. Several nobles → the **highest calculated Honor** only.
- **No active contract:** any titled Empire member who finishes in a CryoRegenesis casket — an Empire guest, or a colonist
  who already holds an Empire title.
- Honor is favor, not a skipped title ceremony. Freeholder still applies immediately; Knight and above still use the usual
  bestowing ceremony once they have enough Honor.

Years restored become decades (years ÷ 10, rounded; **5.0 years counts as one decade**), then multiplied by rank:

| Title                   | Honor Per Decade |
|-------------------------|------------------|
| Freeholder              | 1×               |
| Yeoman                  | 2×               |
| Acolyte                 | 2.5×             |
| Knight                  | 3×               |
| Praetor                 | 5×               |
| Baron                   | 7×               |
| Count                   | 8×               |
| Duke, Consul, Stellarch | 10×              |

Example: a Knight restored 47 years → 5 decades × 3 = **15 Honor**.

| Emperor ending                                       | Honor |
|------------------------------------------------------|-------|
| Imperial Court (leave with the rejuvenated Emperor)  | 1,000 |
| Consul (default win: Emperor restored, Royal Ascent) | 100   |
| Keep What You Kill (usurpation or assassination)     | 0     |

### Endings

Fulfilling the Imperial chain can unlock a true endgame path off this planet — the Empire does not forget those who keep its
elite Forever Young. How (and whether) that ending plays out depends on the choices you make when the shuttles arrive and
leave.

This entire quest system will take years and years; decades, even. The nobles will have to travel at relativistic speeds
that reach into years, for instance.

There are also… other ways the story can close. We will not spoil them here. RimWorld rewards curiosity, ambition, and the
occasional terrible idea. The caskets keep secrets. So do we.

## Changelog

Last 365 days. For more, see [CHANGELOG.md](CHANGELOG.md).

#### v8.0.0

* **[2026-10-26 11:45:12 +04]** [Quests] Stopped the contract description from recording client readiness.
* **[2026-10-26 11:32:50 +04]** Better rimworld v1.7 support for the DLCs.
* **[2026-10-26 11:23:45 +04]** [Quests] Moved the Royalty debug helpers into their own file.
* **[2026-10-26 11:22:01 +04]** [Quests] Fixed DebugStartAt leaving stale regen contracts on canceled clients.
* **[2026-10-26 11:13:36 +04]** Fixed failed resurrections reporting success and ejecting revived corpses.
* **[2026-10-24 13:16:41 +04]** Upgraded to Rimworld v1.7.
* **[2026-10-23 11:38:21 +04]** [Quests] Fixed missing rewards after the first completed contract.
* **[2026-10-23 11:22:16 +04]** [Quests] Fixed Imperial wives disappearing during assassination escape.
* **[2026-10-23 08:28:27 +04]** [Quests] Fixed lower nobility contracts to select titled nobles.
* **[2026-10-23 06:17:39 +04]** [Quests] Fixed Stellarch contracts to select the Empire leader.
* **[2026-09-15 16:44:31 +04]** [Quests] Offer the contract Honor as soon as the highest-Honor client is ready.
* **[2026-09-15 15:35:07 +04]** [Quests] Fixed Imperial nobles arriving as uncontrolled strangers.
* **[2026-09-14 15:23:38 +04]** [Quests] More tweaks to Nobility giving Honor.
* **[2026-09-13 06:27:24 +04]** [Quests] Doll out Honor for Imperial regenesis.
* **[2026-09-12 23:17:08 +04]** [Quests] Fixed: Faction Leaders are no longer deposed when coming for regenesis.
* **[2026-09-07 01:35:08 +04]** [Quests] Tightens which faction leaders are sent (e.g., not hostile nor Ancients).
* **[2026-09-05 01:16:17 +04]** [Quests] Searches for a world leader among all the factions instead of failing at the first.
* **[2026-09-05 11:48:15 +04]** [Quests] Fixed planetary ruler contracts stalling on a single faction.
* **[2026-07-31 06:07:50 +04]** [Quests] Only credit clients aboard pickup shuttles. (Found by PantoAI)
* **[2026-07-31 05:48:07 +04]** Fixed TrueAge ledger double-counting. (Found by PantoAI)
* **[2026-07-31 05:26:46 +04]** [Quests] Fixed an edge case that could inappropriately mark untreated contracts as done. (Found by CodeAnt)
* **[2026-07-31 05:08:44 +04]** [Quests] Fixed an extreme edge case during contract deadline updates. (Found by CodeAnt)
* **[2026-07-31 05:07:51 +04]** [Quests] Changed the behavior of the Emperor's wives and guards after his assassination.
* **[2026-07-31 03:54:25 +04]** [Quests] Added an initial requirement of 500 Uranium to begin the Regen Quests.
* **[2026-07-30 18:03:29 +04]** [Quests] Added Stargate continuity for Emperor shuttle escapees.
* **[2026-07-28 12:39:36 +04]** Added an ARCHITECTURE.md doc.
* **[2026-07-27 23:23:58 +04]** [Quests] Fixed debug campaign waits at five days to greatly cut down system testing time.
* **[2026-07-27 23:11:56 +04]** [Quests] Split the Royalty quest system into focused modules under Quests/.
* **[2026-07-27 17:34:49 +04]** [Quests] Prune dead guests from the evacuation manifest in Assassin end game (found by OpenAI Codex).
* **[2026-07-27 16:25:10 +04]** [Quests] Hunted Assassin mark now jumps to any Knight+ who kills the Marked One.
* **[2026-07-27 16:00:45 +04]** [Quests] Added the Emperor "Keep What You Kill" assassination endgame.
* **[2026-07-25 17:06:03 +04]** [Quests] Completed the Emperor Dies "Planet Killer" end game.
* **[2026-07-25 15:21:34 +04]** [Quests] Completed the Emperor "Keep What You Kill" Diplomatic end game.
* **[2026-07-25 12:18:49 +04]** [Quests] Completed the Emperor standard Regen end game.
* **[2026-07-25 07:37:08 +04]** [Quests] Added a new victory ending when colonists leave on the Imperial shuttle.
* **[2026-07-24 11:34:09 +04]** [Quests] Show royal titles and wife roles on regen contract client lists.
* **[2026-07-24 11:33:11 +04]** [Quests] Built the final Emperor CryoRegenesis party with Stellarch, wives, and guards.
* **[2026-07-22 04:13:21 +04]** [Quests] Immediately dispatch the Emperor (don't wait) once the Stellarch has been regenerated.
* **[2026-07-19 10:20:04 +04]** [Quests] Only direct relations (not exes) of regen-quest nobility may board pickup shuttles.
* **[2026-07-19 10:16:25 +04]** [Quests] Increased contract uranium rewards to 500-600 per treated client.
* **[2026-07-26 09:20:03 +04]** [Quests] Start Royal Ascent via QuestUtility instead of reflection (by CodeRabbit).
* **[2026-07-19 08:03:07 +04]** [Quests] Regen contracts use hospitality multi-wave pickups for ready clients.
* **[2026-07-18 17:04:51 +04]** [Quests] Now brings lovers, fiancés, and spouses along on regen contracts.
* **[2026-07-18 16:53:20 +04]** [Quests] More than 1 noble will arrive now [2-10].
* **[2026-07-11 04:11:28 +04]** [Quests] Fixed Royalty regen quests to complete correctly.
* **[2026-07-11 03:26:55 +04]** [Quests] Spawn the leaders from the World Pawns list directly.
* **[2026-07-11 02:48:02 +04]** [Quests] Added Rewards for the ReGen quests.
* **[2026-07-11 01:33:19 +04]** [Quests] Fixed Regen Quest rulers arriving as uncontrollable visitors.
* **[2026-07-11 00:10:47 +04]** [Quests] Removed the worthless DoRecruit Harmony patch.
* **[2026-07-10 23:22:33 +04]** [Quests] Reworked Royalty CryoRegenesis contracts around parked shuttles and banked completion.
* **[2026-07-10 10:05:12 +04]** [Quests] Display the Regenesis quest contract's target age.
* **[2026-07-10 09:18:44 +04]** [Quests] Fixed the no-recruitment of guests procedure for Rimworld v1.2.
* **[2026-07-10 08:42:05 +04]** [Quests] Stopped body-positivity gratitude from recruiting contract clients.
* **[2026-07-10 08:18:17 +04]** [Quests] Added the Royalty CryoRegenesis quest chain foundation.
* **[2026-07-09 05:41:08 +04]** [Quests] Taught CryoRegenesis to honor True Age contract target ages.
* **[2026-07-09 04:27:33 +04]** [Quests] Extended TrueAgeTracker for contracted target ages.


#### v7.1.0

* **[2026-09-13 05:40:46 EEST]** Stopped CryoRegenesis from healing Anomaly entities and void states.
* **[2026-09-13 05:40:21 EEST]** Stopped CryoRegenesis from healing Biotech gene needs and birth.
* **[2026-10-21 03:35:01 EEST]** Moved the age line cover on the character card to fit the 1.6 layout.
* **[2026-08-05 07:35:42 EEST]** Added better Rimworld v1.6 support.

#### v7.0.0

* **[2026-07-23 11:33:33 EEST]** Fixed simple prosthetics remaining after regeneration.
* **[2026-07-19 09:12:20 CDT]** Repositioned the True Age to the right.
* **[2026-07-18 14:11:59 CDT]** Body positivity thoughts now stack and scale duration from lifetime True Age years erased.
* **[2026-07-12 21:11:16 CDT]** Marked the CryoRegenesis casket as player-ejectable.
* **[2026-07-12 20:21:20 CDT]** Fixed Carry-to-CryoRegenesis never appearing or working for downed, sedated, and guest pawns.
* **[2026-07-10 22:12:38 COT]** [m] Removed the watch for building as it is no longer useful in The Age of AI.
* **[2026-07-09 09:16:13 COT]** Updated the README with Stargate references.
* **[2026-07-09 02:46:37 COT]** Fixed a regression where pawns with Bionic or Archotech legs could not be properly downed.
* **[2026-07-09 02:39:04 COT]** Pawns now cannot be attempted to be carried to a CryoRegenesis casket if none are available.
* **[2026-07-09 02:36:32 COT]** Removed the Heal Not Bad HeDiffs mechanism completely.
* **[2026-07-08 09:40:26 COT]** Fixed the instant ejection of non-Human humanlikes (xenotypes, Empire pawns, modded races) from CryoRegenesis.
* **[2026-07-07 19:17:47 COT]** Added animal support for CryoRegenesis carry.
* **[2026-07-07 19:17:17 COT]** All downed humanlikes can now be carried to CryoRegenesis caskets.
* **[2026-07-07 19:10:52 COT]** Added a sedate-and-carry pipeline for placing pawns into CryoRegenesis caskets via the Operations tab.
* **[2026-07-07 19:10:03 COT]** Added the missing True Age def.
* **[2026-07-02 12:46:22 COT]** Added support for taking Royalty guests and prisoners to the CryoRegenesis casket.
