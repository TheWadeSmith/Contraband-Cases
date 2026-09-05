using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Roulette;

namespace ContrabandCases.Client.Opening;

public enum RouletteMotionMode
{
    Scroll,
    Fade
}

public static class RouletteAnimationMath
{
    public static double LandingX(
        double viewportWidth,
        double tileWidth,
        double tileSpacing,
        int landingIndex)
    {
        RequirePositiveFinite(viewportWidth, nameof(viewportWidth));
        RequirePositiveFinite(tileWidth, nameof(tileWidth));
        RequireNonNegativeFinite(tileSpacing, nameof(tileSpacing));
        if (landingIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(landingIndex));
        }

        return viewportWidth * 0.5d -
            (landingIndex * (tileWidth + tileSpacing) + tileWidth * 0.5d);
    }

    /// Maximum fraction of a tile's width the landing position may drift off dead-center. Kept well under
    /// half a tile so the landing tile is always unambiguous under the marker -- this is purely cosmetic
    /// "doesn't look robotic" jitter, never enough to make it look like a neighboring tile won instead.
    private const double LandingJitterMaxFraction = 0.22d;

    /// A small, deterministic per-spin offset from dead-center so the reel doesn't rest at the exact same
    /// pixel position on every single spin. Derived from the same seed the strip composition already uses
    /// (not a fresh random draw), so replaying/testing a reveal with the same seed always looks identical,
    /// while different spins land at a slightly different point within the tile.
    public static double LandingJitterOffset(double tileWidth, int seed)
    {
        RequirePositiveFinite(tileWidth, nameof(tileWidth));
        var normalized = new Random(seed).NextDouble() * 2d - 1d; // [-1, 1)
        return normalized * LandingJitterMaxFraction * tileWidth;
    }

    /// Maximum fractional deviation from the configured base spin duration that a single spin's
    /// per-opening seed may apply, so consecutive openings don't all take the exact same number of
    /// seconds to land and settle -- the same spirit as LandingJitterOffset above (a small, seed-
    /// derived, purely cosmetic pacing variance that never touches which item is reported won, nor
    /// where it lands), but salted differently so the two draws don't move in lockstep for a given seed.
    private const double DurationJitterMaxFraction = 0.10d;

    /// Derives this spin's actual on-screen duration from the configured base duration plus a small,
    /// deterministic per-seed jitter (+/-10%). The winning item and its exact landing position are both
    /// already fixed by the time this runs -- only how long the reel takes to get there varies, which is
    /// what keeps back-to-back openings from all decelerating on the exact same beat.
    public static double SpinDurationSeconds(double baseDurationSeconds, int seed)
    {
        RequirePositiveFinite(baseDurationSeconds, nameof(baseDurationSeconds));
        var normalized = new Random(unchecked(seed * 397 + 811)).NextDouble() * 2d - 1d; // [-1, 1)
        return baseDurationSeconds * (1d + normalized * DurationJitterMaxFraction);
    }

    public static double EaseOutCubic(double normalizedTime)
    {
        if (double.IsNaN(normalizedTime))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedTime));
        }

        var clamped = Math.Max(0d, Math.Min(1d, normalizedTime));
        return 1d - Math.Pow(1d - clamped, 3d);
    }

    // Match velocity at the join: 2*d/a = k*(1-d)/(1-a).
    // The bounded seeded profiles change rhythm without a snap, reversal or fake near miss.
    public static double EaseInOutSpin(double normalizedTime) => EaseInOutSpin(normalizedTime, 0.30d, 3.5d);

    internal static double EaseInOutSpin(double normalizedTime, double accelerationShare, double decelerationPower)
    {
        if (double.IsNaN(normalizedTime)) throw new ArgumentOutOfRangeException(nameof(normalizedTime));
        var t = Math.Clamp(normalizedTime, 0d, 1d);
        var distance = decelerationPower * accelerationShare /
            (2d * (1d - accelerationShare) + decelerationPower * accelerationShare);
        if (t <= accelerationShare)
        {
            var local = t / accelerationShare;
            return distance * local * local;
        }
        var remaining = (t - accelerationShare) / (1d - accelerationShare);
        return distance + (1d - distance) * (1d - Math.Pow(1d - remaining, decelerationPower));
    }

    public static double Position(double startX, double finalX, double elapsedSeconds, double durationSeconds) =>
        Position(startX, finalX, elapsedSeconds, durationSeconds, 0.30d, 3.5d);

    internal static double Position(double startX, double finalX, double elapsedSeconds, double durationSeconds,
        double accelerationShare, double decelerationPower)
    {
        RequireFinite(startX, nameof(startX));
        RequireFinite(finalX, nameof(finalX));
        RequireNonNegativeFinite(elapsedSeconds, nameof(elapsedSeconds));
        RequirePositiveFinite(durationSeconds, nameof(durationSeconds));
        if (elapsedSeconds >= durationSeconds) return finalX;
        var eased = EaseInOutSpin(elapsedSeconds / durationSeconds, accelerationShare, decelerationPower);
        return startX + (finalX - startX) * eased;
    }

    public static (int TickIndex, float Pitch, float Volume) SpinAudio(
        double stripX, double progress, double stride, int landingIndex)
    {
        RequireFinite(stripX, nameof(stripX));
        RequireFinite(progress, nameof(progress));
        RequirePositiveFinite(stride, nameof(stride));
        if (landingIndex < 0) throw new ArgumentOutOfRangeException(nameof(landingIndex));
        var clamped = Math.Clamp(progress, 0d, 1d);
        return (
            (int)Math.Clamp(Math.Round(-stripX / stride), 0d, landingIndex),
            (float)(1.5d - 0.65d * clamped),
            (float)(0.5d - 0.24d * clamped));
    }

    private static void RequirePositiveFinite(double value, string parameterName)
    {
        RequireFinite(value, parameterName);
        if (value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void RequireNonNegativeFinite(double value, string parameterName)
    {
        RequireFinite(value, parameterName);
        if (value < 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void RequireFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// Maps a reward's rarity to presentation intensity for the landing
/// celebration (pulse), the ambient idle shimmer, and confetti gating.
/// Kept as pure, unit-testable lookups separate from the Unity-object-
/// touching code in RouletteOverlay/ManifestPresentationCoordinator that
/// consumes them.
public static class RarityCelebrationTuning
{
    /// Multiplier applied to the landing pulse's hold duration.
    public static double PulseHoldMultiplier(RewardRarity grade) => grade switch
    {
        RewardRarity.BlackLabel => 1.75d,
        RewardRarity.Restricted => 1.35d,
        RewardRarity.Contractor => 1.1d,
        RewardRarity.Uncommon => 1.025d,
        _ => 1d
    };

    /// Multiplier applied to the landing pulse's scale amplitude.
    public static double PulseScaleMultiplier(RewardRarity grade) => grade switch
    {
        RewardRarity.BlackLabel => 1.6d,
        RewardRarity.Restricted => 1.25d,
        RewardRarity.Contractor => 1.05d,
        RewardRarity.Uncommon => 1.02d,
        _ => 1d
    };

    /// Multiplier applied to the landing pulse's flash intensity.
    public static double PulseFlashMultiplier(RewardRarity grade) => grade switch
    {
        RewardRarity.BlackLabel => 1.5d,
        RewardRarity.Restricted => 1.2d,
        RewardRarity.Contractor => 1.05d,
        RewardRarity.Uncommon => 1.02d,
        _ => 1d
    };

    /// Only the rarest tier earns a confetti burst -- keeping it exclusive
    /// is what makes it read as special rather than routine.
    public static bool ShouldBurstConfetti(RewardRarity grade) =>
        grade == RewardRarity.BlackLabel;

    /// Only the rarest tier gets the idle shimmer while the reel is still
    /// spinning. Placeholder seal tiles are always constructed with
    /// RewardRarity.ScavGrade (see ManifestPresentationFlow.CreateReveal),
    /// so this naturally excludes them without any special-casing.
    public static bool ShouldShimmer(RewardRarity grade) =>
        grade == RewardRarity.BlackLabel;
}

/// Pure grouping/sorting and grid-arithmetic for the pre-open confirmation
/// screen's reward catalog grid: worst-to-best by rarity tier (so the
/// payoff builds as the player scrolls down toward the rarest section),
/// most-common-first within a tier, plus the fixed-column cell math the
/// grid is laid out with. Kept separate from the Unity-object-touching
/// grid construction in RouletteOverlay that consumes it, the same way
/// RouletteAnimationMath is kept separate from the spin it drives.
public static class CatalogGridLayout
{
    /// Rarity tiers in worst-to-best presentation order.
    public static readonly IReadOnlyList<RewardRarity> TierOrder =
        new ReadOnlyCollection<RewardRarity>(
        [
            RewardRarity.ScavGrade,
            RewardRarity.Uncommon,
            RewardRarity.Contractor,
            RewardRarity.Restricted,
            RewardRarity.BlackLabel
        ]);

    public readonly struct Section
    {
        public Section(RewardRarity tier, IReadOnlyList<ValidatedReward> rewards)
        {
            Tier = tier;
            Rewards = rewards;
        }

        public RewardRarity Tier { get; }

        public IReadOnlyList<ValidatedReward> Rewards { get; }
    }

    /// Groups the full catalog into one section per rarity tier that has at
    /// least one entry (empty tiers are omitted rather than shown as an
    /// empty header), each ordered worst-to-best, and orders each tier's
    /// own rewards from most to least common (by published weight, then by
    /// name for a stable, deterministic tie-break) so the grid reads as
    /// "common variants first, rare variants last" within a tier too.
    public static IReadOnlyList<Section> GroupByTier(IReadOnlyList<ValidatedReward> rewards)
    {
        if (rewards is null)
        {
            throw new ArgumentNullException(nameof(rewards));
        }

        var sections = new List<Section>(TierOrder.Count);
        foreach (var tier in TierOrder)
        {
            var ordered = rewards
                .Where(reward => reward.Rarity == tier)
                .OrderByDescending(reward => reward.Weight)
                .ThenBy(reward => reward.DisplayName, StringComparer.Ordinal)
                .ThenBy(reward => reward.Id, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length > 0)
            {
                sections.Add(new Section(tier, new ReadOnlyCollection<ValidatedReward>(ordered)));
            }
        }

        return new ReadOnlyCollection<Section>(sections);
    }

    /// One confirmation-screen audit-grid tile's worth of data, carrying
    /// only what CatalogTileView and the Manifest sprite pipeline need --
    /// deliberately independent of ManifestOpeningLotOddsSnapshot so this
    /// presentation-math class stays free of the Client.Opening odds-
    /// disclosure model, the same separation ValidatedReward already gets
    /// from the legacy catalog grid. `OddsText` is the server's own
    /// pre-formatted percentage string (e.g. lot.ConditionalPercent),
    /// carried through unchanged rather than recomputed from a weight,
    /// since the audit grid's whole point is displaying the exact
    /// published value.
    public readonly struct AuditOddsTile
    {
        public AuditOddsTile(
            string tileId,
            string displayName,
            string oddsText,
            RewardRarity grade,
            string anchorTemplateId)
        {
            TileId = tileId;
            DisplayName = displayName;
            OddsText = oddsText;
            Grade = grade;
            AnchorTemplateId = anchorTemplateId;
        }

        public string TileId { get; }

        public string DisplayName { get; }

        public string OddsText { get; }

        public RewardRarity Grade { get; }

        public string AnchorTemplateId { get; }
    }

    public readonly struct OddsSection
    {
        public OddsSection(RewardRarity tier, IReadOnlyList<AuditOddsTile> tiles)
        {
            Tier = tier;
            Tiles = tiles;
        }

        public RewardRarity Tier { get; }

        public IReadOnlyList<AuditOddsTile> Tiles { get; }
    }

    /// Groups a flattened full-odds audit list (every family's lots
    /// combined into one list) into one section per rarity tier that has
    /// at least one entry, in the same worst-to-best tier order the
    /// reward catalog grid uses. Unlike GroupByTier, a tier's own tiles
    /// keep whatever order the caller already flattened them in (the
    /// server's canonical family/provider/lot order the parser already
    /// validated) rather than being re-sorted by weight here -- an odds
    /// tile only carries a pre-formatted percentage string, not a raw
    /// weight, so there is nothing to re-derive a sort key from without
    /// recomputing a value this screen's whole point is displaying
    /// unmodified from the server.
    public static IReadOnlyList<OddsSection> GroupOddsTilesByTier(IReadOnlyList<AuditOddsTile> tiles)
    {
        if (tiles is null)
        {
            throw new ArgumentNullException(nameof(tiles));
        }

        var sections = new List<OddsSection>(TierOrder.Count);
        foreach (var tier in TierOrder)
        {
            var ordered = tiles.Where(tile => tile.Grade == tier).ToArray();
            if (ordered.Length > 0)
            {
                sections.Add(new OddsSection(tier, new ReadOnlyCollection<AuditOddsTile>(ordered)));
            }
        }

        return new ReadOnlyCollection<OddsSection>(sections);
    }

    /// Maximum number of fixed-size cells (plus spacing) that fit across
    /// the given width, always at least one so a single very narrow
    /// viewport still lays out (rather than dividing by zero downstream).
    public static int ColumnCount(double availableWidth, double cellWidth, double columnSpacing)
    {
        RequirePositiveFinite(availableWidth, nameof(availableWidth));
        RequirePositiveFinite(cellWidth, nameof(cellWidth));
        RequireNonNegativeFinite(columnSpacing, nameof(columnSpacing));

        var columns = (int)Math.Floor((availableWidth + columnSpacing) / (cellWidth + columnSpacing));
        return Math.Max(1, columns);
    }

    public static int RowCount(int itemCount, int columns)
    {
        if (itemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount));
        }
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        return itemCount == 0 ? 0 : (itemCount + columns - 1) / columns;
    }

    /// Top-left offset (x rightward, y downward) of the cell at `index`
    /// within a fixed-column grid, relative to the grid's own top-left
    /// origin -- the caller adds its section's own header height and
    /// running vertical offset on top of this.
    public static (double X, double Y) CellOffset(
        int index,
        int columns,
        double cellWidth,
        double cellHeight,
        double columnSpacing,
        double rowSpacing)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }
        RequirePositiveFinite(cellWidth, nameof(cellWidth));
        RequirePositiveFinite(cellHeight, nameof(cellHeight));
        RequireNonNegativeFinite(columnSpacing, nameof(columnSpacing));
        RequireNonNegativeFinite(rowSpacing, nameof(rowSpacing));

        var column = index % columns;
        var row = index / columns;
        return (
            column * (cellWidth + columnSpacing),
            row * (cellHeight + rowSpacing));
    }

    /// Total height of every row of a `itemCount`-cell grid (header height
    /// is the caller's own concern, added on top of this).
    public static double GridHeight(int itemCount, int columns, double cellHeight, double rowSpacing)
    {
        RequirePositiveFinite(cellHeight, nameof(cellHeight));
        RequireNonNegativeFinite(rowSpacing, nameof(rowSpacing));

        var rows = RowCount(itemCount, columns);
        return rows == 0 ? 0d : rows * cellHeight + (rows - 1) * rowSpacing;
    }

    private static void RequirePositiveFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void RequireNonNegativeFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public sealed class RouletteRevealPlan
{
    private RouletteRevealPlan(
        IReadOnlyList<string> strip,
        string committedWinnerId,
        int landingIndex,
        double finalX,
        double durationSeconds,
        double startX,
        double accelerationTimeShare,
        double decelerationPower,
        RouletteMotionMode motionMode)
    {
        Strip = strip;
        CommittedWinnerId = committedWinnerId;
        LandingIndex = landingIndex;
        FinalX = finalX;
        DurationSeconds = durationSeconds;
        StartX = startX;
        AccelerationTimeShare = accelerationTimeShare;
        DecelerationPower = decelerationPower;
        MotionMode = motionMode;
    }

    public IReadOnlyList<string> Strip { get; }

    public string CommittedWinnerId { get; }

    public int LandingIndex { get; }

    public double FinalX { get; }

    public double StartX { get; }
    public double AccelerationTimeShare { get; }
    public double DecelerationPower { get; }
    public double PositionAt(double elapsedSeconds) => RouletteAnimationMath.Position(
        StartX, FinalX, elapsedSeconds, DurationSeconds, AccelerationTimeShare, DecelerationPower);

    /// This spin's actual on-screen duration -- the configured base duration plus a small deterministic
    /// per-seed jitter (see RouletteAnimationMath.SpinDurationSeconds), so back-to-back openings don't all
    /// take the exact same number of seconds to land. Purely cosmetic pacing; the committed winner and its
    /// exact landing position (FinalX above) are unaffected.
    public double DurationSeconds { get; }

    public RouletteMotionMode MotionMode { get; }

    public bool UsesScrolling => MotionMode == RouletteMotionMode.Scroll;

    public static RouletteRevealPlan Create(
        IReadOnlyList<string> pool,
        string committedWinnerId,
        int tileCount,
        int landingIndex,
        int seed,
        double viewportWidth,
        double tileWidth,
        double tileSpacing,
        bool reducedMotion,
        IReadOnlyList<string>? nearMissCandidates = null,
        int nearMissChancePercent = 35,
        double baseDurationSeconds = 4.5d)
    {
        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (string.IsNullOrWhiteSpace(committedWinnerId))
        {
            throw new ArgumentException("A committed winner ID is required.", nameof(committedWinnerId));
        }

        var strip = RouletteStripPlanner.Plan(
            pool,
            committedWinnerId,
            tileCount,
            landingIndex,
            seed,
            nearMissCandidates,
            nearMissChancePercent).ToArray();
        if (!string.Equals(strip[landingIndex], committedWinnerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The deterministic strip did not preserve the committed winner.");
        }

        var landingX = RouletteAnimationMath.LandingX(viewportWidth, tileWidth, tileSpacing, landingIndex) +
            RouletteAnimationMath.LandingJitterOffset(tileWidth, seed);
        var durationSeconds = RouletteAnimationMath.SpinDurationSeconds(baseDurationSeconds, seed);
        var pacing = new Random(unchecked(seed * 607 + 1297));
        // Keep at least six card strides ahead of the winner, including small test strips.
        var maximumLead = Math.Max(0d, Math.Min(4d * (tileWidth + tileSpacing),
            -landingX - 6d * (tileWidth + tileSpacing)));
        var startX = -Math.Min(pacing.Next(5) * (tileWidth + tileSpacing), maximumLead);
        var accelerationShare = 0.25d + pacing.NextDouble() * 0.10d;
        var decelerationPower = 2.4d + pacing.NextDouble() * 1.2d;

        return new RouletteRevealPlan(
            new ReadOnlyCollection<string>(strip),
            committedWinnerId,
            landingIndex,
            landingX,
            durationSeconds,
            startX,
            accelerationShare,
            decelerationPower,
            reducedMotion ? RouletteMotionMode.Fade : RouletteMotionMode.Scroll);
    }
}

public sealed class RouletteRevealClock
{
    public double RevealElapsedSeconds { get; private set; }

    public bool CanSkip => RevealElapsedSeconds >= OpeningPhaseMachine.SkipDelaySeconds;

    public void AdvancePending(double unscaledDeltaSeconds) =>
        RequireDelta(unscaledDeltaSeconds);

    public void BeginCommittedReveal() => RevealElapsedSeconds = 0d;

    public void AdvanceReveal(double unscaledDeltaSeconds)
    {
        RequireDelta(unscaledDeltaSeconds);
        RevealElapsedSeconds += unscaledDeltaSeconds;
    }

    private static void RequireDelta(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}

public static class OpeningPresentationGuard
{
    public static bool CanActivate(bool disposed, bool eventSystemAvailable) =>
        !disposed && eventSystemAvailable;

    public static bool ShouldFocusSkip(bool wasEnabled, bool isEnabled) =>
        !wasEnabled && isEnabled;
}

public enum OverlayActivationDecision
{
    Reject,
    Reuse,
    Rebuild
}

public static class OverlayTreeLifecycle
{
    public static OverlayActivationDecision DecideActivation(
        bool disposed,
        bool eventSystemAvailable,
        bool rootAvailable,
        bool essentialSubtreeAvailable)
    {
        if (disposed || !eventSystemAvailable)
        {
            return OverlayActivationDecision.Reject;
        }

        return IsPresentationAvailable(rootAvailable, essentialSubtreeAvailable)
            ? OverlayActivationDecision.Reuse
            : OverlayActivationDecision.Rebuild;
    }

    public static bool IsPresentationAvailable(
        bool rootAvailable,
        bool essentialSubtreeAvailable) =>
        rootAvailable && essentialSubtreeAvailable;
}

public enum CosmeticOutcomeSelection
{
    CycleRewards,
    ScavGrade,
    Contractor,
    Restricted,
    BlackLabel,
    Uncommon,
    RelayUpgradePreview,
    RelaySidegradePreview,
    RelayConfiscationPreview
}

public enum CosmeticRelayPreviewKind
{
    Upgrade,
    Sidegrade,
    Confiscation
}

public sealed class CosmeticRelayPreviewPlan
{
    public CosmeticRelayPreviewPlan(CosmeticRelayPreviewKind kind)
    {
        Kind = kind;
    }

    public CosmeticRelayPreviewKind Kind { get; }

    public RelayInteractionOrigin Origin => RelayInteractionOrigin.CosmeticPreview;
}

public static class CosmeticPreviewPlanner
{
    public static bool TryCreateRelay(
        CosmeticOutcomeSelection selection,
        out CosmeticRelayPreviewPlan? plan)
    {
        var kind = selection switch
        {
            CosmeticOutcomeSelection.RelayUpgradePreview => CosmeticRelayPreviewKind.Upgrade,
            CosmeticOutcomeSelection.RelaySidegradePreview => CosmeticRelayPreviewKind.Sidegrade,
            CosmeticOutcomeSelection.RelayConfiscationPreview => CosmeticRelayPreviewKind.Confiscation,
            _ => (CosmeticRelayPreviewKind?)null
        };
        plan = kind is null ? null : new CosmeticRelayPreviewPlan(kind.Value);
        return plan is not null;
    }
}

public readonly record struct CosmeticSelfTestRequest(
    bool TestingMode,
    CosmeticOutcomeSelection Selection,
    double DurationSeconds);

public readonly record struct CosmeticSelfTestLaunchResult(bool Started, string Message);

public static class CosmeticSelfTestPolicy
{
    public const double MinimumDurationSeconds = 0.25d;
    public const double MaximumDurationSeconds = 3d;

    public static CosmeticSelfTestLaunchResult? ProcessTrigger(
        bool requested,
        Action consume,
        CosmeticSelfTestRequest request,
        Func<CosmeticSelfTestRequest, CosmeticSelfTestLaunchResult>? launch)
    {
        if (!requested)
        {
            return null;
        }

        if (consume is null)
        {
            throw new ArgumentNullException(nameof(consume));
        }

        consume();
        return launch?.Invoke(request) ?? new CosmeticSelfTestLaunchResult(
            false,
            "Contraband Cases is not ready for a cosmetic self-test.");
    }

    public static bool CanStart(
        bool testingMode,
        bool disposed,
        bool openingActive,
        bool selfTestActive,
        bool catalogReady) =>
        testingMode &&
        !disposed &&
        !openingActive &&
        !selfTestActive &&
        catalogReady;

    public static double NormalizeDuration(double requestedSeconds)
    {
        if (double.IsNaN(requestedSeconds) || double.IsInfinity(requestedSeconds))
        {
            return 1d;
        }

        return Math.Max(
            MinimumDurationSeconds,
            Math.Min(MaximumDurationSeconds, requestedSeconds));
    }

    public static ValidatedReward SelectReward(
        IReadOnlyList<ValidatedReward> rewards,
        CosmeticOutcomeSelection selection,
        int cycleIndex)
    {
        if (rewards is null)
        {
            throw new ArgumentNullException(nameof(rewards));
        }

        if (rewards.Count == 0)
        {
            throw new ArgumentException("At least one cosmetic reward is required.", nameof(rewards));
        }

        if (selection == CosmeticOutcomeSelection.CycleRewards)
        {
            var index = (int)((uint)cycleIndex % (uint)rewards.Count);
            return rewards[index];
        }

        var rarity = selection switch
        {
            CosmeticOutcomeSelection.ScavGrade => RewardRarity.ScavGrade,
            CosmeticOutcomeSelection.Uncommon => RewardRarity.Uncommon,
            CosmeticOutcomeSelection.Contractor => RewardRarity.Contractor,
            CosmeticOutcomeSelection.Restricted => RewardRarity.Restricted,
            CosmeticOutcomeSelection.BlackLabel => RewardRarity.BlackLabel,
            _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, null)
        };
        return rewards.FirstOrDefault(reward => reward.Rarity == rarity)
            ?? throw new InvalidOperationException(
                $"The cosmetic catalog has no {selection} reward to display.");
    }
}

public static class OpeningScreenTeardownPolicy
{
    public static bool ShouldDetach(
        bool disposed,
        bool hasActiveRun,
        bool presentationDetached) =>
        !disposed && hasActiveRun && !presentationDetached;
}

public static class CatalogReadinessDiagnostics
{
    public static string PublicTerminalMessage(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var category = exception switch
        {
            FileNotFoundException => "catalog file missing",
            UnauthorizedAccessException => "catalog access denied",
            RewardCatalogValidationException => "catalog validation failed",
            IOException => "catalog read failed",
            _ => "unexpected catalog load failure"
        };
        return $"Contraband Cases client catalog is terminally invalid; BR-12 dispatch remains disabled ({category}).";
    }

    public static string? DebugDetails(Exception exception, bool debugLogging)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        return debugLogging
            ? $"Contraband Cases client catalog diagnostic: {exception}"
            : null;
    }
}

public enum OpeningCancelIntent
{
    Ignore,
    CancelConfirmation,
    SkipReveal,
    CloseTerminal
}

public static class OpeningCancelPolicy
{
    public static OpeningCancelIntent Resolve(OpeningPhase phase, bool skipEligible) => phase switch
    {
        OpeningPhase.Confirming => OpeningCancelIntent.CancelConfirmation,
        OpeningPhase.Revealing when skipEligible => OpeningCancelIntent.SkipReveal,
        OpeningPhase.Result or OpeningPhase.Failed => OpeningCancelIntent.CloseTerminal,
        _ => OpeningCancelIntent.Ignore
    };
}

public static class DeferredPresentationBinding
{
    public static bool TryApply<T>(
        OpeningRunCleanup cleanup,
        long generation,
        T value,
        Action<T> cache,
        Action<T> bind)
    {
        if (cleanup is null)
        {
            throw new ArgumentNullException(nameof(cleanup));
        }

        if (cache is null)
        {
            throw new ArgumentNullException(nameof(cache));
        }

        if (bind is null)
        {
            throw new ArgumentNullException(nameof(bind));
        }

        if (!cleanup.CanBind(generation))
        {
            return false;
        }

        cache(value);
        if (!cleanup.CanBind(generation))
        {
            return false;
        }

        bind(value);
        return true;
    }

    public static bool TryApply<T>(
        OpeningRunCleanup cleanup,
        long generation,
        T? value,
        Func<T, bool> isUnavailable,
        Action<T?> cache,
        Action<T?> bind)
        where T : class
    {
        if (isUnavailable is null)
        {
            throw new ArgumentNullException(nameof(isUnavailable));
        }

        var availableValue = value is null || isUnavailable(value)
            ? null
            : value;
        return TryApply(cleanup, generation, availableValue, cache, bind);
    }
}

public static class SpriteRevealReadiness
{
    public const double MaximumWaitSeconds = 0.75d;

    public static bool TryPrepareFirstFrame(
        IReadOnlyCollection<Task> visibleSpriteTasks,
        double elapsedSeconds,
        Action cacheCompletedSprites,
        Action showReveal,
        Action bindCachedSprites)
    {
        if (visibleSpriteTasks is null)
        {
            throw new ArgumentNullException(nameof(visibleSpriteTasks));
        }

        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        if (cacheCompletedSprites is null)
        {
            throw new ArgumentNullException(nameof(cacheCompletedSprites));
        }

        if (showReveal is null)
        {
            throw new ArgumentNullException(nameof(showReveal));
        }

        if (bindCachedSprites is null)
        {
            throw new ArgumentNullException(nameof(bindCachedSprites));
        }

        var allCompleted = true;
        foreach (var task in visibleSpriteTasks)
        {
            if (task is null)
            {
                throw new ArgumentException(
                    "Visible sprite tasks cannot contain null entries.",
                    nameof(visibleSpriteTasks));
            }

            allCompleted &= task.IsCompleted;
        }

        if (!allCompleted && elapsedSeconds < MaximumWaitSeconds)
        {
            return false;
        }

        cacheCompletedSprites();
        showReveal();
        bindCachedSprites();
        return true;
    }

    public static bool ShouldEvict(Task task, bool readinessTimedOut = false)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        return task.IsCanceled ||
            task.IsFaulted ||
            readinessTimedOut && !task.IsCompleted;
    }
}
