using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace ContrabandCases.Server.Settlement;

internal enum ManifestClaimCommitWitnessInspection
{
    Predecessor,
    Current,
    Other,
    Missing = Predecessor,
    Matching = Current
}

/// <summary>Crash-recovery chain head, not a security boundary against local-file tampering.</summary>
internal sealed record ManifestClaimCommitWitnessHead(MongoId ProfileId, long Generation, string PreviousHash, string CommitHash);

internal static class ManifestClaimCommitWitness
{
    internal const string ExtensionDataKey = "ContrabandCases.ManifestClaimCommitWitness";
    internal const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private const int HashLength = 64;
    private const string Domain = "contraband-cases/manifest-claim-commit-witness/v2";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static ManifestClaimPreparedPayload PlanNext(PmcData profile, MongoId profileId, ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.ProfileCommitStarted || prepared.CommitGeneration is not null)
            throw new ArgumentException("Only a fresh Claim can receive a commit plan.", nameof(prepared));
        var head = ReadHead(profile, profileId);
        return prepared.WithCommitPlan(head is null ? 1 : checked(head.Generation + 1), head?.CommitHash ?? GenesisHash);
    }

    // Compatibility helpers for old persisted-test fixtures. Production Claim
    // recovery uses the v2 overloads below and never accepts this format.
    internal static string CreateToken(string manifestId, RewardForestFingerprintV2 fingerprint, ManifestClaimPreparedPayload prepared)
    {
        using var stream = new MemoryStream();
        WriteString(stream, "contraband-cases/manifest-claim-commit-witness/v1");
        WriteString(stream, manifestId); WriteString(stream, fingerprint.Sha256Hex);
        WriteString(stream, prepared.PreparedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        WriteIds(stream, prepared.ExactItemIds); WriteIds(stream, prepared.RootIds);
        return "v1:" + Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
    }

    internal static ManifestClaimCommitWitnessInspection Inspect(PmcData profile, string expectedToken)
    {
        try { ValidateLegacyToken(expectedToken); }
        catch (ArgumentException error) { throw new InvalidOperationException("The profile Claim commit witness is malformed or has an unsupported version.", error); }
        var stored = ReadStoredToken(profile);
        if (stored is null) return ManifestClaimCommitWitnessInspection.Missing;
        try { ValidateLegacyToken(stored); }
        catch (ArgumentException error) { throw new InvalidOperationException("The profile Claim commit witness is malformed or has an unsupported version.", error); }
        return stored == expectedToken ? ManifestClaimCommitWitnessInspection.Matching : ManifestClaimCommitWitnessInspection.Other;
    }

    internal static void Stage(PmcData profile, string token)
    {
        ValidateLegacyToken(token);
        var existing = ReadStoredToken(profile);
        if (existing is not null)
        {
            try { ValidateLegacyToken(existing); }
            catch (ArgumentException error) { throw new InvalidOperationException("The profile Claim commit witness is malformed or has an unsupported version.", error); }
        }
        profile.ExtensionData ??= []; profile.ExtensionData[ExtensionDataKey] = token;
    }

    internal static string CreateCommitHash(MongoId profileId, string manifestId, RewardForestFingerprintV2 fingerprint, ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(prepared);
        var generation = prepared.CommitGeneration ?? throw new ArgumentException("Claim commit generation is required.", nameof(prepared));
        var previousHash = prepared.CommitPredecessorHash ?? throw new ArgumentException("Claim commit predecessor hash is required.", nameof(prepared));
        ValidateGeneration(generation, nameof(prepared));
        ValidateHash(previousHash, nameof(prepared));
        manifestId = ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        using var canonical = new MemoryStream();
        WriteString(canonical, Domain); WriteString(canonical, profileId.ToString()); WriteString(canonical, manifestId);
        WriteString(canonical, fingerprint.Sha256Hex); WriteInt64(canonical, generation); WriteString(canonical, previousHash);
        WriteString(canonical, prepared.PreparedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        WriteIds(canonical, prepared.ExactItemIds); WriteIds(canonical, prepared.RootIds);
        return Convert.ToHexString(SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length)))).ToLowerInvariant();
    }

    internal static ManifestClaimCommitWitnessInspection Inspect(PmcData profile, MongoId profileId, string manifestId, RewardForestFingerprintV2 fingerprint, ManifestClaimPreparedPayload prepared)
    {
        var head = ReadHead(profile, profileId);
        var generation = prepared.CommitGeneration ?? throw new InvalidOperationException("An active Claim has no durable commit generation.");
        var previousHash = prepared.CommitPredecessorHash ?? throw new InvalidOperationException("An active Claim has no durable commit predecessor hash.");
        var expectedHash = CreateCommitHash(profileId, manifestId, fingerprint, prepared);
        if (head is not null && head.Generation == generation && head.PreviousHash == previousHash && head.CommitHash == expectedHash)
            return ManifestClaimCommitWitnessInspection.Current;
        if ((head is null && generation == 1 && previousHash == GenesisHash) ||
            (head is not null && head.Generation == generation - 1 && head.CommitHash == previousHash))
            return ManifestClaimCommitWitnessInspection.Predecessor;
        return ManifestClaimCommitWitnessInspection.Other;
    }

    internal static void Stage(PmcData profile, MongoId profileId, string manifestId, RewardForestFingerprintV2 fingerprint, ManifestClaimPreparedPayload prepared)
    {
        if (Inspect(profile, profileId, manifestId, fingerprint, prepared) != ManifestClaimCommitWitnessInspection.Predecessor)
            throw new InvalidOperationException("Claim commit witness does not have the expected predecessor head.");
        profile.ExtensionData ??= [];
        profile.ExtensionData[ExtensionDataKey] = Serialize(new(profileId, prepared.CommitGeneration!.Value, prepared.CommitPredecessorHash!, CreateCommitHash(profileId, manifestId, fingerprint, prepared)));
    }

    internal static string? CaptureToken(PmcData profile) => ReadStoredToken(profile);
    internal static void RestoreToken(PmcData profile, string? token)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (token is null) { profile.ExtensionData?.Remove(ExtensionDataKey); return; }
        if (token.StartsWith("v1:", StringComparison.Ordinal)) ValidateLegacyToken(token); else _ = Parse(token);
        profile.ExtensionData ??= []; profile.ExtensionData[ExtensionDataKey] = token;
    }

    internal static ManifestClaimCommitWitnessHead? ReadHead(PmcData profile, MongoId profileId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var token = ReadStoredToken(profile); if (token is null) return null;
        var head = Parse(token);
        if (head.ProfileId != profileId) throw new InvalidOperationException("The profile Claim commit witness belongs to a different profile.");
        return head;
    }

    private static string? ReadStoredToken(PmcData profile)
    {
        if (profile.ExtensionData is null || !profile.ExtensionData.TryGetValue(ExtensionDataKey, out var raw)) return null;
        return raw switch { string value => value, JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(), _ => throw new InvalidOperationException("The profile Claim commit witness must be a JSON string.") }
            ?? throw new InvalidOperationException("The profile Claim commit witness must be a JSON string.");
    }

    private static ManifestClaimCommitWitnessHead Parse(string token)
    {
        var parts = token.Split(':');
        if (parts.Length != 5 || parts[0] != "v2" || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
            throw new InvalidOperationException("The profile Claim commit witness is malformed or has an unsupported version.");
        try { var id = new MongoId(parts[1]); if (id.IsEmpty) throw new ArgumentException(); ValidateGeneration(generation, nameof(token)); ValidateHash(parts[3], nameof(token)); ValidateHash(parts[4], nameof(token)); return new(id, generation, parts[3], parts[4]); }
        catch (ArgumentException error) { throw new InvalidOperationException("The profile Claim commit witness is malformed or has an unsupported version.", error); }
    }

    private static string Serialize(ManifestClaimCommitWitnessHead head) => string.Join(':', "v2", head.ProfileId.ToString(), head.Generation.ToString(CultureInfo.InvariantCulture), head.PreviousHash, head.CommitHash);
    private static void ValidateLegacyToken(string token)
    {
        if (token is null || token.Length != 67 || !token.StartsWith("v1:", StringComparison.Ordinal))
            throw new ArgumentException("A v1 lowercase SHA-256 Claim witness is required.", nameof(token));
        ValidateHash(token[3..], nameof(token));
    }
    internal static void ValidateGeneration(long generation, string name) { if (generation < 1) throw new ArgumentOutOfRangeException(name, generation, "Claim generations begin at one."); }
    internal static void ValidateHash(string hash, string name) { if (hash is null || hash.Length != HashLength || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("A lowercase SHA-256 hash is required.", name); }
    private static void WriteIds(Stream stream, IReadOnlyList<MongoId> ids) { Span<byte> count = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(count, ids.Count); stream.Write(count); foreach (var id in ids) WriteString(stream, id.ToString()); }
    private static void WriteInt64(Stream stream, long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); stream.Write(bytes); }
    private static void WriteString(Stream stream, string value) { var bytes = Utf8.GetBytes(value); Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); stream.Write(length); stream.Write(bytes); }
}
