using System.Collections.Concurrent;
using System.Reflection;
using ContrabandCases.Server.Settlement;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class SptProfileCommitterTests
{
    [Fact]
    public async Task Disk_acknowledgement_uses_native_file_and_hash_helpers_on_a_synthetic_text_file()
    {
        var (save, profile, _) = Fixture();
        var id = profile.ProfileInfo!.ProfileId!.Value;
        var directory = Directory.CreateTempSubdirectory("contraband-delivery-proof-");
        var path = Path.Combine(directory.FullName, "synthetic-delivery.txt");
        var files = new FileUtil();
        var hash = new HashUtil(null!);
        const string text = "Synthetic receipt — partial collection ✓";
        try
        {
            var committer = new SptProfileCommitter(save, async (_, token) =>
            {
                NativeHashes(save)[id] = await hash.GenerateHashForDataAsync(HashingAlgorithm.MD5, text, token);
                await files.WriteFileAsync(path, text, token);
            }, (_, token) => files.ReadFileAsync(path, token),
                (json, token) => hash.GenerateHashForDataAsync(HashingAlgorithm.MD5, json, token));
            await committer.CommitAsync(id, CancellationToken.None);
            Assert.Equal(text, await files.ReadFileAsync(path));
        }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public async Task Cancelled_disk_verification_releases_native_lock_and_discards_unacknowledged_hash()
    {
        var (save, profile, _) = Fixture();
        var id = profile.ProfileInfo!.ProfileId!.Value;
        using var cancelled = new CancellationTokenSource();
        var committer = new SptProfileCommitter(save, (_, _) =>
        {
            NativeHashes(save)[id] = "not-acknowledged";
            return Task.CompletedTask;
        }, (_, _) =>
        {
            cancelled.Cancel();
            return Task.FromCanceled<string>(cancelled.Token);
        }, (_, _) => throw new Xunit.Sdk.XunitException("A cancelled read cannot be hashed"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => committer.CommitAsync(id, cancelled.Token));
        Assert.Equal(1, NativeLocks(save)[id].CurrentCount);
        Assert.False(NativeHashes(save).ContainsKey(id));
    }

    [Fact]
    public async Task Failed_native_write_does_not_leave_a_hash_that_suppresses_future_autosaves()
    {
        var (save, profile, _) = Fixture();
        var id = profile.ProfileInfo!.ProfileId!.Value;
        var hashes = NativeHashes(save);
        var hash = new HashUtil(null!);
        const string json = "{\"synthetic\":\"reward mail and witness\"}";
        var expected = await hash.GenerateHashForDataAsync(HashingAlgorithm.MD5, json);
        var fail = true;
        var writes = 0;
        string? disk = null;
        var committer = new SptProfileCommitter(save, (_, _) =>
        {
            if (!hashes.TryGetValue(id, out var prior) || prior != expected)
            {
                hashes[id] = expected;
                if (fail) throw new IOException("native write failed after caching MD5");
                disk = json; writes++;
            }
            return Task.CompletedTask;
        }, (_, _) => disk is null ? Task.FromException<string>(new FileNotFoundException()) : Task.FromResult(disk),
            (text, token) => hash.GenerateHashForDataAsync(HashingAlgorithm.MD5, text, token));
        await Assert.ThrowsAsync<IOException>(() => committer.CommitAsync(id, CancellationToken.None));
        Assert.False(hashes.ContainsKey(id));
        fail = false;
        await committer.CommitAsync(id, CancellationToken.None);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData("written")]
    [InlineData("cached-failed-autosave")]
    [InlineData("missing-file")]
    [InlineData("modified-file")]
    public async Task Profile_commit_requires_disk_acknowledgement_not_only_the_native_md5_cache(string scenario)
    {
        var (save, profile, _) = Fixture();
        var id = profile.ProfileInfo!.ProfileId!.Value;
        var hashes = NativeHashes(save);
        var hash = new HashUtil(null!);
        const string newProfile = "{\"mail\":\"saved exact reward\",\"witness\":\"committed\"}";
        string? disk = "{\"mail\":null}";
        var writes = 0;
        var committer = new SptProfileCommitter(save, async (profileId, token) =>
        {
            var expected = await hash.GenerateHashForDataAsync(HashingAlgorithm.MD5, newProfile, token);
            // Faithful native ordering: an autosave caches MD5, fails its write,
            // then our explicit native save sees unchanged JSON and skips writing.
            if (scenario == "cached-failed-autosave") hashes[profileId] = expected;
            if (!hashes.TryGetValue(profileId, out var previous) || previous != expected)
            { hashes[profileId] = expected; disk = newProfile; writes++; }
            if (scenario == "missing-file") disk = null;
            if (scenario == "modified-file") disk = "{\"mail\":\"different\"}";
        }, (path, _) =>
        {
            Assert.Equal(Path.Combine("user/profiles/", $"{id}.json"), path);
            Assert.Equal(0, NativeLocks(save)[id].CurrentCount);
            return disk is null ? Task.FromException<string>(new FileNotFoundException()) : Task.FromResult(disk);
        }, (json, token) => hash.GenerateHashForDataAsync(HashingAlgorithm.MD5, json, token));
        if (scenario == "written") await committer.CommitAsync(id, CancellationToken.None);
        else await Assert.ThrowsAnyAsync<IOException>(() => committer.CommitAsync(id, CancellationToken.None));
        Assert.Equal(scenario == "cached-failed-autosave" ? 0 : 1, writes);
        Assert.Equal(1, NativeLocks(save)[id].CurrentCount);
        Assert.Equal(scenario == "written", hashes.ContainsKey(id));
    }

    [Fact]
    public async Task Mutation_lease_blocks_native_save_before_serialization_and_releases_once()
    {
        var (save, profile, committer) = Fixture();
        var id = profile.ProfileInfo!.ProfileId!.Value;
        using var lease = await committer.AcquireMutationLeaseAsync(id, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        // Cancellation while waiting proves that the real native save path is
        // excluded. It never reaches serialization or any filesystem dependency.
        var nativeSave = save.SaveProfileAsync(id, cancel.Token);
        Assert.False(nativeSave.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nativeSave);
        lease!.Dispose();
        lease.Dispose();
        Assert.Equal(1, NativeLocks(save)[id].CurrentCount);
    }

    [Fact]
    public async Task Native_save_lock_blocks_mutation_and_cancel_does_not_release_another_owners_lock()
    {
        var (save, profile, committer) = Fixture();
        var id = profile.ProfileInfo!.ProfileId!.Value;
        var semaphore = NativeLocks(save).GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        using var cancel = new CancellationTokenSource();
        var acquisition = committer.AcquireMutationLeaseAsync(id, cancel.Token).AsTask();
        Assert.False(acquisition.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        Assert.Equal(0, semaphore.CurrentCount);
        semaphore.Release();
        using var acquired = await committer.AcquireMutationLeaseAsync(id, CancellationToken.None);
        Assert.Equal(0, semaphore.CurrentCount);
    }

    [Fact]
    public async Task Profiles_have_independent_mutation_leases()
    {
        var (save, profile, committer) = Fixture();
        var other = new SptProfile { ProfileInfo = new() { ProfileId = new MongoId() } };
        save.AddProfile(other);
        using var first = await committer.AcquireMutationLeaseAsync(profile.ProfileInfo!.ProfileId!.Value, CancellationToken.None);
        var second = committer.AcquireMutationLeaseAsync(other.ProfileInfo.ProfileId!.Value, CancellationToken.None);
        Assert.True(second.IsCompletedSuccessfully);
        using var secondLease = await second;
    }

    [Fact]
    public async Task Invalid_profile_is_not_reported_as_a_successful_save()
    {
        var (_, profile, committer) = Fixture();
        // The native migration service owns this internal setter.
        typeof(Info).GetProperty(nameof(Info.InvalidOrUnloadableProfile))!.SetValue(profile.ProfileInfo, true);
        var id = profile.ProfileInfo!.ProfileId!.Value;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            committer.AcquireMutationLeaseAsync(id, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => committer.CommitAsync(id, CancellationToken.None));
    }

    private static (SaveServer Save, SptProfile Profile, SptProfileCommitter Committer) Fixture()
    {
        // In-memory profile only. No Load method, real save, server or game.
        var save = new SaveServer(null!, [], null!, null!, null!, null!, null!, null!);
        var profile = new SptProfile { ProfileInfo = new() { ProfileId = new MongoId() } };
        save.AddProfile(profile);
        return (save, profile, new SptProfileCommitter(save, new FileUtil(), new HashUtil(null!)));
    }

    private static ConcurrentDictionary<MongoId, SemaphoreSlim> NativeLocks(SaveServer save) =>
        Assert.IsType<ConcurrentDictionary<MongoId, SemaphoreSlim>>(typeof(SaveServer)
            .GetField("saveLocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(save));
    private static ConcurrentDictionary<MongoId, string> NativeHashes(SaveServer save) =>
        Assert.IsType<ConcurrentDictionary<MongoId, string>>(typeof(SaveServer)
            .GetField("saveMd5", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(save));
}
