/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using RimWorld;
using Verse;

namespace BetterRimworlds.CryoRegenesis;

[DefOf]
public static class HuntedAssassinDefOf
{
    public static HediffDef HuntedAssassin;

    static HuntedAssassinDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(HuntedAssassinDefOf));
    }
}
