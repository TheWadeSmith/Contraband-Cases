using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Computes bounded economy and stash facts from the exact resolved forest and
/// finalized live SPT tables. No pack-authored value or grade crosses this
/// boundary.
/// </summary>
public sealed class CargoLotEvaluator
{
    internal const int MaximumFootprintCells = 512;
    internal const decimal MaximumUnitValue = 100_000_000m;
    internal const decimal MaximumLotValue = 10_000_000_000m;
    private const decimal MaximumUseValueMultiplier = 3m;

    private readonly Func<string, TemplateItem?> _findTemplate;
    private readonly Func<string, double?> _findHandbookPrice;

    public CargoLotEvaluator(
        Func<string, TemplateItem?> findTemplate,
        Func<string, double?> findHandbookPrice)
    {
        _findTemplate = findTemplate ?? throw new ArgumentNullException(nameof(findTemplate));
        _findHandbookPrice =
            findHandbookPrice ?? throw new ArgumentNullException(nameof(findHandbookPrice));
    }

    public ResolvedCargoLot Evaluate(ResolvedCargoLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var templates = new Dictionary<string, TemplateItem>(StringComparer.Ordinal);
        decimal handbookTotal = 0m;
        decimal useTotal = 0m;
        foreach (var node in lot.Forest.Nodes)
        {
            var template = ResolveTemplate(node.TemplateId, templates);
            var handbookUnit = RequirePositiveValue(
                node.TemplateId,
                "handbook",
                ReadHandbookPrice(node.TemplateId));
            var creditsUnit = ReadCreditsPrice(node.TemplateId, template);
            var useUnit = Math.Min(
                Math.Max(handbookUnit, creditsUnit ?? handbookUnit),
                checked(handbookUnit * MaximumUseValueMultiplier));

            handbookTotal = AddBounded(
                handbookTotal,
                checked(handbookUnit * node.StackCount),
                lot.Definition.LotId);
            useTotal = AddBounded(
                useTotal,
                checked(useUnit * node.StackCount),
                lot.Definition.LotId);
        }

        var handbookValue = ToPositiveInt64(handbookTotal, lot.Definition.LotId, "handbook");
        var useValue = ToPositiveInt64(useTotal, lot.Definition.LotId, "use");
        var footprint = ComputeFootprint(lot, templates);
        return lot.WithEvaluation(new CargoLotEvaluation(
            handbookValue,
            useValue,
            footprint,
            ShipmentEconomy.Grade(lot.Identity.LotId, useValue)));
    }

    private TemplateItem ResolveTemplate(
        string templateId,
        IDictionary<string, TemplateItem> cache)
    {
        if (cache.TryGetValue(templateId, out var cached))
        {
            return cached;
        }

        TemplateItem? template;
        try
        {
            template = _findTemplate(templateId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' could not be read for cargo evaluation.");
        }

        if (template is null ||
            !string.Equals(template.Id.ToString(), templateId, StringComparison.Ordinal) ||
            template.Properties is null)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' is unavailable for cargo evaluation.");
        }

        cache.Add(templateId, template);
        return template;
    }

    private double? ReadHandbookPrice(string templateId)
    {
        try
        {
            return _findHandbookPrice(templateId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' handbook price could not be read.");
        }
    }

    private static decimal? ReadCreditsPrice(string templateId, TemplateItem template)
    {
        var value = template.Properties?.CreditsPrice;
        if (value is null or 0d)
        {
            return null;
        }
        if (!double.IsFinite(value.Value) ||
            value < 0d ||
            value > (double)MaximumUnitValue)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has an invalid stable replacement price.");
        }

        return checked((decimal)value.Value);
    }

    private static decimal RequirePositiveValue(
        string templateId,
        string valueName,
        double? value)
    {
        if (value is null ||
            !double.IsFinite(value.Value) ||
            value <= 0d ||
            value > (double)MaximumUnitValue)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has no valid positive {valueName} value.");
        }

        return checked((decimal)value.Value);
    }

    private static decimal AddBounded(decimal current, decimal addition, string lotId)
    {
        var result = checked(current + addition);
        if (result > MaximumLotValue)
        {
            throw new CargoCatalogValidationException(
                $"Lot '{lotId}' exceeds the supported value bound.");
        }

        return result;
    }

    private static long ToPositiveInt64(decimal value, string lotId, string valueName)
    {
        var rounded = decimal.Ceiling(value);
        if (rounded <= 0m || rounded > long.MaxValue)
        {
            throw new CargoCatalogValidationException(
                $"Lot '{lotId}' has an invalid computed {valueName} value.");
        }

        return checked((long)rounded);
    }

    private static int ComputeFootprint(
        ResolvedCargoLot lot,
        IReadOnlyDictionary<string, TemplateItem> templates)
    {
        long total = 0;
        foreach (var root in lot.Forest.Roots)
        {
            var rootProperties = templates[root.TemplateId].Properties!;
            var width = RequireDimension(lot.Definition.LotId, root.TemplateId, "width", rootProperties.Width);
            var height = RequireDimension(lot.Definition.LotId, root.TemplateId, "height", rootProperties.Height);
            var maximumUp = 0;
            var maximumDown = 0;
            var maximumLeft = 0;
            var maximumRight = 0;
            var forcedUp = 0;
            var forcedDown = 0;
            var forcedLeft = 0;
            var forcedRight = 0;

            foreach (var node in lot.Forest.Nodes.Where(candidate =>
                         string.Equals(candidate.TreeRootPath, root.TreeRootPath, StringComparison.Ordinal)))
            {
                var properties = templates[node.TemplateId].Properties!;
                var up = RequireExtraSize(node.TemplateId, "up", properties.ExtraSizeUp);
                var down = RequireExtraSize(node.TemplateId, "down", properties.ExtraSizeDown);
                var left = RequireExtraSize(node.TemplateId, "left", properties.ExtraSizeLeft);
                var right = RequireExtraSize(node.TemplateId, "right", properties.ExtraSizeRight);
                if (properties.ExtraSizeForceAdd is true)
                {
                    forcedUp = checked(forcedUp + up);
                    forcedDown = checked(forcedDown + down);
                    forcedLeft = checked(forcedLeft + left);
                    forcedRight = checked(forcedRight + right);
                }
                else
                {
                    maximumUp = Math.Max(maximumUp, up);
                    maximumDown = Math.Max(maximumDown, down);
                    maximumLeft = Math.Max(maximumLeft, left);
                    maximumRight = Math.Max(maximumRight, right);
                }
            }

            var finalWidth = checked(width + maximumLeft + maximumRight + forcedLeft + forcedRight);
            var finalHeight = checked(height + maximumUp + maximumDown + forcedUp + forcedDown);
            total = checked(total + ((long)finalWidth * finalHeight));
            if (total > MaximumFootprintCells)
            {
                throw new CargoCatalogValidationException(
                    $"Lot '{lot.Definition.LotId}' exceeds the supported stash-footprint bound.");
            }
        }

        return checked((int)total);
    }

    private static int RequireDimension(
        string lotId,
        string templateId,
        string name,
        int? value)
    {
        if (value is not > 0 or > 256)
        {
            throw new CargoCatalogValidationException(
                $"Lot '{lotId}' template '{templateId}' has invalid {name} data.");
        }

        return value.Value;
    }

    private static int RequireExtraSize(string templateId, string direction, int? value)
    {
        var normalized = value ?? 0;
        if (normalized is < 0 or > 256)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has invalid extra-size-{direction} data.");
        }

        return normalized;
    }
}
