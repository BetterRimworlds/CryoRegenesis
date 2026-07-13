// ==== Source/Patch_CryoSedationSurgeryReady.cs ====
using BetterRimworlds.CryoRegenesis;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CryoRegenesis.HarmonyPatches;

/**
 * Vanilla surgery bills only proceed when the patient is InBed()
 * (Pawn.CurrentlyUsableForBills → "not ready for surgery").
 *
 * CryoRegenesis sedation is a sedate-and-carry protocol, not hospital surgery.
 * Prisoners, prison guests, and area-restricted guests often never path into a
 * medical bed (or arrive already downed on the floor), so the bill stalls forever.
 *
 * Allow our specific bill to run in place: the doctor walks to the patient,
 * sedates them, then enqueues the carry-to-casket job.
 */
[HarmonyPatch(typeof(Pawn), nameof(Pawn.CurrentlyUsableForBills))]
public static class Patch_Pawn_CurrentlyUsableForBills_CryoSedation
{
    public static void Postfix(Pawn __instance, ref bool __result)
    {
        if (__result)
            return;

        if (__instance == null || __instance.Dead || !__instance.Spawned)
            return;

        if (!HasPendingCryoSedationBill(__instance))
            return;

        // Free-standing humanlikes resolve InteractionCell to an adjacent standable cell.
        if (!__instance.InteractionCell.IsValid)
            return;

        __result = true;
        JobFailReason.Clear();
    }

    private static bool HasPendingCryoSedationBill(Pawn pawn)
    {
        BillStack bills = pawn.health?.surgeryBills;
        if (bills == null || bills.Count == 0)
            return false;

        RecipeDef sedationRecipe = CryoRegenesisDefOf.CR_AdministerCryoRegenesisSedation;
        if (sedationRecipe == null)
            return false;

        for (int i = 0; i < bills.Count; i++)
        {
            Bill bill = bills[i];
            if (bill == null || bill.deleted || bill.suspended)
                continue;

            if (bill.recipe == sedationRecipe)
                return true;
        }

        return false;
    }
}
