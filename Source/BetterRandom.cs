/*
 * This file is part of CryoRegenesis, a Better Rimworlds Project.
 *
 * Copyright © 2020-2026 Theodore R. Smith
 * Author: Theodore R. Smith <hopeseekr@gmail.com>
 *   GPG Fingerprint: D8EA 6E4D 5952 159D 7759  2BB4 EEB6 CE72 F441 EC41
 *   https://github.com/BetterRimworlds/CryoRegenesis
 *
 * This file is licensed under the MIT License.
 */

using System;

namespace BetterRimworlds;

public class BetterRandom
{
    private static readonly Random random = new Random();

    public static int pick(int min, int max)
    {
        if (min > max)
        {
            throw new ArgumentException("min value should not be greater than max value.");
        }

        // The upper bound in Random.Next is exclusive, so we add 1 to include max
        return random.Next(min, max + 1);
    }
}
