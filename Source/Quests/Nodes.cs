/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *
 * This file is licensed under the MIT License.
 */

using RimWorld;
using RimWorld.QuestGen;
using Verse;

#if RIMWORLD15 || RIMWORLD16
using LudeonTK;
#endif

namespace BetterRimworlds.CryoRegenesis;

public class QuestNode_StartRoyaltyRegenesisChain : QuestNode
{
    protected override bool TestRunInt(Slate slate)
    {
        return true;
    }

    protected override void RunInt()
    {
    }
}
