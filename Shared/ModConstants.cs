namespace ContrabandCases.Shared;

public static class ModConstants
{
    public const string ModId = "wade.contrabandcases";
    public const string ModName = "Contraband Cases";
    public const string ModVersion = "0.4.14";
    public const string OpenAction = "ContrabandCasesOpen";
    public const string RelaySecureAction = "ContrabandCasesRelaySecure";
    public const string RelayAction = "ContrabandCasesRelay";
    public const string ManifestLockAction = "ContrabandCasesManifestLock";
    public const string ManifestBurnAction = "ContrabandCasesManifestBurn";
    public const string ManifestClaimAction = "ContrabandCasesManifestClaim";
    public const string ManifestRelayAction = "ContrabandCasesManifestRelay";
    public const string ManifestForfeitAction = "ContrabandCasesManifestForfeit";
    public const string TestingInventoryGrantAction = "ContrabandCasesTestingInventoryGrant";
    public const string RelaySnapshotRoute = "/contrabandcases/relay/snapshot";
    public const string RelayPendingRoute = "/contrabandcases/relay/pending";
    public const string ManifestCurrentRoute = "/contrabandcases/manifest/current";
    public const string ManifestSnapshotRoute = "/contrabandcases/manifest/snapshot";
    public const string ManifestLibraryRoute = "/contrabandcases/manifest/library";
    public const string RelayReceiptExtensionKey = "contrabandCasesRelay";
    public const string RuntimeModFolder = "Wade-ContrabandCases";

    public const string CaseTemplateId = "66d000000000000000000001";
    public const string KeyTemplateId = "66d000000000000000000002";
    public const string MechanicCaseAssortRootId = "66d000000000000000000003";

    // MechanicKeyAssortRootId (formerly 66d000000000000000000004) was retired in 0.3.18: the BR-12
    // Relay Key is find-only now and is no longer offered by Mechanic at all.

    public const string CaseBundleKey = "contrabandcases/br12_case.bundle";
    public const string KeyBundleKey = "contrabandcases/br12_key.bundle";

    // Matches config/reward-packs/vault.json's "providerId". Used purely for
    // cosmetic reveal presentation (accent color) -- never for odds or
    // economic logic, which stay entirely server-side.
    public const string VaultProviderId = "vault";
}

public static class TestingInventoryGrantPolicy
{
    public const int MaximumCaseCount = 10;
    public const int MaximumKeyCount = 40;
    public const int MaximumTotalCount = 40;

    public static void Validate(int caseCount, int keyCount)
    {
        if (caseCount is < 0 or > MaximumCaseCount)
        {
            throw new ArgumentOutOfRangeException(nameof(caseCount));
        }

        if (keyCount is < 0 or > MaximumKeyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(keyCount));
        }

        var total = checked(caseCount + keyCount);
        if (total is < 1 or > MaximumTotalCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keyCount),
                "A testing grant must contain between one and forty total items.");
        }
    }
}
