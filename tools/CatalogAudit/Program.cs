using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json.Converters;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Settlement;

if (args.Length == 4 && args[0] == "--projected")
{
    File.WriteAllText(args[3], JsonSerializer.Serialize(ProjectedCatalogAudit.Create(args[1], args[2]),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Offline source-data projection audit: {args[3]}");
    return;
}

if (args.Length == 3 && args[0] == "--cash")
{
    File.WriteAllText(args[2], JsonSerializer.Serialize(CashAudit.Create(args[1]),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Offline Cash Cache audit: {args[2]}");
    return;
}
if (args.Length == 4 && args[0] == "--captured")
{
    File.WriteAllText(args[3], JsonSerializer.Serialize(CapturedOpeningAudit.Create(args[1], args[2]),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Captured opening-only projection: {args[3]}");
    return;
}
if (args.Length != 3)
    throw new ArgumentException("Usage: CatalogAudit <database directory> <reward-pack directory> <report file> OR --captured <live report> <reward-pack directory> <report file>");
var database = Path.GetFullPath(args[0]);
var packRoot = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
var templates = JsonSerializer.Deserialize<Dictionary<string, TemplateItem>>(
    File.ReadAllText(Path.Combine(database, "templates", "items.json")), options)!;
using var globals = JsonDocument.Parse(File.ReadAllText(Path.Combine(database, "globals.json")));
var presets = globals.RootElement.GetProperty("ItemPresets").Deserialize<Dictionary<string, Preset>>(options)!;
using var handbook = JsonDocument.Parse(File.ReadAllText(Path.Combine(database, "templates", "handbook.json")));
var prices = handbook.RootElement.GetProperty("Items").EnumerateArray().ToDictionary(
    item => item.GetProperty("Id").GetString()!, item => item.GetProperty("Price").GetDouble());
TemplateItem? FindTemplate(string id) => templates.GetValueOrDefault(id);
Preset? FindPreset(string id) => presets.GetValueOrDefault(id);
var loader = new JsonRewardPackLoader();
var core = loader.LoadFile(Path.Combine(packRoot, "core.json"));
var optional = Directory.EnumerateFiles(packRoot, "*.json").Where(path => Path.GetFileName(path) != "core.json")
    .OrderBy(path => path, StringComparer.Ordinal).Select(loader.LoadFile).ToArray();
var catalog = new CargoCatalogSnapshotBuilder(
    new CargoLotResolver(new CargoLotResolverDependencies(FindTemplate, FindPreset)),
    new CargoLotEvaluator(FindTemplate, id => prices.GetValueOrDefault(id)),
    new CargoPackRequirementValidator(FindTemplate, FindPreset).Validate).Build(core, optional);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
catalog = CaseCatalogs.WithPrice(catalog, ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice);
// Exercise the real client library parser with full resolved forests, including
// larger shipments; the captured-value mode intentionally cannot prove this.
var library = ManifestLibraryProjection.Create(new CaseOpeningJournal(), catalog, new Dictionary<string, string>());
var parsedLibrary = ManifestSnapshotParser.ParseLibrary(JsonSerializer.Serialize(new
    { err = 0, errmsg = (string?)null, data = library }));
if (parsedLibrary.Lots.Count != catalog.FreshOpeningLots.Count)
    throw new InvalidOperationException("Native cargo library lost fresh lots in the client contract.");
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    Scope = "Offline database only; does NOT run installed mod hooks. Live catalog-report.json is authoritative for mod integrations.",
    ClientLibraryValidated = true,
    Report = ManifestEconomyReport.Create(catalog)
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Offline audit: {catalog.Lots.Count} resolved lots, {catalog.SkippedPacks.Count} unavailable packs. Report: {output}");
