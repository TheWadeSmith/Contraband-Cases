namespace ContrabandCases.Client.Opening;

public sealed class ManifestSnapshotException : InvalidOperationException
{
    public ManifestSnapshotException(string message)
        : base(message)
    {
    }

    public ManifestSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class ManifestSnapshotEnvelope
{
    public static ManifestSnapshot Parse(string json, string expectedManifestId) =>
        ManifestSnapshotParser.Parse(json, expectedManifestId, snapshotRequired: true)
        ?? throw new ManifestSnapshotException(
            "The Manifest snapshot response did not contain the requested snapshot.");

    public static ManifestCurrentState ParseCurrent(string json) =>
        ManifestSnapshotParser.ParseCurrent(json);
}
