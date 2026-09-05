using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

internal static class CashAudit
{
    internal static object Create(string database)
    {
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        T Read<T>(params string[] parts) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(new[] { database }.Concat(parts).ToArray())), options)!;
        var items = Read<Dictionary<MongoId, TemplateItem>>("templates", "items.json");
        // Native client trader-price responses use static (handbook) prices, not
        // dynamic flea offers. This offline audit cannot run installed mod hooks.
        using var handbook = JsonDocument.Parse(File.ReadAllText(Path.Combine(database, "templates", "handbook.json")));
        var prices = handbook.RootElement.GetProperty("Items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("Id").GetString()!, item => item.GetProperty("Price").GetDouble());
        var traders = new TradersTable();
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(database, "traders")))
        {
            var basis = Path.Combine(directory, "base.json");
            var assort = Path.Combine(directory, "assort.json");
            if (!File.Exists(basis) || !File.Exists(assort)) continue;
            var traderBase = JsonSerializer.Deserialize<TraderBase>(File.ReadAllText(basis), options)!;
            traders[traderBase.Id] = new Trader
            {
                Base = traderBase,
                Dialogue = [],
                QuestAssort = [],
                Assort = JsonSerializer.Deserialize<TraderAssort>(File.ReadAllText(assort), options)!
            };
        }
        var cash = CashCurrencyQuotes.Freeze(items, traders, prices);
        return new { Scope = "Offline SPT database only; installed mod hooks and player-specific sale modifiers are NOT simulated.", Report = CashPayoutCatalog.Report(cash) };
    }
}
