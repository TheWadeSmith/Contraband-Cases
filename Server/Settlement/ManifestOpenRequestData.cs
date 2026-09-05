using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Inventory;

namespace ContrabandCases.Server.Settlement;

public sealed record ManifestOpenRequestData : OpenRandomLootContainerRequestData
{
    [JsonPropertyName("expectedCatalogSnapshotId")]
    public string? ExpectedCatalogSnapshotId { get; set; }
}
