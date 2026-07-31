# BetterRimworlds/CryoRegenesis ChangeLog

## v7.0.0

* **[2026-07-31 04:26:38 EEST]** [Quests] Fixed an edge case that could complete untreated contracts.
* **[2026-07-31 04:08:35 EEST]** [Quests] Fixed null clients during contract deadline updates.
* **[2026-07-31 04:07:37 EEST]** [Quests] Fixed Imperial survivors during Emperor assassination escapes.
* **[2026-07-30 17:02:39 EEST]** [Quests] Added Stargate continuity for Emperor shuttle escapees.
* **[2026-07-27 22:21:04 EEST]** [Quests] Fixed debug campaign waits at five days to greatly cut down system testing time.
* **[2026-07-27 16:34:16 EEST]** [Quests] Prune dead guests from the evacuation manifest in Assassin end game (found by OpenAI Codex).
* **[2026-07-27 15:25:00 EEST]** [Quests] Hunted Assassin mark now jumps to any Knight+ who kills the Marked One.
* **[2026-07-27 15:00:10 EEST]** [Quests] Added the Emperor "Keep What You Kill" assassination endgame.
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

## v6.0.0

* **[2026-06-29 05:24:28 COT]** [m] Simple README and About.xml improvements.
* **[2026-06-28 19:40:41 COT]** Moved the True Age below the Royalty indicator.
* **[2026-06-28 17:13:38 COT]** Greatly simplified the csproj.

## v6.1.0

* **[2026-05-28 22:26:31 COT]** Added cryosleep counter as well to the new True Age popup.
* **[2026-05-28 18:09:22 COT]** Fixed Colonist despawning / lost forever when entering an unpowered CryoRegen pod.

## v6.0.0

* **[2026-05-25 18:42:17 COT]** Updated the README for v6.0.0.
* **[2026-05-25 12:43:28 COT]** Completely reimplemented the True Age display to not use global UI widgets.
* **[2026-05-25 12:39:21 COT]** Increased the research cost.
* **[2026-05-25 10:28:48 COT]** Falls back to normal cryptosleep casket when power fails.
* **[2026-05-25 10:16:42 COT]** An idle cryoregenesis casket no longer needs power.
* **[2026-05-25 10:12:55 COT]** Split the actual regenesis code out of the CroRegenesis god class.
* **[2026-05-25 09:27:53 COT]** Added a True Age mechanism for tracking total Living Time.
* **[2026-05-25 09:08:39 COT]** Refactored out Cosmetics, Resurrection, and Happy Thoughts from the CryoRegenesis god class.

## v5.1.0

* **[2026-05-05 11:04:27 COT]** Translated into many different languages.
* **[2026-04-29 21:03:02 CDT]** Greatly enhanced the Regenesis High.
* **[2026-04-29 07:57:17 CDT]** Added a Grateful To Be Alive extended Thought upon resurrection.
* **[2026-04-29 07:04:03 CDT]** Fixed the thought stage calculation for the age regression mood boost.
* **[2026-04-29 07:00:49 CDT]** Now freezes the corpse's rotting state preservation during resurrection.
* **[2026-04-29 06:55:59 CDT]** Fixed Ejection to properly handle prisoners
* **[2026-04-29 06:52:14 CDT]** Added BetterRandom utility class for deterministic random number generation

## v5.0.0: Resurrection Improvements

* **[2026-04-29 05:06:26 CDT]** Fail the build if any of the Rimworld versions fail to compile.
* **[2025-07-22 23:47:41 ART]** Added happy thoughts about being regenerated.
* **[2025-07-22 06:45:26 ART]** Added a "Grateful to be alive!" thought when pawns are resurrected.
* **[2025-07-22 06:28:00 ART]** Added Luciferium addiction to resurrected pawns.
* **[2025-07-22 06:05:47 ART]** Cause resurrected pawns to be under anesthetic on revival.
* **[2025-07-22 05:58:41 ART]** Store the pawn's original age for use later.
* **[2025-07-22 05:45:45 ART]** Fixed the resurrection system.
* **[2025-07-14 09:00:11 ART]** Fixed the requirements gathering for resurrection.
* **[2025-07-11 20:25:25 ART]** Added support for Rimworld v1.6.
* **[2025-07-11 20:01:11 ART]** Migrated to a modern dotnet SDK project.

## v4.1.0: .NET v9.0 and C# v10.0.

* **[2025-03-14 15:52:36 COT]** Rearchitected the files to the BetterRimworlds standard layout.
* **[2025-03-06 07:27:19 COT]** Ported to .NET v9.0 and C# v10.0.

## v4.0.0

* **[2024-05-13 17:18:40 COT]** Stored the Resurrection fuel and progress in the savegame.
* **[2024-05-13 09:14:16 COT]** [m] Mild cosmetic fix.
* **[2024-05-13 09:08:19 COT]** Added a resurrection demo video.
* **[2024-05-13 08:47:12 COT]** Code cleanup.
* **[2024-05-12 13:53:08 COT]** Implemented the resurrection of corpses.
* **[2024-03-15 01:30:19 CST]** Version 3.1.0.
* **[2024-03-15 01:16:24 CST]** Cured the CyroRegenesis Quantum Anomaly that broke when guests were put in.
* **[2024-03-15 00:47:43 CST]** Added optional code (enabled by default) to not heal any hediffs that are not marked as "bad".
* **[2024-03-15 00:46:43 CST]** Made the CryoRegenesis flickable (turns it into a normal cryocasket).
* **[2024-03-15 00:44:32 CST]** Upgraded to Rimworld v1.5.
* **[2024-03-15 00:44:12 CST]** Majorly refactored the deploy script.
* **[2024-03-09 11:46:39 CST]** Update README.md
* **[2023-07-29 01:43:18 GST]** Fixed a bug that prohibited animals from being put in the cryocasket.

## v3.0.0

* **[2023-05-22 10:31:53 GST]** Block non-colonist humans from being placed in the casket.
* **[2023-05-22 09:47:45 GST]** Added functionality to never heal implants.
* **[2023-05-22 09:47:24 GST]** Added an option to not try to heal anything that isn't tendable.
* **[2023-05-22 09:44:19 GST]** Added a new option for the target age of Human pawns.
* **[2023-05-22 09:43:34 GST]** Added a new option to only regen until the pawn is healed.
* **[2023-05-22 09:42:55 GST]** Enabled the new optional debug messaging system.
* **[2023-05-22 09:38:56 GST]** [m] Rearranged the SpawnSetup method.
* **[2023-05-22 09:32:17 GST]** Added Mod Settings.
* **[2023-05-22 09:30:13 GST]** Added a proper Mod class.
* **[2023-05-21 19:41:18 GST]** Update README.md: Added info about Cargo
* **[2023-03-26 13:41:29 CEST]** Re-added more hair colors.

## v2.6.0

* **[2023-01-13 06:35:04 EST]** Fixed a bug in v1.4 support.
* **[2023-01-13 06:23:38 EST]** Version 2.6.0.
* **[2023-01-13 06:16:45 EST]** Upgraded to Rimworld v1.4.3580.
* **[2023-01-13 06:15:43 EST]** Automatically package all of the supported versions DLLs into the v1.2 Mod directory.
* **[2023-01-13 06:12:40 EST]** Moved the Defs into the standard location.
* **[2022-05-26 05:13:10 CST]** Reverse the age of animals completely.
* **[2022-01-29 13:19:22 COT]** Version 2.5.0.
* **[2022-01-29 13:10:02 COT]** Now the chamber reanalyzes for new injuries after each healing.
* **[2022-01-29 13:02:11 COT]** Properly determine healing frequency for species with lower life expenctancies.
* **[2022-01-29 13:01:15 COT]** Properly count Old Age disabilities.
* **[2022-01-29 13:00:53 COT]** Completely ignore missing body parts due to bionics.
* **[2022-01-29 12:59:46 COT]** Reverse age any pawn that enters healthy.
* **[2022-01-29 12:51:07 COT]** Increased the cost to build by 250 Gold.
* **[2022-01-28 18:11:00 COT]** Eject the pawn when there are no more injuries.
* **[2022-01-28 18:10:00 COT]** Heal the pawn no matter what.
* **[2021-10-29 03:51:52 COT]** FIXED: Age is shown when there are no injuries.
* **[2021-10-28 08:33:48 COT]** Version 2.0.
* **[2021-10-28 08:31:10 COT]** Show the Contents tab with the pawn inside.
* **[2021-10-28 07:59:53 COT]** Added the "Time To Heal" to the casket display.
* **[2021-10-28 06:57:34 COT]** Added a deploy script.
* **[2021-10-28 06:26:09 COT]** Added support for compiling for both Rimworld v1.2 and v1.3.
* **[2021-10-28 02:52:26 COT]** Don't heal over bionics, but do heal peg legs, etc.
* **[2021-10-28 01:00:32 COT]** Don't copy over all of the assemblies.
* **[2021-10-28 00:35:43 COT]** Added Linux build support.

## v1.1.0

* **[2021-06-17 22:45:32 MDT]** Version 1.1.
* **[2021-06-17 22:39:56 MDT]** Reset the pawn's rest, joy and comfort levels on reawakening.
* **[2021-06-17 22:38:02 MDT]** Ignore "missing body parts" for more efficient handling of bionics.

## v1.2.2719

* **[2020-08-11 09:11:17 CDT]** Upgraded to Rimworld v1.2.2719.
* **[2020-08-10 22:11:40 CDT]** Released to Steam Workshop!!

## v1.1.2654

* **[2020-08-10 21:08:59 CDT]** Ported to v1.1.2654 + lots of release changes.
* **[2020-08-10 21:03:41 CDT]** Report time frames in years, quadrums and days.

## v1.0.2559

* **[2020-08-06 17:11:37 CDT]** Upgraded to v1.0.2559.
* **[2020-08-05 07:14:51 CDT]** Upgraded to B18-0.18.1722.
* **[2020-08-06 18:02:33 CDT]** Upgraded to A17-0.17.1557.
* **[2020-08-04 21:39:22 CDT]** Completely reworked how curable injuries are determined. Ignore drugs + addictions.
* **[2020-08-04 10:33:00 CDT]** Upgraded to A16-0.16.1393.
* **[2020-08-03 13:18:12 CDT]** Upgraded to A15-0.15.1284.
* **[2020-08-03 16:02:23 CDT]** Fixed the Definition files.
* **[2020-08-03 04:09:06 CDT]** Remove negative and now-irrelevant thoughts upon ejection.
* **[2020-08-03 04:08:24 CDT]** Ignore joywires during the regenesis process.
* **[2020-08-03 04:07:55 CDT]** Properly notify the user on errors.
* **[2020-08-03 04:07:15 CDT]** Auto-eject the person when cryoregenesis is compelete.
* **[2020-07-28 20:18:09 CDT]** Simplified the healing logic.
* **[2020-07-28 19:01:56 CDT]** [m] Force Visual Studio 2019 to save with LF line endings.
* **[2020-07-28 18:51:31 CDT]** Dynamically determine a creature's target age based on its life expectancy.
* **[2020-07-26 07:53:48 CDT]** Added logic for rejuvenating Methuselah pawns > 100 years old.
* **[2020-07-26 07:52:57 CDT]** Reduced the amount of Uranium needed, as it was WAYY too much!
* **[2020-07-24 14:29:46 CDT]** Always use fuel if there's something to do.
* **[2020-07-24 14:29:01 CDT]** Refuse to heal if pregnant or has implants.
* **[2020-07-24 09:40:43 CDT]** Heal primarily based off of Uranium consumption.
* **[2020-07-23 23:28:18 CDT]** Lots and lots of improvements that I'm pretty happy with.
* **[2020-07-22 22:25:38 CDT]** Added restorative powers. Heal 1 injury per regressed bio-year, starting at age 25.
* **[2020-07-22 22:22:07 CDT]** Backported the mod to Rimworld A14.
* **[2020-07-22 22:11:26 CDT]** More renames.
* **[2020-07-22 22:09:20 CDT]** Initial renames from CryptoRestore to CryoRegenesis.
* **[2020-07-22 22:06:37 CDT]** Initial. Based off of kittycat2002/CryptoRestore-Casket@1.1.1
