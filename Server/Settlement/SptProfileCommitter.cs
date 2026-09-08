using System.Collections.Concurrent;
using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace ContrabandCases.Server.Settlement;

[Injectable(InjectionType.Singleton)]
public sealed class SptProfileCommitter : IProfileCommitter
{
    private readonly SaveServer _saveServer;
    private readonly ConcurrentDictionary<MongoId, SemaphoreSlim> _saveLocks;
    private readonly ConcurrentDictionary<MongoId, string> _saveHashes;
    private readonly string _profileDirectory;
    private readonly Func<MongoId, CancellationToken, Task> _save;
    private readonly Func<string, CancellationToken, Task<string>> _readFile;
    private readonly Func<string, CancellationToken, Task<string>> _hash;

    public SptProfileCommitter(SaveServer saveServer, FileUtil fileUtil, HashUtil hashUtil)
        : this(saveServer, (id, token) => saveServer.SaveProfileAsync(id, token),
            fileUtil.ReadFileAsync, (json, token) => hashUtil.GenerateHashForDataAsync(HashingAlgorithm.MD5, json, token)) { }

    internal SptProfileCommitter(SaveServer saveServer, Func<MongoId, CancellationToken, Task> save,
        Func<string, CancellationToken, Task<string>> readFile, Func<string, CancellationToken, Task<string>> hash)
    {
        _saveServer = saveServer ?? throw new ArgumentNullException(nameof(saveServer));
        _save = save;
        _readFile = readFile;
        _hash = hash;
        // SPT 4.1 exposes no mutation lease. Share exactly the semaphore used by
        // SaveProfileAsync (including autosaves), never a parallel lock pool or
        // a replacement serializer. Fail closed if the native contract changes.
        _saveLocks = typeof(SaveServer).GetField("saveLocks", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(saveServer) as ConcurrentDictionary<MongoId, SemaphoreSlim>
            ?? throw new InvalidOperationException(
                "Contraband Cases cannot coordinate reward delivery with this SPT version's profile saves.");
        _saveHashes = typeof(SaveServer).GetField("saveMd5", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(saveServer) as ConcurrentDictionary<MongoId, string>
            ?? throw new InvalidOperationException("This SPT version does not expose a verifiable profile save cache.");
        _profileDirectory = typeof(SaveServer).GetField("profileFilepath", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetRawConstantValue() as string
            ?? throw new InvalidOperationException("This SPT version does not expose its profile storage path.");
    }

    public async ValueTask<IDisposable?> AcquireMutationLeaseAsync(MongoId profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSaveableProfile(profileId);
        var semaphore = _saveLocks.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SaveLease(semaphore);
    }

    public async Task CommitAsync(MongoId profileId, CancellationToken cancellationToken)
    {
        RequireSaveableProfile(profileId);
        try { await _save(profileId, cancellationToken).ConfigureAwait(false); }
        catch
        {
            // Native writes can throw after caching the new hash. Cleanup must
            // finish even when the request was cancelled. Removing a newer
            // successful cache value only forces a harmless extra native write.
            var semaphore = _saveLocks.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _saveHashes.TryRemove(profileId, out _); }
            finally { semaphore.Release(); }
            throw;
        }
        // Native SPT records MD5 BEFORE the asynchronous write. A failed queued
        // autosave can poison that cache, making our own save silently skip the
        // same staged profile. Only matching bytes on disk acknowledge delivery.
        // Reacquire native exclusion so a concurrent writer cannot change the
        // file/cache pair during verification; keep native serialization intact.
        using var lease = await AcquireMutationLeaseAsync(profileId, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_saveHashes.TryGetValue(profileId, out var expected))
                throw new IOException("The native profile save has no acknowledgement hash.");
            var persisted = await _readFile(Path.Combine(_profileDirectory, $"{profileId}.json"), cancellationToken).ConfigureAwait(false);
            var actual = await _hash(persisted, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new IOException("Reward delivery was not acknowledged by the saved profile; restart before retrying.");
        }
        catch
        {
            // Do not leave an unacknowledged hash that would suppress later
            // native autosaves. The settlement caller still quarantines retries.
            _saveHashes.TryRemove(profileId, out _);
            throw;
        }
    }

    private void RequireSaveableProfile(MongoId profileId)
    {
        _ = _saveServer.GetProfile(profileId);
        // Native SaveProfileAsync silently returns for this flag. It must not
        // be mistaken for a successful durable reward delivery.
        if (_saveServer.IsProfileInvalidOrUnloadable(profileId))
            throw new InvalidOperationException("The reward recipient's profile cannot currently be saved.");
    }

    private sealed class SaveLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
