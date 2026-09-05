using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace ContrabandCases.Server.Settlement;

internal sealed record ManifestInputCommitWitnessTokens(string? Ticket, string? RelayKey);

/// <summary>
/// Profile-side crash-recovery evidence for Manifest input consumption. This is
/// an integrity/recovery chain, not a security boundary against local-file tampering.
/// </summary>
internal static class ManifestInputCommitWitness
{
    internal const string TicketExtensionDataKey = "ContrabandCases.ManifestTicketCommitWitness";
    internal const string RelayKeyExtensionDataKey = "ContrabandCases.ManifestRelayKeyCommitWitness";
    internal const string GenesisHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private const int HashLength = 64;
    private const string TicketDomain = "contraband-cases/manifest-ticket-commit-witness/v1";
    private const string RelayKeyDomain = "contraband-cases/manifest-relay-key-commit-witness/v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static ManifestTicketPayload PlanTicket(
        PmcData profile,
        MongoId profileId,
        ManifestTicketPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.ProfileCommitStarted || prepared.Committed || prepared.CommitGeneration is not null)
        {
            throw new ArgumentException("Only a fresh Manifest ticket can receive a commit plan.", nameof(prepared));
        }

        var plan = PlanNext(profile, profileId, TicketExtensionDataKey, "ticket");
        return prepared.WithCommitPlan(plan.Generation, plan.PredecessorHash);
    }

    internal static ManifestRelayKeyPreparation PlanRelayKey(
        PmcData profile,
        MongoId profileId,
        MongoId keyId,
        DateTimeOffset preparedAtUtc)
    {
        var plan = PlanNext(profile, profileId, RelayKeyExtensionDataKey, "Relay key");
        return new ManifestRelayKeyPreparation(
            keyId,
            preparedAtUtc,
            plan.Generation,
            plan.PredecessorHash);
    }

    internal static ManifestCommitWitnessState InspectTicket(
        PmcData profile,
        MongoId profileId,
        string manifestId,
        ManifestTicketPayload prepared) =>
        Inspect(
            profile,
            profileId,
            prepared.CommitGeneration,
            prepared.CommitPredecessorHash,
            TicketExtensionDataKey,
            "ticket",
            () => CreateTicketHash(profileId, manifestId, prepared));

    internal static void StageTicket(
        PmcData profile,
        MongoId profileId,
        string manifestId,
        ManifestTicketPayload prepared) =>
        Stage(
            profile,
            profileId,
            prepared.CommitGeneration,
            prepared.CommitPredecessorHash,
            TicketExtensionDataKey,
            "ticket",
            () => CreateTicketHash(profileId, manifestId, prepared));

    internal static ManifestCommitWitnessState InspectRelayKey(
        PmcData profile,
        MongoId profileId,
        string manifestId,
        RewardForestFingerprintV2 inputFingerprint,
        ManifestRelayPreparedPayload prepared) =>
        Inspect(
            profile,
            profileId,
            prepared.CommitGeneration,
            prepared.CommitPredecessorHash,
            RelayKeyExtensionDataKey,
            "Relay key",
            () => CreateRelayKeyHash(profileId, manifestId, inputFingerprint, prepared));

    internal static void StageRelayKey(
        PmcData profile,
        MongoId profileId,
        string manifestId,
        RewardForestFingerprintV2 inputFingerprint,
        ManifestRelayPreparedPayload prepared) =>
        Stage(
            profile,
            profileId,
            prepared.CommitGeneration,
            prepared.CommitPredecessorHash,
            RelayKeyExtensionDataKey,
            "Relay key",
            () => CreateRelayKeyHash(profileId, manifestId, inputFingerprint, prepared));

    internal static ManifestInputCommitWitnessTokens CaptureTokens(PmcData profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var ticket = ReadStoredToken(profile, TicketExtensionDataKey, "ticket");
        var relayKey = ReadStoredToken(profile, RelayKeyExtensionDataKey, "Relay key");
        if (ticket is not null)
        {
            _ = Parse(ticket, "ticket");
        }
        if (relayKey is not null)
        {
            _ = Parse(relayKey, "Relay key");
        }
        return new ManifestInputCommitWitnessTokens(ticket, relayKey);
    }

    internal static void RestoreTokens(PmcData profile, ManifestInputCommitWitnessTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tokens);
        RestoreToken(profile, TicketExtensionDataKey, tokens.Ticket, "ticket");
        RestoreToken(profile, RelayKeyExtensionDataKey, tokens.RelayKey, "Relay key");
    }

    internal static void ValidateGeneration(long generation, string parameterName)
    {
        if (generation < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                generation,
                "Manifest input commit generations begin at one.");
        }
    }

    internal static void ValidateHash(string hash, string parameterName)
    {
        if (hash is null || hash.Length != HashLength ||
            hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 hash is required.", parameterName);
        }
    }

    private static (long Generation, string PredecessorHash) PlanNext(
        PmcData profile,
        MongoId profileId,
        string extensionDataKey,
        string operation)
    {
        var head = ReadHead(profile, profileId, extensionDataKey, operation);
        return (head is null ? 1 : checked(head.Generation + 1), head?.CommitHash ?? GenesisHash);
    }

    private static ManifestCommitWitnessState Inspect(
        PmcData profile,
        MongoId profileId,
        long? commitGeneration,
        string? commitPredecessorHash,
        string extensionDataKey,
        string operation,
        Func<string> createCommitHash)
    {
        var generation = commitGeneration
            ?? throw new InvalidOperationException($"The active Manifest {operation} has no durable commit generation.");
        var predecessorHash = commitPredecessorHash
            ?? throw new InvalidOperationException($"The active Manifest {operation} has no durable predecessor hash.");
        ValidateGeneration(generation, nameof(commitGeneration));
        ValidateHash(predecessorHash, nameof(commitPredecessorHash));

        var head = ReadHead(profile, profileId, extensionDataKey, operation);
        var expectedHash = createCommitHash();
        if (head is not null && head.Generation == generation &&
            string.Equals(head.PredecessorHash, predecessorHash, StringComparison.Ordinal) &&
            string.Equals(head.CommitHash, expectedHash, StringComparison.Ordinal))
        {
            return ManifestCommitWitnessState.Current;
        }
        if (head is null && generation == 1 &&
            string.Equals(predecessorHash, GenesisHash, StringComparison.Ordinal) ||
            head is not null && head.Generation == generation - 1 &&
            string.Equals(head.CommitHash, predecessorHash, StringComparison.Ordinal))
        {
            return ManifestCommitWitnessState.Predecessor;
        }
        return ManifestCommitWitnessState.Other;
    }

    private static void Stage(
        PmcData profile,
        MongoId profileId,
        long? commitGeneration,
        string? commitPredecessorHash,
        string extensionDataKey,
        string operation,
        Func<string> createCommitHash)
    {
        var state = Inspect(
            profile,
            profileId,
            commitGeneration,
            commitPredecessorHash,
            extensionDataKey,
            operation,
            createCommitHash);
        if (state != ManifestCommitWitnessState.Predecessor)
        {
            throw new InvalidOperationException(
                $"The Manifest {operation} commit witness does not have the expected predecessor head.");
        }

        profile.ExtensionData ??= [];
        profile.ExtensionData[extensionDataKey] = Serialize(new ManifestInputCommitWitnessHead(
            profileId,
            commitGeneration!.Value,
            commitPredecessorHash!,
            createCommitHash()));
    }

    private static string CreateTicketHash(
        MongoId profileId,
        string manifestId,
        ManifestTicketPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        manifestId = ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        var generation = prepared.CommitGeneration
            ?? throw new InvalidOperationException("The active Manifest ticket has no durable commit generation.");
        var predecessorHash = prepared.CommitPredecessorHash
            ?? throw new InvalidOperationException("The active Manifest ticket has no durable predecessor hash.");

        using var canonical = new MemoryStream();
        // Preserve byte-for-byte witnesses for existing mixed-case journals.
        var themed = prepared.CaseTemplateId != ContrabandCases.Shared.ModConstants.CaseTemplateId;
        WriteString(canonical, themed ? TicketDomain + "/themed-v1" : TicketDomain);
        if (themed) WriteString(canonical, prepared.CaseTemplateId);
        WriteString(canonical, profileId.ToString());
        WriteString(canonical, manifestId);
        WriteInt64(canonical, generation);
        WriteString(canonical, predecessorHash);
        WriteString(canonical, prepared.CaseId.ToString());
        WriteString(canonical, prepared.KeyId.ToString());
        WriteString(canonical, prepared.PreparedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        return Hash(canonical);
    }

    private static string CreateRelayKeyHash(
        MongoId profileId,
        string manifestId,
        RewardForestFingerprintV2 inputFingerprint,
        ManifestRelayPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(inputFingerprint);
        ArgumentNullException.ThrowIfNull(prepared);
        manifestId = ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        var generation = prepared.CommitGeneration
            ?? throw new InvalidOperationException("The active Manifest Relay has no durable commit generation.");
        var predecessorHash = prepared.CommitPredecessorHash
            ?? throw new InvalidOperationException("The active Manifest Relay has no durable predecessor hash.");

        using var canonical = new MemoryStream();
        WriteString(canonical, RelayKeyDomain);
        WriteString(canonical, profileId.ToString());
        WriteString(canonical, manifestId);
        WriteString(canonical, inputFingerprint.Sha256Hex);
        WriteInt64(canonical, generation);
        WriteString(canonical, predecessorHash);
        WriteString(canonical, prepared.KeyId.ToString());
        WriteString(canonical, prepared.PreparedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        WriteInt32(canonical, (int)prepared.Outcome);
        WriteOptionalEntitlement(canonical, prepared.Output);
        WriteInt32(canonical, prepared.Odds.UpgradePercent);
        WriteInt32(canonical, prepared.Odds.SidegradePercent);
        WriteInt32(canonical, prepared.Odds.ConfiscatePercent);
        WriteRng(canonical, prepared.OutcomeRng);
        WriteOptionalRng(canonical, prepared.TargetRng);
        WriteInt32(canonical, prepared.BrokerFavorBefore);
        WriteInt32(canonical, prepared.BrokerFavorAfter);
        WriteInt32(canonical, prepared.NextRelayCandidates.Count);
        foreach (var candidate in prepared.NextRelayCandidates)
        {
            WriteInt32(canonical, (int)candidate.TargetResult);
            WriteInt32(canonical, (int)candidate.Rarity);
            WriteString(canonical, candidate.Fingerprint.Sha256Hex);
        }
        return Hash(canonical);
    }

    private static void WriteOptionalEntitlement(Stream stream, ManifestEntitlementSnapshot? entitlement)
    {
        WriteBoolean(stream, entitlement is not null);
        if (entitlement is null)
        {
            return;
        }
        WriteInt32(stream, (int)entitlement.Rarity);
        WriteString(stream, entitlement.Fingerprint.Sha256Hex);
    }

    private static void WriteRng(Stream stream, CanonicalRngEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        WriteInt32(stream, (int)evidence.Purpose);
        WriteInt32(stream, evidence.DrawOrdinal);
        WriteInt64(stream, evidence.UnitNumerator);
    }

    private static void WriteOptionalRng(Stream stream, CanonicalRngEvidence? evidence)
    {
        WriteBoolean(stream, evidence is not null);
        if (evidence is not null)
        {
            WriteRng(stream, evidence);
        }
    }

    private static ManifestInputCommitWitnessHead? ReadHead(
        PmcData profile,
        MongoId profileId,
        string extensionDataKey,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var token = ReadStoredToken(profile, extensionDataKey, operation);
        if (token is null)
        {
            return null;
        }

        var head = Parse(token, operation);
        if (head.ProfileId != profileId)
        {
            throw new InvalidOperationException(
                $"The profile Manifest {operation} commit witness belongs to a different profile.");
        }
        return head;
    }

    private static string? ReadStoredToken(PmcData profile, string extensionDataKey, string operation)
    {
        if (profile.ExtensionData is null ||
            !profile.ExtensionData.TryGetValue(extensionDataKey, out var raw))
        {
            return null;
        }

        return raw switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => throw new InvalidOperationException(
                $"The profile Manifest {operation} commit witness must be a JSON string.")
        } ?? throw new InvalidOperationException(
            $"The profile Manifest {operation} commit witness must be a JSON string.");
    }

    private static ManifestInputCommitWitnessHead Parse(string token, string operation)
    {
        var parts = token.Split(':');
        if (parts.Length != 5 || !string.Equals(parts[0], "v1", StringComparison.Ordinal) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
        {
            throw new InvalidOperationException(
                $"The profile Manifest {operation} commit witness is malformed or has an unsupported version.");
        }

        try
        {
            var profileId = new MongoId(parts[1]);
            if (profileId.IsEmpty)
            {
                throw new ArgumentException("A profile ID is required.", nameof(token));
            }
            ValidateGeneration(generation, nameof(token));
            ValidateHash(parts[3], nameof(token));
            ValidateHash(parts[4], nameof(token));
            return new ManifestInputCommitWitnessHead(profileId, generation, parts[3], parts[4]);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"The profile Manifest {operation} commit witness is malformed or has an unsupported version.",
                exception);
        }
    }

    private static void RestoreToken(
        PmcData profile,
        string extensionDataKey,
        string? token,
        string operation)
    {
        if (token is null)
        {
            profile.ExtensionData?.Remove(extensionDataKey);
            return;
        }

        _ = Parse(token, operation);
        profile.ExtensionData ??= [];
        profile.ExtensionData[extensionDataKey] = token;
    }

    private static string Serialize(ManifestInputCommitWitnessHead head) => string.Join(
        ':',
        "v1",
        head.ProfileId.ToString(),
        head.Generation.ToString(CultureInfo.InvariantCulture),
        head.PredecessorHash,
        head.CommitHash);

    private static string Hash(MemoryStream canonical) => Convert.ToHexString(
        SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length))))
        .ToLowerInvariant();

    private static void WriteBoolean(Stream stream, bool value) => stream.WriteByte(value ? (byte)1 : (byte)0);

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Utf8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private sealed record ManifestInputCommitWitnessHead(
        MongoId ProfileId,
        long Generation,
        string PredecessorHash,
        string CommitHash);
}
