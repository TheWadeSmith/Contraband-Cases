using System.Globalization;
using System.Security.Cryptography;

namespace ContrabandCases.Shared.Catalog;

public sealed class RewardForestFingerprintV2 : IEquatable<RewardForestFingerprintV2>
{
    public const string Domain = "reward-forest-v2";

    public RewardForestFingerprintV2(string sha256Hex)
    {
        if (sha256Hex is null || sha256Hex.Length != 64 || sha256Hex.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new CargoCatalogValidationException("A lowercase SHA-256 fingerprint is required.");
        }

        Sha256Hex = sha256Hex;
    }

    public string Sha256Hex { get; }

    public static RewardForestFingerprintV2 Compute(
        string providerId,
        string lotId,
        RewardForest forest)
    {
        providerId = CargoDomainValidator.RequireIdentifier(providerId, nameof(providerId));
        lotId = CargoDomainValidator.RequireIdentifier(lotId, nameof(lotId));
        if (forest is null)
        {
            throw new ArgumentNullException(nameof(forest));
        }

        var canonical = RewardForestCanonicalizer.Encode(providerId, lotId, forest.Nodes);
        if (canonical.Length > RewardForest.MaxCanonicalSizeBytes + 2_048)
        {
            throw new CargoCatalogValidationException("Reward fingerprint canonical size exceeds the supported limit.");
        }

        using var sha256 = SHA256.Create();
        return new RewardForestFingerprintV2(ToLowerHex(sha256.ComputeHash(canonical)));
    }

    public bool Equals(RewardForestFingerprintV2? other) =>
        other is not null && string.Equals(Sha256Hex, other.Sha256Hex, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RewardForestFingerprintV2);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Sha256Hex);

    public override string ToString() => Sha256Hex;

    private static string ToLowerHex(byte[] bytes)
    {
        var characters = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = alphabet[bytes[index] >> 4];
            characters[(index * 2) + 1] = alphabet[bytes[index] & 0x0f];
        }

        return new string(characters);
    }
}

internal static class RewardForestCanonicalizer
{
    internal static int GetForestCanonicalSize(IReadOnlyList<RewardForestNode> nodes) =>
        Encode(null, null, nodes).Length;

    internal static byte[] Encode(
        string? providerId,
        string? lotId,
        IReadOnlyList<RewardForestNode> nodes)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, CargoUtf8.StrictEncoding, leaveOpen: true))
        {
            WriteString(writer, DomainFor(providerId));
            if (providerId is not null)
            {
                WriteString(writer, providerId);
                WriteString(writer, lotId!);
            }

            var ordered = nodes
                .OrderBy(node => node.TreeRootPath, StringComparer.Ordinal)
                .ThenBy(node => node.LogicalPath, StringComparer.Ordinal)
                .ToArray();
            writer.Write(ordered.Length);
            foreach (var node in ordered)
            {
                WriteString(writer, node.TreeRootPath);
                WriteString(writer, node.LogicalPath);
                WriteString(writer, node.TemplateId);
                WriteOptionalString(writer, node.ParentLogicalPath);
                WriteOptionalString(writer, node.SlotId);
                writer.Write(node.StackCount);

                writer.Write(node.InternalLocation is not null);
                if (node.InternalLocation is not null)
                {
                    writer.Write(node.InternalLocation.X);
                    writer.Write(node.InternalLocation.Y);
                    writer.Write((int)node.InternalLocation.Rotation);
                }

                writer.Write(node.StableState is not null);
                if (node.StableState is not null)
                {
                    WriteOptionalDecimal(writer, node.StableState.Durability);
                    WriteOptionalDecimal(writer, node.StableState.MaximumDurability);
                    writer.Write(node.StableState.ResourceKind is not null);
                    if (node.StableState.ResourceKind is not null)
                    {
                        writer.Write((int)node.StableState.ResourceKind.Value);
                    }
                    WriteOptionalDecimal(writer, node.StableState.ResourceValue);
                    WriteOptionalDecimal(writer, node.StableState.MaximumResourceValue);
                }
            }
        }

        return stream.ToArray();
    }

    private static string DomainFor(string? providerId) =>
        providerId is null ? RewardForestFingerprintV2.Domain + "/forest" : RewardForestFingerprintV2.Domain;

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = CargoUtf8.GetBytes(value, "Reward fingerprint text");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteOptionalString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteString(writer, value);
        }
    }

    private static void WriteOptionalDecimal(BinaryWriter writer, decimal? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteString(writer, value.Value.ToString("G29", CultureInfo.InvariantCulture));
        }
    }
}
