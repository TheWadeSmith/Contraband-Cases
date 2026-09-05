using System.Collections.ObjectModel;

namespace ContrabandCases.Shared.Roulette;

public static class RouletteStripPlanner
{
    public static IReadOnlyList<string> Plan(
        IReadOnlyList<string> pool,
        string winner,
        int tileCount,
        int landingIndex,
        int seed,
        IReadOnlyList<string>? nearMissCandidates = null,
        int nearMissChancePercent = 35)
    {
        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }
        if (pool.Count == 0 || pool.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The cosmetic reward pool must contain non-empty entries.", nameof(pool));
        }

        if (string.IsNullOrWhiteSpace(winner))
        {
            throw new ArgumentException("A committed winner is required.", nameof(winner));
        }

        if (tileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tileCount));
        }

        if (landingIndex < 0 || landingIndex >= tileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(landingIndex));
        }

        var state = unchecked((uint)seed) + 0x9E3779B9U;
        var tiles = new string[tileCount];
        for (var index = 0; index < tiles.Length; index++)
        {
            state = Next(state);
            tiles[index] = pool[(int)(state % (uint)pool.Count)];
        }

        tiles[landingIndex] = winner;

        // Occasionally seat one of the winner's own graded tiles immediately
        // beside the landing spot ("near miss"). This never fabricates a
        // higher rarity than the winner -- the disclosed pool has no such
        // entries -- it only raises the odds that a neighbor reads as
        // equally valuable, so the win feels earned rather than arbitrary.
        // Purely cosmetic: it never touches tiles[landingIndex] itself, so
        // the committed winner is unaffected.
        if (nearMissCandidates is { Count: > 0 } && nearMissChancePercent > 0)
        {
            var chance = (uint)Math.Min(nearMissChancePercent, 100);
            foreach (var neighborIndex in new[] { landingIndex - 1, landingIndex + 1 })
            {
                if (neighborIndex < 0 || neighborIndex >= tileCount)
                {
                    continue;
                }

                state = Next(state);
                if (state % 100U >= chance)
                {
                    continue;
                }

                state = Next(state);
                tiles[neighborIndex] = nearMissCandidates[(int)(state % (uint)nearMissCandidates.Count)];
            }
        }

        return new ReadOnlyCollection<string>(tiles);
    }

    private static uint Next(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
