namespace ContrabandCases.Shared.Catalog;

public static class WeightedRewardSelector
{
    private const double WeightTolerance = 1e-9;

    public static ValidatedReward Select(IReadOnlyList<ValidatedReward> rewards, double unitValue)
    {
        if (rewards is null)
        {
            throw new ArgumentNullException(nameof(rewards));
        }
        if (!double.IsFinite(unitValue) || unitValue < 0d || unitValue >= 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(unitValue));
        }

        if (rewards.Count == 0)
        {
            throw new ArgumentException("At least one validated reward is required.", nameof(rewards));
        }

        var total = 0d;
        foreach (var reward in rewards)
        {
            if (reward is null || !double.IsFinite(reward.Weight) || reward.Weight <= 0d)
            {
                throw new ArgumentException("Rewards must be validated before selection.", nameof(rewards));
            }

            total += reward.Weight;
        }

        if (Math.Abs(total - 1d) > WeightTolerance)
        {
            throw new ArgumentException("Reward weights must total 1.0.", nameof(rewards));
        }

        total = 0d;
        foreach (var reward in rewards)
        {
            total += reward.Weight;
            if (unitValue < total)
            {
                return reward;
            }
        }

        return rewards[rewards.Count - 1];
    }
}
