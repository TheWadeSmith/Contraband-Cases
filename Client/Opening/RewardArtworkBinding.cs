using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Client.Opening;

// One screen's hero-art priority: exact anchor, then an actual lot item, then
// the rarity seal supplied by the view. Failed/late loads never erase good art.
internal sealed class RewardArtworkBinding
{
    private readonly HashSet<string> _contentIds;
    private int _priority;

    public RewardArtworkBinding(string anchorId, RewardRarity grade, IEnumerable<string>? contentIds = null)
    {
        AnchorId = anchorId;
        Grade = grade;
        _contentIds = new HashSet<string>(contentIds ?? [], StringComparer.Ordinal);
    }

    public string AnchorId { get; }
    public RewardRarity Grade { get; }

    public static RewardArtworkBinding ForLot(ManifestLotSnapshot lot) =>
        new($"lot:{lot.Fingerprint}", lot.Grade, lot.Contents.Select(c => $"content:{c.TemplateId}"));

    public bool TryAccept(string tileId, bool available)
    {
        if (!available) return false;
        var priority = tileId == AnchorId ? 2 : _contentIds.Contains(tileId) ? 1 : 0;
        if (priority <= _priority) return false;
        _priority = priority;
        return true;
    }
}
