using ContrabandCases.Server.Settlement;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class MissingRelayKeyTests
{
    [Fact]
    public void Missing_key_is_a_distinct_preconsumption_rejection()
    {
        var exception = Assert.Throws<RelayKeyRequiredException>(() =>
            SptOpeningInventory.SelectLowestRelayKey(Array.Empty<Item>(), new HashSet<MongoId>()));
        Assert.Contains("No items were consumed", exception.Message);
    }

    [Fact]
    public void Route_returns_an_ordinary_warning_and_preserves_existing_response()
    {
        var existing = new Warning { Code = BackendErrorCodes.NotEnoughSpace, ErrorMessage = "existing" };
        var output = new ItemEventRouterResponse { Warnings = [existing] };
        Assert.Same(output, ContrabandCaseRouter.RejectMissingKey(output));
        Assert.Same(existing, output.Warnings[0]);
        Assert.Equal(BackendErrorCodes.HTTPBadRequest, output.Warnings[1].Code);
        Assert.Contains("BR-12 Relay Key required", output.Warnings[1].ErrorMessage);
    }
}
