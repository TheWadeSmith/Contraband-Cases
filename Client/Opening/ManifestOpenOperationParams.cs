using ContrabandCases.Shared;
using Newtonsoft.Json;

namespace ContrabandCases.Client.Opening;

internal sealed class ManifestOpenOperationParams
{
    public ManifestOpenOperationParams(string item, string? expectedCatalogSnapshotId)
    {
        Item = ManifestProtocolValidation.RequireIdentifier(item, nameof(item));
        ExpectedCatalogSnapshotId = expectedCatalogSnapshotId is null ? null :
            ManifestProtocolValidation.RequireIdentifier(expectedCatalogSnapshotId, nameof(expectedCatalogSnapshotId));
    }

    public string Action => ModConstants.OpenAction;

    [JsonProperty("item")]
    public string Item { get; }

    [JsonProperty("expectedCatalogSnapshotId")]
    public string? ExpectedCatalogSnapshotId { get; }
}
