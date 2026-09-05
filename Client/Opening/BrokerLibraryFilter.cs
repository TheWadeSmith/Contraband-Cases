namespace ContrabandCases.Client.Opening;

internal static class BrokerLibraryFilter
{
    internal static IReadOnlyList<ManifestLotSnapshot> Lots(ManifestLibrarySnapshot library, string caseId, string family,
        string search = "")
    {
        HashSet<string>? permitted = null;
        if (!string.IsNullOrEmpty(caseId))
        {
            if (!library.Cases.TryGetValue(caseId, out var ids)) return Array.Empty<ManifestLotSnapshot>();
            permitted = new HashSet<string>(ids, StringComparer.Ordinal);
        }
        var query = search.Trim();
        bool Contains(string text) => text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        return library.Lots.Where(lot => (permitted is null || permitted.Contains($"{lot.ProviderId}/{lot.LotId}")) &&
                (string.IsNullOrEmpty(family) || family == lot.FamilyId) &&
                (query.Length == 0 || Contains(lot.DisplayName) || Contains(lot.Purpose) ||
                 Contains(lot.ProviderLabel) || lot.Contents.Any(item => Contains(item.DisplayName))))
            .OrderBy(lot => lot.DisplayName, StringComparer.Ordinal).ToArray();
    }
}

/// <summary>Two rows, with wider cards when the screen has less room for readable text.</summary>
internal readonly struct BrokerLibraryLayout
{
    private BrokerLibraryLayout(int columns) => Columns = columns;
    internal int Columns { get; }
    internal int PageSize => Columns * 2;
    internal int LastPage(int count) => Math.Max(0, (count - 1) / PageSize);
    internal static BrokerLibraryLayout ForScreen(int width, int height) => new(width >= 1600 && height >= 900 ? 3 : 2);
}
