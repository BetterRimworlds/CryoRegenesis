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
using System.IO;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

/// Soft bridge to BetterRimworlds.Stargate: move free colonists from the Emperor
/// pickup into a live gate buffer, write them to Shuttle.xml, then remove them from
/// the world so they leave with the Emperor (serialized for a new-game recall).
///
/// Stargate is optional. Presence is detected by the unique TransdimensionalStargate def.
internal static class StargateShuttleBuffer
{
    private const string StargateProbeDefName = "TransdimensionalStargate";

    private static readonly string[] GateDefNames =
    {
        "Stargate",
        "TransdimensionalStargate",
        "OffWorldStargate",
    };

    private static bool? stargateModPresent;
    private static MethodInfo saveThingsMethod;
    private static MethodInfo transmitContentsMethod;
    private static bool saveMethodResolved;
    private static bool transmitMethodResolved;

    /// True when the Stargate mod has loaded its defs (unique probe def).
    public static bool IsStargateModLoaded()
    {
        if (stargateModPresent.HasValue)
        {
            return stargateModPresent.Value;
        }

        stargateModPresent = DefDatabase<ThingDef>.GetNamedSilentFail(StargateProbeDefName) != null;
        return stargateModPresent.Value;
    }

    /// Transfer free-colonist escapees from the Imperial shuttle into a map Stargate buffer,
    /// serialize only those pawns to …/Stargate/Shuttle.xml, then destroy them so they
    /// leave this world (they must not remain sitting in the gate after launch).
    /// Returns true when at least one colonist was written.
    public static bool TryTransmitFreeColonistsFromTransporter(
        CompTransporter transporter,
        Func<Pawn, bool> isEscapee,
        Action<string> log = null)
    {
        // Outer guard: never throw into CompShuttle.Send (1.2 sets sending=true first).
        try
        {
            return TryTransmitFreeColonistsFromTransporterInner(transporter, isEscapee, log);
        }
        catch (Exception e)
        {
            Log.Error(
                "[CryoRegenesis] StargateShuttleBuffer.TryTransmit failed (ignored): " + e);
            return false;
        }
    }

    private static bool TryTransmitFreeColonistsFromTransporterInner(
        CompTransporter transporter,
        Func<Pawn, bool> isEscapee,
        Action<string> log)
    {
        if (transporter?.innerContainer == null || isEscapee == null)
        {
            return false;
        }

        if (!IsStargateModLoaded())
        {
            log?.Invoke(
                "Stargate mod not present (no " + StargateProbeDefName
                + " def) — skip shuttle buffer write.");
            return false;
        }

        List<Pawn> escapees = transporter.innerContainer
            .OfType<Pawn>()
            .Where(p => p != null && !p.Destroyed && isEscapee(p))
            .ToList();
        if (!escapees.Any())
        {
            return false;
        }

        Map map = transporter.parent?.MapHeld;
        if (map == null)
        {
            log?.Invoke("No map for Imperial shuttle — cannot find a Stargate.");
            return false;
        }

        Thing gate = FindStargateOnMap(map);
        if (gate == null)
        {
            log?.Invoke("No Stargate on map — skip shuttle buffer write for "
                + escapees.Count + " colonist(s).");
            return false;
        }

        if (!(gate is IThingHolder holder))
        {
            log?.Invoke("Stargate building is not an IThingHolder.");
            return false;
        }

        ThingOwner buffer = holder.GetDirectlyHeldThings();
        if (buffer == null)
        {
            log?.Invoke("Stargate GetDirectlyHeldThings() returned null.");
            return false;
        }

        // Drop free colonists from requiredPawns so a failed mid-leave cannot lock Send forever.
        CompShuttle shuttleComp = transporter.parent?.TryGetComp<CompShuttle>();
        if (shuttleComp?.requiredPawns != null)
        {
            shuttleComp.requiredPawns.RemoveAll(
                p => p == null || p.Destroyed || escapees.Contains(p));
        }

        List<Pawn> transferred = new List<Pawn>();
        foreach (Pawn pawn in escapees)
        {
            // Pull out of the transporter first so the shuttle will not destroy them,
            // then let StargateBuffer.TryAdd attach the traveler implant and hold them.
            if (transporter.innerContainer.Contains(pawn))
            {
                transporter.innerContainer.Remove(pawn);
            }

            if (buffer.TryAdd(pawn, canMergeWithExistingStacks: false))
            {
                transferred.Add(pawn);
                continue;
            }

            bool returnedToShuttle = !pawn.Destroyed
                && transporter.innerContainer.TryAddOrTransfer(pawn, false);
            if (!returnedToShuttle && !pawn.Spawned && !pawn.Destroyed)
            {
                GenSpawn.Spawn(pawn, transporter.parent.PositionHeld, map, WipeMode.Vanish);
            }

            log?.Invoke("Failed to add " + pawn.LabelShort + " to Stargate buffer; "
                + (returnedToShuttle ? "returned to shuttle." : "returned to the map."));
        }

        if (!transferred.Any())
        {
            return false;
        }

        if (!TrySaveEscapeesToShuttleXml(buffer, transferred, log))
        {
            // Serialization failed — leave them in the gate buffer rather than destroy
            // living colonists that never made it to Shuttle.xml.
            log?.Invoke(
                "Shuttle.xml write failed; " + transferred.Count
                + " colonist(s) remain in the Stargate buffer.");
            return false;
        }

        // They have been serialized for a new-game recall and must leave this world.
        // Leaving them in the gate would strand "escaped" colonists on the map and
        // break the Imperial Court / usurpation endgames' "they left" fiction.
        foreach (Pawn pawn in transferred)
        {
            if (buffer.Contains(pawn))
            {
                buffer.Remove(pawn);
            }

            if (pawn != null && !pawn.Destroyed)
            {
                pawn.Destroy(DestroyMode.Vanish);
            }
        }

        Find.ColonistBar?.MarkColonistsDirty();
        log?.Invoke(
            "Transmitted " + transferred.Count
            + " free colonist(s) to Stargate Shuttle.xml and removed them from the world via "
            + gate.LabelShort + ".");
        return true;
    }

    private static bool TrySaveEscapeesToShuttleXml(
        ThingOwner buffer,
        List<Pawn> escapees,
        Action<string> log)
    {
        string shuttlePath = ResolveShuttleBufferPath(buffer);
        if (shuttlePath.NullOrEmpty())
        {
            log?.Invoke("Could not resolve Stargate Shuttle.xml path.");
            return false;
        }

        // Prefer saving only the escapees so pre-existing gate cargo is not mixed in.
        if (TryResolveSaveThings(buffer.GetType(), log)
            && TryInvokeSaveThings(escapees.Cast<Thing>().ToList(), shuttlePath, log))
        {
            return true;
        }

        // Fallback: TransmitContents(keepContents: true) writes the whole buffer, then
        // the caller still destroys only the escapees we transferred.
        if (!TryResolveTransmitContents(buffer.GetType(), log))
        {
            return false;
        }

        try
        {
            transmitContentsMethod.Invoke(buffer, new object[] { true });
            log?.Invoke(
                "Saved Shuttle.xml via TransmitContents(keepContents: true) fallback.");
            return true;
        }
        catch (Exception e)
        {
            Log.Error(
                "[CryoRegenesis] Stargate TransmitContents(keepContents: true) failed: "
                + e);
            return false;
        }
    }

    private static string ResolveShuttleBufferPath(ThingOwner buffer)
    {
        if (buffer == null)
        {
            return null;
        }

        // Match StargateBuffer field when present.
        FieldInfo field = buffer.GetType().GetField(
            "ShuttleBufferFilePath",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            string path = field.GetValue(buffer) as string;
            if (!path.NullOrEmpty())
            {
                return path;
            }
        }

        // Same layout StargateBuffer.EnsurePathsInitialized uses.
        string baseDirectory = Path.Combine(GenFilePaths.SaveDataFolderPath, "Stargate");
        if (!Directory.Exists(baseDirectory))
        {
            Directory.CreateDirectory(baseDirectory);
        }

        return Path.Combine(baseDirectory, "Shuttle.xml");
    }

    private static bool TryResolveSaveThings(Type bufferType, Action<string> log)
    {
        if (saveMethodResolved)
        {
            return saveThingsMethod != null;
        }

        saveMethodResolved = true;

        // Prefer the type from the live Stargate assembly (buffer's assembly).
        Type saveType = bufferType.Assembly.GetType(
            "Enhanced_Development.Stargate.Saving.SaveThings");
        if (saveType == null)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    saveType = assembly.GetType(
                        "Enhanced_Development.Stargate.Saving.SaveThings");
                    if (saveType != null)
                    {
                        break;
                    }
                }
                catch
                {
                    // Skip dynamic / reflection-only assemblies.
                }
            }
        }

        if (saveType == null)
        {
            log?.Invoke(
                "SaveThings type not found — will try TransmitContents(keepContents) fallback.");
            return false;
        }

        saveThingsMethod = saveType.GetMethod(
            "save",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(List<Thing>), typeof(string) },
            modifiers: null);

        if (saveThingsMethod == null)
        {
            log?.Invoke(
                "SaveThings.save(List<Thing>, string) not found — will try TransmitContents fallback.");
            return false;
        }

        return true;
    }

    private static bool TryInvokeSaveThings(
        List<Thing> things,
        string path,
        Action<string> log)
    {
        if (saveThingsMethod == null || things == null || path.NullOrEmpty())
        {
            return false;
        }

        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!directory.NullOrEmpty() && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            saveThingsMethod.Invoke(null, new object[] { things, path });
            return true;
        }
        catch (Exception e)
        {
            Log.Error("[CryoRegenesis] SaveThings.save for Shuttle.xml failed: " + e);
            log?.Invoke("SaveThings.save failed: " + e.Message);
            return false;
        }
    }

    private static Thing FindStargateOnMap(Map map)
    {
        if (map == null)
        {
            return null;
        }

        foreach (string defName in GateDefNames)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                continue;
            }

            // Prefer player-owned buildings; fall back to any thing of that def.
            Building colonistGate = map.listerBuildings.allBuildingsColonist
                .FirstOrDefault(b => b.def == def);
            if (colonistGate != null)
            {
                return colonistGate;
            }

            Thing any = map.listerThings.ThingsOfDef(def).FirstOrDefault();
            if (any != null)
            {
                return any;
            }
        }

        // Def names can lag a WIP build; match Stargate building classes by name.
        foreach (Building building in map.listerBuildings.allBuildingsColonist)
        {
            string typeName = building.GetType().Name;
            if (typeName == "Building_Stargate"
                || typeName == "Building_TransdimensionalStargate"
                || typeName == "Building_OffWorldStargate")
            {
                return building;
            }
        }

        return null;
    }

    private static bool TryResolveTransmitContents(Type bufferType, Action<string> log)
    {
        if (transmitMethodResolved)
        {
            if (transmitContentsMethod == null)
            {
                log?.Invoke(
                    "StargateBuffer.TransmitContents(bool) was not found earlier — "
                    + "rebuild Stargate with the keepContents API.");
            }

            return transmitContentsMethod != null;
        }

        transmitMethodResolved = true;
        transmitContentsMethod = bufferType.GetMethod(
            "TransmitContents",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(bool) },
            modifiers: null);

        if (transmitContentsMethod == null)
        {
            log?.Invoke(
                "StargateBuffer.TransmitContents(bool) not found on "
                + bufferType.FullName
                + " — rebuild Stargate with the keepContents API.");
            return false;
        }

        return true;
    }
}
