# CryoRegenesis: Forever Young

![CryoRegenesis: Live forever!](https://raw.githubusercontent.com/BetterRimworlds/CryoRegenesis/master/CryoRegenesis/About/Preview.png)

With this Glittertech, all of your colonists (and pets, and even enemies, if you're so 
inclined!) can live forever young!

CryoRegenesis sarcophagi not only restore your pawns to their youthful vigor, they also 
cure all age-related infirmities as well as every physical injury!

 • No more aging. Everyone can be in their 20s!  
 • Heals bad backs, permanent scars, even dimensia, Alzheimer's and bullets to the brain!  
 • Heals most diseases. There is no more need to die from the flu!  
 • Luciferium need cannot be cured, nor can psychological addictions, though physical
   tolerances will disappear.
 • Resurrects dead Pawns that 1) have a brain and 2) haven't been left unrefrigerated for more than 1 day.

Stock up on Uranium. You'll need a whole lot of it!

![Cargo](https://github.com/BetterRimworlds/CryoRegenesis/assets/1125541/e76d8ca0-2616-44d6-9f7e-f89fa014a633)

Here's our illustrious pup, Cargo! He's been with the colonists 101 years and is 107 years old! He's lived
on more Rimworlds than most humans!

## Resurrection Costs and Consequences

**Resource Requirements:**
Bringing someone back from the dead requires significant resources:
- **150 Uranium** - Powers the nanite reconstruction process
- **500 Gold** - Essential for the advanced circuitry and nanite construction
- **50 Luciferium** - The mechanites are required to jumpstart cellular regeneration

**The Price of Resurrection:**
- All resurrected pawns will gain a **permanent Luciferium addiction**. Death is not without consequence - the mechanites that bring them back require continued doses to maintain cellular cohesion.
- This addiction cannot be cured by the sarcophagus (as noted in features above).

## New Mood Effects

**"Grateful to be alive!" (Resurrection only)**
- Humanlike pawns who are resurrected gain a powerful +28 mood buff lasting 60 days
- Represents the profound psychological impact of near-death experiences
- Prevents post-resurrection mental breaks and helps colonists readjust to life

**"I feel younger!" (Age Reversal)**
- New staged mood buff based on years regenerated:
  - 20 years reversed: +15 mood - "I feel 20 years younger!"
  - 40 years reversed: +25 mood - "Substantially regenerated"
  - 60 years reversed: +35 mood - "Majorly regenerated" 
  - 80+ years reversed: +40 mood - "Fully regenerated"
- Lasts 60 days as colonists enjoy their renewed youth
- Stacks with resurrection mood bonus when applicable

### Balance Notes
The combination of resource costs and permanent Luciferium addiction ensures resurrection remains a serious decision rather than a casual convenience. Plan accordingly and maintain a steady Luciferium supply for your immortal colonists!

## Changelog

**Version 1.0: 2020-08-11**
* **[2020-08-10 22:11:40 CDT]** All of the core functionality.
* **[2020-08-11 09:11:17 CDT]** Added support for Rimworld v1.2.

**Version 1.1: 2021-06-17**
* **[2021-06-17 23:38:02 CDT]** Ignore "missing body parts" for more efficient handling of bionics.
* **[2021-06-17 23:39:56 CDT]** Reset the pawn's rest, joy and comfort levels on reawakening.

**Version 2.0: 2021-10-28**
* **[2021-10-28 00:35:43 CDT]** Added Linux build support.
* **[2021-10-28 01:00:32 CDT]** [m] Don't copy over all of the assemblies.
* **[2021-10-28 02:52:26 CDT]** Don't heal over bionics, but do heal peg legs, etc.
* **[2021-10-28 06:26:09 CDT]** Added support for compiling for both Rimworld v1.2 and v1.3.
* **[2021-10-28 06:57:34 CDT]** [m] Added a deploy script.
* **[2021-10-28 07:59:53 CDT]** Added the "Time To Heal" to the casket display.
* **[2021-10-28 08:31:10 CDT]** Show the Contents tab with the pawn inside.

**Version 2.5: 2022-01-29**
* **[2021-10-29 02:51:52 CDT]** FIXED: Age is shown when there are no injuries. origin/v2.0
* **[2022-01-28 17:10:00 CDT]** Heal the pawn no matter what.
* **[2022-01-28 17:11:00 CDT]** Eject the pawn when there are no more injuries.
* **[2022-01-29 11:51:07 CDT]** Increased the cost to build by 250 Gold.
* **[2022-01-29 11:59:46 CDT]** Reverse age any pawn that enters healthy.
* **[2022-01-29 12:00:53 CDT]** Completely ignore missing body parts due to bionics.
* **[2022-01-29 12:01:15 CDT]** Properly count Old Age disabilities.
* **[2022-01-29 12:02:11 CDT]** Properly determine healing frequency for species with lower life expenctancies.
* **[2022-01-29 12:10:02 CDT]** Now the chamber reanalyzes for new injuries after each healing.

**Version 2.6: 2023-01-13**
* **[2022-05-26 06:13:10 CDT]** Reverse the age of animals completely.
* **[2023-01-13 05:12:40 CDT]** Moved the Defs into the standard location.
* **[2023-01-13 05:15:43 CDT]** Automatically package all of the supported versions DLLs into the v1.2 Mod directory.
* **[2023-01-13 05:16:45 CDT]** Upgraded to Rimworld v1.4.3580.

**Version 3.0: 2023-05-22**
* **[2023-03-26 06:41:29 CDT]** Re-added more hair colors.
* **[2023-05-21 10:41:18 CDT]** [m] Update README.md: Added info about Cargo.
* **[2023-05-22 00:30:13 CDT]** [m] Added a proper Mod class.
* **[2023-05-22 00:32:17 CDT]** Added Mod Settings.
* **[2023-05-22 00:38:56 CDT]** [m] Rearranged the SpawnSetup method.
* **[2023-05-22 00:42:55 CDT]** Enabled the new optional debug messaging system.
* **[2023-05-22 00:43:34 CDT]** Added a new option to only regen until the pawn is healed.
* **[2023-05-22 00:44:19 CDT]** Added a new option for the target age of Human pawns.
* **[2023-05-22 00:47:24 CDT]** Added an option to not try to heal anything that isn't tendable.
* **[2023-05-22 00:47:45 CDT]** Added functionality to never heal implants.
* **[2023-05-22 01:31:53 CDT]** Block non-colonist humans from being placed in the casket to avoid CryoRegenesis Quantum Anomaly (Bug #6).

**Version 3.0.1: 2023-07-28**
* **[2023-07-28 16:43:18 CDT]** Fixed a bug that prohibited animals from being put in the cryocasket.

**Version 3.1.0: 2024-03-15**
* **[2024-03-15 01:44:12 CDT]** [m] Majorly refactored the deploy script.
* **[2024-03-15 01:44:32 CDT]** Upgraded to Rimworld v1.5.
* **[2024-03-15 01:46:43 CDT]** Made the CryoRegenesis flickable (turns it into a normal cryocasket).
* **[2024-03-15 01:47:43 CDT]** Added optional code (enabled by default) to not heal any hediffs that are not marked as "bad".
* **[2024-03-15 02:16:24 CDT]** Cured the CyroRegenesis Quantum Anomaly that broke when guests were put in.

**Version 4.0.0: 2024-05-13**
* **[2024-05-12 13:53:08 CDT]** Implemented the resurrection of corpses.

**Version 4.1.0: 2025-03-15**
* **[2025-03-06 06:27:19 CDT]** Ported to .NET v9.0 and C# v10.0.
* **[2025-03-14 15:52:36 CDT]** Rearchitected the files to the BetterRimworlds standard layout.
* **[2025-03-15 03:11:00 CDT]** [m] Tiny code cleanups.

**Version 5.0.0: 2025-07-22**
* **[2025-07-22 21:47:41 CDT]** Added happy thoughts about being regenerated.
* **[2025-07-22 04:45:26 CDT]** Added a "Grateful to be a alive!" thought when pawns are resurrected.
* **[2025-07-22 04:28:00 CDT]** Added Luciferium addiction to resurrected pawns.
* **[2025-07-22 04:05:47 CDT]** Cause resurrected pawns to be under anesthetic on revival.
* **[2025-07-22 03:58:41 CDT]** Store the pawn's original age for use later.
* **[2025-07-22 03:45:45 CDT]** Fixed the resurrection system.
* **[2025-07-14 07:00:11 CDT]** Fixed the requirements gathering for resurrection.
* **[2025-07-11 18:25:25 CDT]** Added support for Rimworld v1.6.
* **[2025-07-11 18:01:11 CDT]** Migrated to a modern dotnet SDK project.

