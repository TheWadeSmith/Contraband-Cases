using ContrabandCases.Server.Loot;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using Xunit;

namespace ContrabandCases.Tests.Loot;

public sealed class KeyRaidAuditTests
{
    [Fact]
    public void Supply_summary_includes_zero_find_samples_all_case_types_and_testing_flag()
    {
        var empty = KeyRaidAudit.DescribeRetained(new HashSet<MongoId>(), new Dictionary<MongoId, string>(), false);
        Assert.Contains("key instances=0", empty);
        Assert.Contains("testing grants enabled=False", empty);
        foreach (var template in CaseContracts.Templates)
            Assert.Contains($"{CaseContracts.ShortName(template)}=0", empty);
        MongoId carried = "000000000000000000000001";
        MongoId found = "000000000000000000000002";
        var summary = KeyRaidAudit.DescribeRetained(new HashSet<MongoId> { carried },
            new Dictionary<MongoId, string> { [carried] = ModConstants.KeyTemplateId, [found] = CaseContracts.Operations }, true);
        Assert.Contains("key instances=0", summary);
        Assert.Contains($"{CaseContracts.ShortName(CaseContracts.Operations)}=1", summary);
        Assert.Contains("testing grants enabled=True", summary);
        Assert.DoesNotContain(found.ToString(), summary);
    }

    [Fact]
    public void Retained_keys_exclude_carried_keys_and_duplicate_inventory_rows()
    {
        MongoId old = "000000000000000000000001";
        MongoId found = "000000000000000000000002";
        Assert.Equal(1, KeyRaidAudit.CountNewKeys(new HashSet<MongoId> { old }, [old, found, found]));
        Assert.Equal(0, KeyRaidAudit.CountNewKeys(new HashSet<MongoId> { old }, []));
        Assert.Equal(0, KeyRaidAudit.CountNewKeys(new HashSet<MongoId> { old }, [old]));
    }
}
