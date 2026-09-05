using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Catalog;

public sealed class CargoPackRequirementValidator
{
    private readonly Func<string, TemplateItem?> _findTemplate;
    private readonly Func<string, Preset?> _findPreset;
    private readonly Func<string, bool>? _hasBundle;

    public CargoPackRequirementValidator(
        Func<string, TemplateItem?> findTemplate,
        Func<string, Preset?> findPreset,
        Func<string, bool>? hasBundle = null)
    {
        _findTemplate = findTemplate ?? throw new ArgumentNullException(nameof(findTemplate));
        _findPreset = findPreset ?? throw new ArgumentNullException(nameof(findPreset));
        _hasBundle = hasBundle;
    }

    public void Validate(CargoLotPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var templates = pack.RequiredTemplateIds.ToHashSet(StringComparer.Ordinal);
        var presets = pack.RequiredPresetIds.ToHashSet(StringComparer.Ordinal);
        foreach (var lot in pack.Lots)
        {
            if (!templates.Contains(lot.AnchorTemplateId))
            {
                throw new CargoCatalogValidationException(
                    $"Pack '{pack.ProviderId}' does not declare anchor template '{lot.AnchorTemplateId}'.");
            }

            foreach (var line in lot.RecipeLines)
            {
                if (line is TemplateLine templateLine &&
                    !templates.Contains(templateLine.TemplateId))
                {
                    throw new CargoCatalogValidationException(
                        $"Pack '{pack.ProviderId}' does not declare template '{templateLine.TemplateId}'.");
                }
                if (line is PresetLine presetLine &&
                    !presets.Contains(presetLine.PresetId))
                {
                    throw new CargoCatalogValidationException(
                        $"Pack '{pack.ProviderId}' does not declare preset '{presetLine.PresetId}'.");
                }
            }
        }

        foreach (var templateId in pack.RequiredTemplateIds)
        {
            ValidateMongoId(templateId, "template", pack.ProviderId);
            TemplateItem? template;
            try
            {
                template = _findTemplate(templateId);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw MissingRequirement(pack.ProviderId, "template", templateId);
            }
            if (template is null ||
                !string.Equals(template.Id.ToString(), templateId, StringComparison.Ordinal))
            {
                throw MissingRequirement(pack.ProviderId, "template", templateId);
            }
        }

        foreach (var presetId in pack.RequiredPresetIds)
        {
            ValidateMongoId(presetId, "preset", pack.ProviderId);
            Preset? preset;
            try
            {
                preset = _findPreset(presetId);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw MissingRequirement(pack.ProviderId, "preset", presetId);
            }
            if (preset is null ||
                !string.Equals(preset.Id.ToString(), presetId, StringComparison.Ordinal))
            {
                throw MissingRequirement(pack.ProviderId, "preset", presetId);
            }
        }

        foreach (var bundleKey in pack.RequiredBundleKeys)
        {
            if (_hasBundle is null || !_hasBundle(bundleKey))
            {
                throw MissingRequirement(pack.ProviderId, "bundle", bundleKey);
            }
        }
    }

    private static void ValidateMongoId(string value, string kind, string providerId)
    {
        if (value.Length != 24 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new CargoCatalogValidationException(
                $"Pack '{providerId}' contains invalid {kind} requirement '{value}'.");
        }
    }

    private static CargoCatalogValidationException MissingRequirement(
        string providerId,
        string kind,
        string id) =>
        new($"Pack '{providerId}' requires missing finalized {kind} '{id}'.");
}
