using Comfort.Common;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using EFT;

namespace ContrabandCases.Client.Opening;

internal sealed class TestingInventoryGrantDispatcher
{
    private bool _pending;

    public bool IsPending => _pending;

    public bool TryDispatch(
        bool enabled,
        bool presentationBusy,
        int caseCount,
        int keyCount,
        TestingCrateType crateType,
        Callback callback,
        out string message)
    {
        if (!enabled)
        {
            message = "Enable Inventory Grant Controls before requesting testing items.";
            return false;
        }

        if (_pending)
        {
            message = "A Contraband Cases testing grant is already pending.";
            return false;
        }

        if (presentationBusy)
        {
            message = "Close the active Contraband Cases opening or cosmetic preview first.";
            return false;
        }

        if (Singleton<GameWorld>.Instantiated)
        {
            message = "Testing items can only be granted from the stash outside a raid.";
            return false;
        }

        try
        {
            TestingInventoryGrantPolicy.Validate(caseCount, keyCount);
        }
        catch (Exception)
        {
            message = "Choose between 0–10 cases and 0–40 keys, with 1–40 total items.";
            return false;
        }

        if (!Singleton<ClientApplication<IEftSession>>.Instantiated)
        {
            message = "The authenticated Tarkov session is not ready yet.";
            return false;
        }

        var session = Singleton<ClientApplication<IEftSession>>.Instance.GetClientBackEndSession();
        var profile = session?.Profile;
        if (session is null || profile is null || string.IsNullOrWhiteSpace(profile.Id))
        {
            message = "The authenticated Tarkov profile is unavailable.";
            return false;
        }

        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        _pending = true;
        try
        {
            session.SendOperationRightNow(
                new TestingInventoryGrantOperationParams(caseCount, keyCount, crateType),
                result =>
                {
                    _pending = false;
                    callback(result);
                });
            message = TestingCrateTypeCodec.PremiumTier(crateType) is { } premiumTier
                ? $"Requested {caseCount} {CaseContracts.ShortName(TestingCrateTypeCodec.CaseTemplate(crateType))} cases with {premiumTier} testing openings and {keyCount} universal Relay Keys. Open before restarting the server; testing tags are temporary."
                : TestingCrateTypeCodec.CaseTemplate(crateType) != ModConstants.CaseTemplateId
                ? $"Requested {caseCount} {crateType} cases and {keyCount} universal Relay Keys."
                : crateType == TestingCrateType.TrueRandom
                ? $"Requested {caseCount} BR-12 cases and {keyCount} BR-12 keys from the server."
                : $"Requested {caseCount} BR-12 cases (forced to the {TestingCrateTypeCodec.ToWireValue(crateType)} pool) and {keyCount} BR-12 keys from the server.";
            return true;
        }
        catch
        {
            _pending = false;
            throw;
        }
    }

    private sealed class TestingInventoryGrantOperationParams
    {
        public TestingInventoryGrantOperationParams(int caseCount, int keyCount, TestingCrateType crateType)
        {
            Action = ModConstants.TestingInventoryGrantAction;
            this.caseCount = caseCount;
            this.keyCount = keyCount;
            this.crateType = TestingCrateTypeCodec.ToWireValue(crateType);
        }

        public string Action { get; }

        public int caseCount { get; }

        public int keyCount { get; }

        public string crateType { get; }
    }
}
