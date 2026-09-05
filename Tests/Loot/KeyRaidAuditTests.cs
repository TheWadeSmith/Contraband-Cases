using ContrabandCases.Server.Loot;
using SPTarkov.Server.Core.Models.Common;
using Xunit;

namespace ContrabandCases.Tests.Loot;

public sealed class KeyRaidAuditTests
{
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
