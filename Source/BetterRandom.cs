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
