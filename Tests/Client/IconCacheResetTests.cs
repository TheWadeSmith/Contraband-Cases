using ContrabandCases.Client;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class IconCacheResetTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("contraband-icon-reset-").FullName;

    [Fact]
    public void Failed_clear_does_not_mark_success_and_retries_on_the_next_launch()
    {
        var marker = Path.Combine(_directory, IconCacheReset.MarkerFileName);
        var failures = new List<Exception>();
        Assert.False(IconCacheResetGate.RunOnce(marker, () => throw new IOException("Cache locked"), failures.Add));
        Assert.Single(failures);
        Assert.False(File.Exists(marker));

        var clears = 0;
        Assert.True(IconCacheResetGate.RunOnce(marker, () => clears++, failures.Add));
        Assert.True(File.Exists(marker));
        Assert.False(IconCacheResetGate.RunOnce(marker, () => clears++, failures.Add));
        Assert.Equal(1, clears);
    }

    [Fact]
    public void Failed_marker_write_remains_retryable_without_crashing_plugin_load()
    {
        var marker = Path.Combine(_directory, IconCacheReset.MarkerFileName);
        Directory.CreateDirectory(marker); // Simulate an unavailable marker destination.
        var failures = new List<Exception>();
        var clears = 0;
        Assert.False(IconCacheResetGate.RunOnce(marker, () => clears++, failures.Add));
        Assert.Single(failures);
        Assert.False(File.Exists(marker));
        Directory.Delete(marker);
        Assert.True(IconCacheResetGate.RunOnce(marker, () => clears++, failures.Add));
        Assert.Equal(2, clears);
    }

    [Fact]
    public void Legacy_attempt_marker_does_not_suppress_the_corrected_success_reset()
    {
        File.WriteAllText(Path.Combine(_directory, "icon-cache-reset-0.3.20.marker"), "old attempt");
        var clears = 0;
        Assert.True(IconCacheResetGate.RunOnce(Path.Combine(_directory, IconCacheReset.MarkerFileName),
            () => clears++, exception => Assert.Fail(exception.Message)));
        Assert.Equal(1, clears);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(_directory)) Directory.Delete(directory);
        Directory.Delete(_directory);
    }

    [Fact]
    public void Resets_when_no_marker_file_exists_yet()
    {
        Assert.True(IconCacheResetGate.ShouldReset(markerFileExists: false));
    }

    [Fact]
    public void Does_not_reset_again_once_the_marker_file_exists()
    {
        Assert.False(IconCacheResetGate.ShouldReset(markerFileExists: true));
    }
}
