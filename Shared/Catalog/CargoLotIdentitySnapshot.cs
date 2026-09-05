namespace ContrabandCases.Shared.Catalog;

public sealed class CargoLotIdentitySnapshot
{
    public CargoLotIdentitySnapshot(
        string providerId,
        string packVersion,
        string lotId,
        string displayName,
        string purpose,
        FamilyId familyId,
        TrackId trackId,
        string anchorTemplateId,
        double weight,
        UsePath usePath,
        RewardForestFingerprintV2 fingerprint)
    {
        ProviderId = CargoDomainValidator.RequireIdentifier(providerId, nameof(providerId));
        PackVersion = CargoDomainValidator.RequireIdentifier(packVersion, nameof(packVersion));
        LotId = CargoDomainValidator.RequireIdentifier(lotId, nameof(lotId));
        DisplayName = CargoDomainValidator.RequireText(displayName, nameof(displayName));
        Purpose = CargoDomainValidator.RequireText(purpose, nameof(purpose));
        FamilyId = familyId ?? throw new CargoCatalogValidationException("A family ID is required.");
        TrackId = trackId ?? throw new CargoCatalogValidationException("A track ID is required.");
        AnchorTemplateId = CargoDomainValidator.RequireIdentifier(anchorTemplateId, nameof(anchorTemplateId));
        Weight = CargoDomainValidator.RequirePositiveFinite(weight, nameof(weight));
        UsePath = usePath ?? throw new CargoCatalogValidationException("A use path is required.");
        Fingerprint = fingerprint ?? throw new CargoCatalogValidationException("A reward forest fingerprint is required.");
    }

    public string ProviderId { get; }

    public string PackVersion { get; }

    public string LotId { get; }

    public string DisplayName { get; }

    public string Purpose { get; }

    public FamilyId FamilyId { get; }

    public TrackId TrackId { get; }

    public string AnchorTemplateId { get; }

    public double Weight { get; }

    public UsePath UsePath { get; }

    public RewardForestFingerprintV2 Fingerprint { get; }

    public static CargoLotIdentitySnapshot Capture(
        CargoLotDefinition definition,
        RewardForestFingerprintV2 fingerprint)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        return new CargoLotIdentitySnapshot(
            definition.ProviderId,
            definition.PackVersion,
            definition.LotId,
            definition.DisplayName,
            definition.Purpose,
            definition.FamilyId,
            definition.TrackId,
            definition.AnchorTemplateId,
            definition.Weight,
            definition.UsePath,
            fingerprint);
    }
}
