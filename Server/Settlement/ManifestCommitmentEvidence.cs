using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Settlement;

/// <summary>
/// Immutable commit/reveal evidence for the offers fixed before ticket spend.
/// The hash is safe to publish while <see cref="NonceHex"/> remains private, then
/// the nonce and persisted manifest can be revealed for independent verification.
/// </summary>
public sealed class ManifestCommitmentEvidence
{
    public const int CurrentVersion = 1;
    public const int NonceByteCount = 32;
    public const string Algorithm = "SHA-256";
    public const string Domain = "contraband-cases/manifest-commitment";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ManifestCommitmentEvidence(
        int version,
        string commitmentSha256Hex,
        string nonceHex)
    {
        if (version != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "The manifest commitment version is not supported.");
        }

        Version = version;
        CommitmentSha256Hex = RequireLowerHex(commitmentSha256Hex, 32, nameof(commitmentSha256Hex));
        NonceHex = RequireLowerHex(nonceHex, NonceByteCount, nameof(nonceHex));
    }

    public int Version { get; }

    /// <summary>The value published before the ticket inventory mutation begins.</summary>
    public string CommitmentSha256Hex { get; }

    /// <summary>The persisted secret revealed after offer settlement.</summary>
    public string NonceHex { get; }

    public static ManifestCommitmentEvidence Create(
        string manifestId,
        string catalogSnapshotId,
        string nonceHex,
        IEnumerable<ManifestOfferSnapshot> offers)
    {
        var canonicalNonce = RequireLowerHex(nonceHex, NonceByteCount, nameof(nonceHex));
        var digest = ComputeDigest(
            CurrentVersion,
            manifestId,
            catalogSnapshotId,
            Convert.FromHexString(canonicalNonce),
            offers);
        return new ManifestCommitmentEvidence(
            CurrentVersion,
            Convert.ToHexStringLower(digest),
            canonicalNonce);
    }

    public static ManifestCommitmentEvidence CreateWithRandomNonce(
        string manifestId,
        string catalogSnapshotId,
        IEnumerable<ManifestOfferSnapshot> offers) =>
        Create(
            manifestId,
            catalogSnapshotId,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(NonceByteCount)),
            offers);

    public bool VerifyReveal(
        string manifestId,
        string catalogSnapshotId,
        IEnumerable<ManifestOfferSnapshot> offers)
    {
        var actual = ComputeDigest(
            Version,
            manifestId,
            catalogSnapshotId,
            Convert.FromHexString(NonceHex),
            offers);
        var expected = Convert.FromHexString(CommitmentSha256Hex);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ComputeDigest(
        int version,
        string manifestId,
        string catalogSnapshotId,
        byte[] nonce,
        IEnumerable<ManifestOfferSnapshot> offers)
    {
        manifestId = ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        catalogSnapshotId = ManifestRecordValidation.RequireIdentifier(catalogSnapshotId, nameof(catalogSnapshotId));
        ArgumentNullException.ThrowIfNull(offers);
        var orderedOffers = offers.ToArray();
        if (orderedOffers.Length is not (1 or ManifestRecord.OfferCount) ||
            orderedOffers.Any(offer => offer is null) ||
            !orderedOffers.Select(offer => offer.Ordinal).SequenceEqual(Enumerable.Range(1, orderedOffers.Length)))
        {
            throw new ArgumentException("A commitment requires one or three offers in ordinal order.", nameof(offers));
        }

        using var stream = new MemoryStream();
        WriteField(stream, 0x01, StrictUtf8.GetBytes(Domain));
        WriteUInt32Field(stream, 0x02, checked((uint)version));
        WriteTextField(stream, 0x03, manifestId);
        WriteTextField(stream, 0x04, catalogSnapshotId);
        WriteField(stream, 0x05, nonce);
        WriteUInt32Field(stream, 0x06, orderedOffers.Length);

        foreach (var offer in orderedOffers)
        {
            ManifestRecordValidation.ValidateRarity(offer.Rarity, nameof(offers));
            WriteUInt32Field(stream, 0x10, checked((uint)offer.Ordinal));
            WriteTextField(stream, 0x11, offer.Identity.FamilyId.Value);
            WriteTextField(stream, 0x12, offer.Identity.ProviderId);
            WriteTextField(stream, 0x13, offer.Identity.LotId);
            WriteField(stream, 0x14, Convert.FromHexString(offer.Fingerprint.Sha256Hex));
            WriteField(stream, 0x15, [RarityCode(offer.Rarity)]);
            WriteField(stream, 0x16, [RngPurposeCode(offer.RngEvidence.Purpose)]);
            WriteUInt32Field(stream, 0x17, checked((uint)offer.RngEvidence.DrawOrdinal));
            WriteUInt64Field(stream, 0x18, checked((ulong)offer.RngEvidence.UnitNumerator));
        }

        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static void WriteTextField(Stream stream, byte tag, string value)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Manifest commitment text must contain valid Unicode scalar values.", nameof(value));
        }
        WriteField(stream, tag, bytes);
    }

    private static void WriteUInt32Field(Stream stream, byte tag, int value) =>
        WriteUInt32Field(stream, tag, checked((uint)value));

    private static void WriteUInt32Field(Stream stream, byte tag, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        WriteField(stream, tag, bytes);
    }

    private static void WriteUInt64Field(Stream stream, byte tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        WriteField(stream, tag, bytes);
    }

    private static void WriteField(Stream stream, byte tag, ReadOnlySpan<byte> value)
    {
        stream.WriteByte(tag);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        stream.Write(length);
        stream.Write(value);
    }

    private static byte RarityCode(RewardRarity rarity) => rarity switch
    {
        RewardRarity.ScavGrade => 0x01,
        RewardRarity.Contractor => 0x02,
        RewardRarity.Restricted => 0x03,
        RewardRarity.BlackLabel => 0x04,
        RewardRarity.Uncommon => 0x05,
        _ => throw new ArgumentOutOfRangeException(nameof(rarity))
    };

    private static byte RngPurposeCode(ManifestRngPurpose purpose) => purpose switch
    {
        ManifestRngPurpose.OfferSelection => 0x01,
        ManifestRngPurpose.RelayOutcome => 0x02,
        ManifestRngPurpose.RelayTargetSelection => 0x03,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    private static string RequireLowerHex(string? value, int byteCount, string parameterName)
    {
        if (value is null || value.Length != checked(byteCount * 2) || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException($"A canonical lowercase {byteCount}-byte hexadecimal value is required.", parameterName);
        }

        return value;
    }
}
