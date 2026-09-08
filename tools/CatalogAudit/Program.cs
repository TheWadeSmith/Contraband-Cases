using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json.Converters;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Settlement;

const string usage = """
Usage:
  CatalogAudit <database directory> <reward-pack directory> <report file>
  CatalogAudit --projected <fixture file> <reward-pack directory> <report file>
  CatalogAudit --cash <database directory> <report file>
  CatalogAudit --captured <live report> <reward-pack directory> <report file>
""";

int Fail(string problem)
{
    Console.Error.WriteLine($"CatalogAudit: {problem}");
    Console.Error.WriteLine(usage);
    return 2;
}

// Every mode writes through this so a long audit is never lost to a missing
// report folder at the final write, and callers always see the resolved path.
static string WriteReport(string path, object report)
{
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return full;
}

try
{
    // Match the mode before the argument count: a flag given the wrong number of
    // arguments must report its own usage, not fall through to the positional
    // form and get read as a database directory.
    var mode = args.Length > 0 && args[0].StartsWith('-') ? args[0] : "";
    var expected = mode switch
    {
        "" => 3,
        "--cash" => 3,
        "--projected" => 4,
        "--captured" => 4,
        _ => 0
    };
    if (expected == 0) return Fail($"unknown option '{mode}'.");
    if (args.Length != expected)
        return Fail($"{(mode.Length == 0 ? "offline database mode" : mode)} expects {expected} arguments, got {args.Length}.");

    switch (mode)
    {
        case "--projected":
            Console.WriteLine("Offline source-data projection audit: " +
                WriteReport(args[3], ProjectedCatalogAudit.Create(args[1], args[2])));
            return 0;
        case "--cash":
            Console.WriteLine("Offline Cash Cache audit: " + WriteReport(args[2], CashAudit.Create(args[1])));
            return 0;
        case "--captured":
            Console.WriteLine("Captured opening-only projection: " +
                WriteReport(args[3], CapturedOpeningAudit.Create(args[1], args[2])));
            return 0;
    }

    var database = Path.GetFullPath(args[0]);
    var packRoot = Path.GetFullPath(args[1]);
    var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
    foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
    var templates = JsonSerializer.Deserialize<Dictionary<string, TemplateItem>>(
        File.ReadAllText(Path.Combine(database, "templates", "items.json")), options)!;
    using var globals = JsonDocument.Parse(File.ReadAllText(Path.Combine(database, "globals.json")));
    var presets = globals.RootElement.GetProperty("ItemPresets").Deserialize<Dictionary<string, Preset>>(options)!;
    using var handbook = JsonDocument.Parse(File.ReadAllText(Path.Combine(database, "templates", "handbook.json")));
    var prices = handbook.RootElement.GetProperty("Items").EnumerateArray().ToDictionary(
        item => item.GetProperty("Id").GetString()!, item => item.GetProperty("Price").GetDouble());
    var traders = Directory.GetDirectories(Path.Combine(database, "traders"))
        .Select(path => JsonSerializer.Deserialize<SPTarkov.Server.Core.Models.Eft.Common.Tables.TraderBase>(
            File.ReadAllText(Path.Combine(path, "base.json")), options)!).ToArray();
    var resale = new CargoTraderResale(templates.GetValueOrDefault, prices, traders);
    TemplateItem? FindTemplate(string id) => templates.GetValueOrDefault(id);
    Preset? FindPreset(string id) => presets.GetValueOrDefault(id);
    var loader = new JsonRewardPackLoader();
    var core = loader.LoadFile(Path.Combine(packRoot, "core.json"));
    var optional = Directory.EnumerateFiles(packRoot, "*.json").Where(path => Path.GetFileName(path) != "core.json")
        .OrderBy(path => path, StringComparer.Ordinal).Select(loader.LoadFile).ToArray();
    var catalog = new CargoCatalogSnapshotBuilder(
        new CargoLotResolver(new CargoLotResolverDependencies(FindTemplate, FindPreset)),
        new CargoLotEvaluator(FindTemplate, id => prices.GetValueOrDefault(id), resale.Estimate),
        new CargoPackRequirementValidator(FindTemplate, FindPreset).Validate).Build(core, optional);
    catalog = CaseCatalogs.WithPrice(catalog, ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice);
    // Exercise the real client library parser with full resolved forests, including
    // larger shipments; the captured-value mode intentionally cannot prove this.
    var library = ManifestLibraryProjection.Create(new CaseOpeningJournal(), catalog, new Dictionary<string, string>());
    var parsedLibrary = ManifestSnapshotParser.ParseLibrary(JsonSerializer.Serialize(new
        { err = 0, errmsg = (string?)null, data = library }));
    if (parsedLibrary.Lots.Count != catalog.FreshOpeningLots.Count)
        throw new InvalidOperationException("Native cargo library lost fresh lots in the client contract.");
    var output = WriteReport(args[2], new
    {
        Scope = "Offline database only; does NOT run installed mod hooks. Live catalog-report.json is authoritative for mod integrations.",
        ClientLibraryValidated = true,
        Report = ManifestEconomyReport.Create(catalog),
        DecisionWeightedCases = new[] { catalog }.Concat(CaseContracts.Templates.Where(t =>
                t != catalog.CaseTemplateId && t != CaseContracts.CashCache).Select(t => CaseCatalogs.ForCase(catalog, t)))
            .Where(view => view.OpeningEnabled).Select(ProjectedCatalogAudit.DescribeDecisions).ToArray()
    });
    Console.WriteLine($"Offline audit: {catalog.Lots.Count} resolved lots, {catalog.SkippedPacks.Count} unavailable packs. Report: {output}");
    return 0;
}
catch (Exception exception)
{
    // Report the failure instead of dying unhandled: an unhandled exception exits
    // 0xE0434352 and Windows shows only "CatalogAudit.exe stopped working".
    Console.Error.WriteLine($"CatalogAudit failed: {exception.Message}");
    Console.Error.WriteLine(exception.StackTrace);
    return 1;
}
