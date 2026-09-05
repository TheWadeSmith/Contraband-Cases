namespace ContrabandCases.Client.Opening;

internal sealed class ManifestLibrarySnapshot
{
    public ManifestLibrarySnapshot(string dossier, IReadOnlyList<ManifestLotSnapshot> lots,
        bool? hasPending = null, string status = "Server status requires the updated server mod.",
        IReadOnlyDictionary<string, IReadOnlyList<string>>? cases = null)
    {
        Dossier = dossier;
        Lots = lots;
        HasPending = hasPending;
        Status = status;
        Cases = cases ?? new Dictionary<string, IReadOnlyList<string>>();
    }

    public string Dossier { get; }
    public IReadOnlyList<ManifestLotSnapshot> Lots { get; }
    public bool? HasPending { get; }
    public string Status { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Cases { get; }
}
