using System.Globalization;
using System.Numerics;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ContrabandCases.Client.Opening;

internal static class ManifestSnapshotParser
{
    private const int OfferCount = 3;
    private const int MaximumContents = 512;

    internal static ManifestSnapshot? Parse(
        string json,
        string? expectedManifestId,
        bool snapshotRequired)
    {
        if (expectedManifestId is not null)
        {
            try
            {
                expectedManifestId = ManifestProtocolValidation.RequireIdentifier(
                    expectedManifestId,
                    nameof(expectedManifestId));
            }
            catch (ArgumentException exception)
            {
                throw new ManifestSnapshotException(
                    "A canonical expected Manifest ID is required.",
                    exception);
            }
        }

        try
        {
            var data = ParseSuccessfulData(json);
            RequireExactProperties(data, "response data", "snapshot");
            var snapshotToken = RequireProperty(data, "snapshot");
            if (snapshotToken.Type == JTokenType.Null)
            {
                if (snapshotRequired)
                {
                    throw new ManifestSnapshotException(
                        "The requested Manifest snapshot was not returned.");
                }

                return null;
            }

            var snapshot = ParseSnapshot(RequireObject(snapshotToken, "snapshot"));
            if (expectedManifestId is not null &&
                !string.Equals(snapshot.ManifestId, expectedManifestId, StringComparison.Ordinal))
            {
                throw new ManifestSnapshotException(
                    "The server returned a different Manifest than the authenticated request.");
            }

            return snapshot;
        }
        catch (ManifestSnapshotException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ManifestSnapshotException(
                "The Manifest server response was not valid canonical JSON.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ManifestSnapshotException(
                "The Manifest server response failed validation.",
                exception);
        }
    }

    internal static ManifestCurrentState ParseCurrent(string json)
    {
        try
        {
            var data = ParseSuccessfulData(json);
            RequireExactProperties(data, "current response data", "snapshot", "openingOdds");
            var snapshotToken = RequireProperty(data, "snapshot");
            var oddsToken = RequireProperty(data, "openingOdds");
            var snapshot = snapshotToken.Type == JTokenType.Null
                ? null
                : ParseSnapshot(RequireObject(snapshotToken, "snapshot"));
            var openingOdds = oddsToken.Type == JTokenType.Null
                ? null
                : ParseOpeningOdds(RequireObject(oddsToken, "opening odds"));
            return new ManifestCurrentState(snapshot, openingOdds);
        }
        catch (ManifestSnapshotException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ManifestSnapshotException(
                "The current Manifest response was not valid canonical JSON.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ManifestSnapshotException(
                "The current Manifest response failed validation.",
                exception);
        }
    }

    internal static ManifestLibrarySnapshot ParseLibrary(string json)
    {
        try
        {
            var data = ParseSuccessfulData(json);
            var version = ReadInt(data, "protocolVersion");
            if (version is not (1 or 2))
                throw new ManifestSnapshotException("Unsupported library protocol.");
            if (version == 1) RequireExactProperties(data, "library", "protocolVersion", "dossier", "lots");
            else RequireExactProperties(data, "library", "protocolVersion", "dossier", "lots", "hasPending", "status", "cases");
            var dossierToken = RequireProperty(data, "dossier");
            if (dossierToken.Type != JTokenType.String) throw new ManifestSnapshotException("Missing dossier text.");
            var dossier = dossierToken.Value<string>()!;
            if (dossier.Length > 65_536) throw new ManifestSnapshotException("Dossier exceeds its bound.");
            var lots = RequireArray(RequireProperty(data, "lots"), "library lots", 0, ManifestOpeningOddsSnapshot.MaximumTotalLots)
                .Select(token => ParseNullableLot(token)!).ToArray();
            if (lots.Select(lot => (lot.ProviderId, lot.LotId)).Distinct().Count() != lots.Length)
                throw new ManifestSnapshotException("Duplicate library lot identities.");
            if (version == 1) return new ManifestLibrarySnapshot(dossier, lots);
            var pending = RequireProperty(data, "hasPending");
            var status = RequireProperty(data, "status");
            if (pending.Type != JTokenType.Boolean || status.Type != JTokenType.String || status.Value<string>()!.Length > 32_768)
                throw new ManifestSnapshotException("Invalid library status.");
            var cases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var identities = new HashSet<string>(lots.Select(l => $"{l.ProviderId}/{l.LotId}"), StringComparer.Ordinal);
            foreach (var entry in RequireArray(RequireProperty(data, "cases"), "library cases", 0, 5))
            {
                if (entry is not JObject c) throw new ManifestSnapshotException("Invalid library case.");
                RequireExactProperties(c, "library case", "templateId", "lotIds");
                var id = c["templateId"]?.Type == JTokenType.String ? c["templateId"]!.Value<string>() : null;
                if (!CaseContracts.IsCase(id) || cases.ContainsKey(id!)) throw new ManifestSnapshotException("Invalid or duplicate library case.");
                var keys = RequireArray(RequireProperty(c, "lotIds"), "case lots", 0, ManifestOpeningOddsSnapshot.MaximumTotalLots)
                    .Select(key => key.Type == JTokenType.String ? key.Value<string>()! : throw new ManifestSnapshotException("Invalid lot reference.")).ToArray();
                if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(key => !identities.Contains(key)))
                    throw new ManifestSnapshotException("Unknown or duplicate case lot reference.");
                cases.Add(id!, keys);
            }
            return new ManifestLibrarySnapshot(dossier, lots, pending.Value<bool>(), status.Value<string>()!, cases);
        }
        catch (ManifestSnapshotException) { throw; }
        catch (Exception exception)
        {
            throw new ManifestSnapshotException("The library response failed validation.", exception);
        }
    }

    private static JObject ParseSuccessfulData(string json)
    {
        if (json is null || json.Length > ManifestProtocolValidation.MaximumResponseCharacters)
        {
            throw new ManifestSnapshotException(
                "The Manifest server response is absent or exceeds its size bound.");
        }

        var token = JToken.Parse(json, new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
        });
        var envelope = RequireObject(token, "response envelope");
        RequireExactProperties(envelope, "response envelope", "err", "errmsg", "data");

        if (ReadInt(envelope, "err") != 0)
        {
            throw new ManifestSnapshotException(
                "The Manifest server returned an unsuccessful response.");
        }
        var errorMessage = ReadNullableString(envelope, "errmsg", allowEmpty: true);
        if (!string.IsNullOrEmpty(errorMessage))
        {
            throw new ManifestSnapshotException(
                "A successful Manifest response cannot contain an error message.");
        }

        return RequireObject(RequireProperty(envelope, "data"), "response data");
    }

    private static ManifestOpeningOddsSnapshot ParseOpeningOdds(JObject source)
    {
        RequirePropertiesWithExtension(
            source,
            "opening odds",
            ["premiumOdds"],
            "caseTemplateId",
            "casePrice",
            "protocolVersion",
            "catalogSnapshotId",
            "offerCount",
            "familyCount",
            "selectionRule",
            "families");

        var protocolVersion = ReadInt(source, "protocolVersion");
        if (protocolVersion != ManifestOpeningOddsSnapshot.CurrentProtocolVersion)
        {
            throw new ManifestSnapshotException(
                "The Manifest opening-odds protocol version is unsupported.");
        }

        var offerCount = ReadInt(source, "offerCount");
        var caseTemplate = CaseContracts.Require(ReadIdentifier(source, "caseTemplateId"));
        if (offerCount != CaseContracts.OfferCount(caseTemplate))
        {
            throw new ManifestSnapshotException(
                "The Manifest opening-odds offer count is unsupported.");
        }

        var familyCount = ReadIntInRange(
            source,
            "familyCount",
            offerCount,
            ManifestOpeningOddsSnapshot.MaximumFamilies);
        var selectionRule = ReadIdentifier(source, "selectionRule");
        if (!string.Equals(
                selectionRule,
                caseTemplate == CaseContracts.CashCache ? CashPayouts.SelectionRule : ManifestOpeningOddsSnapshot.CanonicalSelectionRule,
                StringComparison.Ordinal))
        {
            throw new ManifestSnapshotException(
                "The Manifest opening-odds selection rule is unsupported.");
        }

        var familyArray = RequireArray(
            RequireProperty(source, "families"),
            "opening-odds families",
            familyCount,
            familyCount);
        var families = new ManifestOpeningFamilyOddsSnapshot[familyArray.Count];
        string? previousFamilyId = null;
        var totalLotCount = 0;
        for (var index = 0; index < familyArray.Count; index++)
        {
            var family = ParseOpeningFamily(
                RequireObject(familyArray[index], "opening-odds family"),
                familyCount,
                offerCount,
                ref totalLotCount);
            if (previousFamilyId is not null &&
                string.CompareOrdinal(previousFamilyId, family.FamilyId) >= 0)
            {
                throw new ManifestSnapshotException(
                    "Manifest opening-odds families are not in unique canonical order.");
            }

            previousFamilyId = family.FamilyId;
            families[index] = family;
        }

        var casePrice = ReadNullableLongInRange(source, "casePrice", 1, ManifestProtocolValidation.MaximumMoneyValue);
        if (caseTemplate != ModConstants.CaseTemplateId && casePrice is null)
            throw new ManifestSnapshotException("The themed case is missing its finalized price.");

        return new ManifestOpeningOddsSnapshot(
            protocolVersion,
            ReadIdentifier(source, "catalogSnapshotId"),
            offerCount,
            familyCount,
            selectionRule,
            families,
            caseTemplate,
            casePrice,
            ParsePremiumOdds(source.Property("premiumOdds")?.Value, caseTemplate));
    }

    private static ManifestPremiumOddsSnapshot? ParsePremiumOdds(JToken? token, string caseTemplate)
    {
        if (token is null || token.Type == JTokenType.Null) return null;
        if (caseTemplate == CaseContracts.CashCache)
            throw new ManifestSnapshotException("Cash Cache cannot publish premium tier odds.");
        var source = RequireObject(token, "premium odds");
        RequireExactProperties(source, "premium odds", "epic", "legendary");
        return new ManifestPremiumOddsSnapshot(ParseTier("epic", ManifestOpeningTierRules.EpicBasisPoints, false),
            ParseTier("legendary", ManifestOpeningTierRules.LegendaryBasisPoints, true));

        ManifestPremiumTierOddsSnapshot ParseTier(string name, int publishedRate, bool legendary)
        {
            var tier = RequireObject(RequireProperty(source, name), "premium tier");
            RequireExactProperties(tier, "premium tier", "chanceBasisPoints", "minimumUseValue", "lots");
            var rate = ReadInt(tier, "chanceBasisPoints");
            if (rate != 0 && rate != publishedRate)
                throw new ManifestSnapshotException("Unsupported surprise tier probability.");
            var floor = ReadLongInRange(tier, "minimumUseValue", legendary ? 300_000 : 200_000,
                ManifestProtocolValidation.MaximumMoneyValue);
            var array = RequireArray(RequireProperty(tier, "lots"), "premium lots", rate == 0 ? 0 : 3,
                rate == 0 ? 0 : ManifestOpeningOddsSnapshot.MaximumTotalLots);
            var lots = rate == 0 ? [] : ParseOpeningLots(array);
            if (lots.Any(lot => legendary ? lot.Grade != RewardRarity.BlackLabel
                : lot.Grade is not (RewardRarity.Restricted or RewardRarity.BlackLabel)))
                throw new ManifestSnapshotException("Premium odds contain a below-grade reward.");
            return new ManifestPremiumTierOddsSnapshot(rate, floor, lots);
        }
    }

    private static ManifestOpeningFamilyOddsSnapshot ParseOpeningFamily(
        JObject source,
        int familyCount,
        int offerCount,
        ref int totalLotCount)
    {
        RequireExactProperties(
            source,
            "opening-odds family",
            "familyId",
            "familyLabel",
            "perSlotNumerator",
            "perSlotDenominator",
            "perSlotPercent",
            "inclusionNumerator",
            "inclusionDenominator",
            "inclusionPercent",
            "lots");

        var perSlot = ReadRational(
            source,
            "perSlotNumerator",
            "perSlotDenominator",
            "family per-slot probability");
        var inclusion = ReadRational(
            source,
            "inclusionNumerator",
            "inclusionDenominator",
            "family inclusion probability");
        if (!perSlot.Equals(ParsedRational.Reduce(BigInteger.One, familyCount)) ||
            !inclusion.Equals(ParsedRational.Reduce(
                offerCount,
                familyCount)))
        {
            throw new ManifestSnapshotException(
                "Manifest family odds contradict uniform selection without replacement.");
        }

        var lotArray = RequireArray(
            RequireProperty(source, "lots"),
            "opening-odds lots",
            minimum: 1,
            ManifestOpeningOddsSnapshot.MaximumLotsPerFamily);
        totalLotCount = checked(totalLotCount + lotArray.Count);
        if (totalLotCount > ManifestOpeningOddsSnapshot.MaximumTotalLots)
        {
            throw new ManifestSnapshotException(
                "The Manifest opening-odds catalog exceeds its total lot bound.");
        }

        var lots = ParseOpeningLots(lotArray);
        return new ManifestOpeningFamilyOddsSnapshot(
            ReadIdentifier(source, "familyId"),
            ReadText(source, "familyLabel"),
            perSlot.NumeratorText,
            perSlot.DenominatorText,
            ReadPercent(source, "perSlotPercent", perSlot),
            inclusion.NumeratorText,
            inclusion.DenominatorText,
            ReadPercent(source, "inclusionPercent", inclusion),
            lots);
    }

    private static ManifestOpeningLotOddsSnapshot[] ParseOpeningLots(JArray lotArray)
    {
        var lots = new ManifestOpeningLotOddsSnapshot[lotArray.Count];
        var sum = ParsedRational.Zero;
        string? previousProviderId = null;
        string? previousLotId = null;
        for (var index = 0; index < lotArray.Count; index++)
        {
            var lotSource = RequireObject(lotArray[index], "opening-odds lot");
            RequireExactProperties(
                lotSource,
                "opening-odds lot",
                "providerId",
                "providerLabel",
                "lotId",
                "displayName",
                "grade",
                "anchorTemplateId",
                "conditionalNumerator",
                "conditionalDenominator",
                "conditionalPercent");
            var providerId = ReadIdentifier(lotSource, "providerId");
            var lotId = ReadIdentifier(lotSource, "lotId");
            if (previousProviderId is not null &&
                (string.CompareOrdinal(previousProviderId, providerId) > 0 ||
                 string.Equals(previousProviderId, providerId, StringComparison.Ordinal) &&
                 string.CompareOrdinal(previousLotId, lotId) >= 0))
            {
                throw new ManifestSnapshotException(
                    "Manifest opening-odds lots are not in unique canonical order.");
            }

            var probability = ReadRational(
                lotSource,
                "conditionalNumerator",
                "conditionalDenominator",
                "conditional lot probability");
            if (probability.Numerator.IsZero)
            {
                throw new ManifestSnapshotException(
                    "A published Manifest lot must have positive conditional probability.");
            }

            sum = sum.Add(probability);
            lots[index] = new ManifestOpeningLotOddsSnapshot(
                providerId,
                ReadText(lotSource, "providerLabel"),
                lotId,
                ReadText(lotSource, "displayName"),
                ReadEnum<RewardRarity>(lotSource, "grade"),
                ReadIdentifier(lotSource, "anchorTemplateId"),
                probability.NumeratorText,
                probability.DenominatorText,
                ReadPercent(lotSource, "conditionalPercent", probability));
            previousProviderId = providerId;
            previousLotId = lotId;
        }

        if (!sum.Equals(ParsedRational.One))
        {
            throw new ManifestSnapshotException(
                "Published Manifest lot probabilities do not sum to one inside their family.");
        }

        return lots;
    }

    private static ParsedRational ReadRational(
        JObject source,
        string numeratorName,
        string denominatorName,
        string label)
    {
        var numerator = ReadCanonicalUnsignedInteger(source, numeratorName);
        var denominator = ReadCanonicalUnsignedInteger(source, denominatorName);
        if (denominator.Value <= BigInteger.Zero ||
            numerator.Value > denominator.Value ||
            BigInteger.GreatestCommonDivisor(numerator.Value, denominator.Value) != BigInteger.One)
        {
            throw new ManifestSnapshotException(
                $"The {label} is not a reduced probability rational.");
        }

        return new ParsedRational(
            numerator.Value,
            denominator.Value,
            numerator.Text,
            denominator.Text);
    }

    private static (BigInteger Value, string Text) ReadCanonicalUnsignedInteger(
        JObject source,
        string name)
    {
        var token = RequireProperty(source, name);
        if (token.Type != JTokenType.String)
        {
            throw new ManifestSnapshotException(
                $"The probability component '{name}' must be a decimal string.");
        }

        var text = token.Value<string>()!;
        if (text.Length is 0 or > ManifestOpeningOddsSnapshot.MaximumRationalDigits ||
            text.Length > 1 && text[0] == '0' ||
            text.Any(character => character is < '0' or > '9') ||
            !BigInteger.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ManifestSnapshotException(
                $"The probability component '{name}' is not a bounded canonical decimal string.");
        }

        return (value, text);
    }

    private static string ReadPercent(
        JObject source,
        string name,
        ParsedRational probability)
    {
        var value = ReadText(source, name);
        var decimalPoint = value.Length - 4;
        if (value.Length is < 5 or > 16 ||
            value[^1] != '%' ||
            decimalPoint < 1 ||
            value[decimalPoint] != '.' ||
            value.Take(decimalPoint).Any(character => character is < '0' or > '9') ||
            value[decimalPoint + 1] is < '0' or > '9' ||
            value[decimalPoint + 2] is < '0' or > '9')
        {
            throw new ManifestSnapshotException(
                $"The property '{name}' is not a server-formatted percentage.");
        }
        if (!string.Equals(value, probability.PercentText, StringComparison.Ordinal))
        {
            throw new ManifestSnapshotException(
                $"The property '{name}' does not match its published probability rational.");
        }

        return value;
    }

    private readonly struct ParsedRational : IEquatable<ParsedRational>
    {
        internal static readonly ParsedRational Zero = Reduce(BigInteger.Zero, BigInteger.One);
        internal static readonly ParsedRational One = Reduce(BigInteger.One, BigInteger.One);

        internal ParsedRational(
            BigInteger numerator,
            BigInteger denominator,
            string numeratorText,
            string denominatorText)
        {
            Numerator = numerator;
            Denominator = denominator;
            NumeratorText = numeratorText;
            DenominatorText = denominatorText;
        }

        internal BigInteger Numerator { get; }

        private BigInteger Denominator { get; }

        internal string NumeratorText { get; }

        internal string DenominatorText { get; }

        internal string PercentText
        {
            get
            {
                var hundredths = BigInteger.DivRem(
                    Numerator * 10_000,
                    Denominator,
                    out var remainder);
                if (remainder * 2 >= Denominator)
                {
                    hundredths++;
                }

                var whole = BigInteger.DivRem(hundredths, 100, out var fraction);
                return string.Concat(
                    whole.ToString(CultureInfo.InvariantCulture),
                    ".",
                    checked((int)fraction).ToString("D2", CultureInfo.InvariantCulture),
                    "%");
            }
        }

        internal ParsedRational Add(ParsedRational other) => Reduce(
            Numerator * other.Denominator + other.Numerator * Denominator,
            Denominator * other.Denominator);

        internal static ParsedRational Reduce(BigInteger numerator, BigInteger denominator)
        {
            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            numerator /= divisor;
            denominator /= divisor;
            return new ParsedRational(
                numerator,
                denominator,
                numerator.ToString(CultureInfo.InvariantCulture),
                denominator.ToString(CultureInfo.InvariantCulture));
        }

        public bool Equals(ParsedRational other) =>
            Numerator == other.Numerator && Denominator == other.Denominator;

        public override bool Equals(object? obj) =>
            obj is ParsedRational other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    }

    private static ManifestSnapshot ParseSnapshot(JObject source)
    {
        RequirePropertiesWithExtension(
            source,
            "snapshot",
            ["openingTier", "premiumChoices"],
            "caseTemplateId",
            "protocolVersion",
            "manifestId",
            "phase",
            "currentOrdinal",
            "lockedOrdinal",
            "relayStage",
            "relayTerminal",
            "rarityLadderVersion",
            "catalogSnapshotId",
            "ticketCommitted",
            "brokerFavor",
            "brokerFavorMaximum",
            "familySeals",
            "currentLot",
            "availableActions",
            "relay",
            "latestReceipt",
            "missingContentBlocked",
            "recoveryCaseItemId");

        var protocolVersion = ReadInt(source, "protocolVersion");
        if (protocolVersion != ManifestSnapshot.CurrentProtocolVersion)
        {
            throw new ManifestSnapshotException("The Manifest snapshot protocol version is unsupported.");
        }

        var manifestId = ReadIdentifier(source, "manifestId");
        var phase = ReadEnum<ManifestPhase>(source, "phase");
        var currentOrdinal = ReadIntInRange(source, "currentOrdinal", 1, OfferCount);
        var lockedOrdinal = ReadNullableIntInRange(source, "lockedOrdinal", 1, OfferCount);
        var relayStage = ReadIntInRange(source, "relayStage", 1, RelayRules.MaximumStage);
        var relayTerminal = ReadBoolean(source, "relayTerminal");
        var rarityLadderVersion = ReadEnum<RarityLadderVersion>(source, "rarityLadderVersion");
        var catalogSnapshotId = ReadNullableIdentifier(source, "catalogSnapshotId");
        var ticketCommitted = ReadBoolean(source, "ticketCommitted");
        var brokerFavor = ReadIntInRange(
            source,
            "brokerFavor",
            0,
            RelayRules.MaximumRecoveryMeter);
        var brokerFavorMaximum = ReadInt(source, "brokerFavorMaximum");
        if (brokerFavorMaximum != RelayRules.MaximumRecoveryMeter)
        {
            throw new ManifestSnapshotException("The Manifest snapshot has an unsupported Broker Favor scale.");
        }

        var caseTemplate = CaseContracts.Require(ReadIdentifier(source, "caseTemplateId"));
        var familySeals = ParseFamilySeals(RequireProperty(source, "familySeals"), CaseContracts.OfferCount(caseTemplate));
        var currentLot = ParseNullableLot(RequireProperty(source, "currentLot"));
        var availableActions = ParseAvailableActions(
            RequireObject(RequireProperty(source, "availableActions"), "available actions"));
        var relay = ParseNullableRelay(RequireProperty(source, "relay"));
        var latestReceipt = ParseNullableReceipt(RequireProperty(source, "latestReceipt"));
        var missingContentBlocked = ReadBoolean(source, "missingContentBlocked");
        var recoveryCaseItemId = ReadNullableString(source, "recoveryCaseItemId");

        var snapshot = new ManifestSnapshot(
            protocolVersion,
            manifestId,
            phase,
            currentOrdinal,
            lockedOrdinal,
            relayStage,
            relayTerminal,
            rarityLadderVersion,
            catalogSnapshotId,
            ticketCommitted,
            brokerFavor,
            brokerFavorMaximum,
            familySeals,
            currentLot,
            availableActions,
            relay,
            latestReceipt,
            missingContentBlocked,
            recoveryCaseItemId,
            caseTemplate,
            source.Property("openingTier") is null ? ManifestOpeningTier.Normal : ReadEnum<ManifestOpeningTier>(source, "openingTier"),
            source.Property("premiumChoices") is null ? [] : RequireArray(RequireProperty(source, "premiumChoices"),
                "premium choices", 0, OfferCount).Select(token => ParseNullableLot(token)
                    ?? throw new ManifestSnapshotException("A premium choice cannot be null.")));
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    private static IReadOnlyList<ManifestFamilySealSnapshot> ParseFamilySeals(JToken token, int expectedCount)
    {
        var array = RequireArray(token, "family seals", expectedCount, expectedCount);
        var seals = new ManifestFamilySealSnapshot[array.Count];
        for (var index = 0; index < array.Count; index++)
        {
            var source = RequireObject(array[index], "family seal");
            RequireExactProperties(
                source,
                "family seal",
                "ordinal",
                "familyId",
                "familyLabel",
                "riskBand",
                "revealed",
                "burned",
                "locked");
            seals[index] = new ManifestFamilySealSnapshot(
                ReadIntInRange(source, "ordinal", 1, OfferCount),
                ReadIdentifier(source, "familyId"),
                ReadText(source, "familyLabel"),
                ReadEnum<ManifestRiskBand>(source, "riskBand"),
                ReadBoolean(source, "revealed"),
                ReadBoolean(source, "burned"),
                ReadBoolean(source, "locked"));
        }

        return seals;
    }

    private static ManifestLotSnapshot? ParseNullableLot(JToken token)
    {
        if (token.Type == JTokenType.Null)
        {
            return null;
        }

        var source = RequireObject(token, "current lot");
        RequireExactProperties(
            source,
            "current lot",
            "providerId",
            "providerLabel",
            "lotId",
            "displayName",
            "purpose",
            "familyId",
            "trackId",
            "grade",
            "anchorTemplateId",
            "fingerprint",
            "liquidationValue",
            "useValue",
            "footprintCells",
            "contents");

        return new ManifestLotSnapshot(
            ReadIdentifier(source, "providerId"),
            ReadText(source, "providerLabel"),
            ReadIdentifier(source, "lotId"),
            ReadText(source, "displayName"),
            ReadText(source, "purpose"),
            ReadIdentifier(source, "familyId"),
            ReadIdentifier(source, "trackId"),
            ReadEnum<RewardRarity>(source, "grade"),
            ReadIdentifier(source, "anchorTemplateId"),
            ReadFingerprint(source, "fingerprint"),
            ReadNullableLongInRange(
                source,
                "liquidationValue",
                0,
                ManifestProtocolValidation.MaximumMoneyValue),
            ReadNullableLongInRange(
                source,
                "useValue",
                1,
                ManifestProtocolValidation.MaximumMoneyValue),
            ReadNullableIntInRange(
                source,
                "footprintCells",
                1,
                ManifestProtocolValidation.MaximumFootprintCells),
            ParseContents(RequireProperty(source, "contents")));
    }

    private static IReadOnlyList<ManifestLotContentSnapshot> ParseContents(JToken token)
    {
        var array = RequireArray(token, "lot contents", 1, MaximumContents);
        var contents = new ManifestLotContentSnapshot[array.Count];
        for (var index = 0; index < array.Count; index++)
        {
            var source = RequireObject(array[index], "lot content");
            RequireExactProperties(
                source,
                "lot content",
                "templateId",
                "displayName",
                "quantity");
            contents[index] = new ManifestLotContentSnapshot(
                ReadIdentifier(source, "templateId"),
                ReadText(source, "displayName"),
                ReadLongInRange(source, "quantity", 1, int.MaxValue));
        }

        return contents;
    }

    private static ManifestAvailableActionsSnapshot ParseAvailableActions(JObject source)
    {
        RequireExactProperties(
            source,
            "available actions",
            "canLock",
            "canBurn",
            "canClaim",
            "canRelay",
            "canForfeit",
            "expectedOrdinal",
            "expectedPhase",
            "expectedRelayStage");
        return new ManifestAvailableActionsSnapshot(
            ReadBoolean(source, "canLock"),
            ReadBoolean(source, "canBurn"),
            ReadBoolean(source, "canClaim"),
            ReadBoolean(source, "canRelay"),
            ReadBoolean(source, "canForfeit"),
            ReadIntInRange(source, "expectedOrdinal", 1, OfferCount),
            ReadEnum<ManifestPhase>(source, "expectedPhase"),
            ReadIntInRange(source, "expectedRelayStage", 1, RelayRules.MaximumStage));
    }

    private static ManifestRelayPropositionSnapshot? ParseNullableRelay(JToken token)
    {
        if (token.Type == JTokenType.Null)
        {
            return null;
        }

        var source = RequireObject(token, "Relay proposition");
        RequireExactProperties(
            source,
            "Relay proposition",
            "stage",
            "upgradePercent",
            "sidegradePercent",
            "confiscatePercent",
            "favorBefore",
            "favorAfterOnLoss",
            "guaranteeActive",
            "keyCost",
            "upgradeGrade",
            "candidateValueMin",
            "candidateValueMax",
            "sidegradeEndsChain",
            "terminalReason");
        return new ManifestRelayPropositionSnapshot(
            ReadIntInRange(source, "stage", 1, RelayRules.MaximumStage),
            ReadIntInRange(source, "upgradePercent", 0, 100),
            ReadIntInRange(source, "sidegradePercent", 0, 100),
            ReadIntInRange(source, "confiscatePercent", 0, 100),
            ReadIntInRange(source, "favorBefore", 0, RelayRules.MaximumRecoveryMeter),
            ReadIntInRange(source, "favorAfterOnLoss", 0, RelayRules.MaximumRecoveryMeter),
            ReadBoolean(source, "guaranteeActive"),
            ReadLongInRange(source, "keyCost", 1, ManifestProtocolValidation.MaximumMoneyValue),
            ReadNullableEnum<RewardRarity>(source, "upgradeGrade"),
            ReadNullableLongInRange(
                source,
                "candidateValueMin",
                1,
                ManifestProtocolValidation.MaximumMoneyValue),
            ReadNullableLongInRange(
                source,
                "candidateValueMax",
                1,
                ManifestProtocolValidation.MaximumMoneyValue),
            ReadBoolean(source, "sidegradeEndsChain"),
            ReadNullableText(source, "terminalReason"));
    }

    private static ManifestRelayReceiptSnapshot? ParseNullableReceipt(JToken token)
    {
        if (token.Type == JTokenType.Null)
        {
            return null;
        }

        var source = RequireObject(token, "latest receipt");
        RequireExactProperties(
            source,
            "latest receipt",
            "stage",
            "outcome",
            "inputDisplayName",
            "inputGrade",
            "outputDisplayName",
            "outputGrade",
            "brokerFavorBefore",
            "brokerFavorAfter");
        return new ManifestRelayReceiptSnapshot(
            ReadIntInRange(source, "stage", 1, RelayRules.MaximumStage),
            ReadEnum<ManifestRelayResult>(source, "outcome"),
            ReadText(source, "inputDisplayName"),
            ReadEnum<RewardRarity>(source, "inputGrade"),
            ReadNullableText(source, "outputDisplayName"),
            ReadNullableEnum<RewardRarity>(source, "outputGrade"),
            ReadIntInRange(source, "brokerFavorBefore", 0, RelayRules.MaximumRecoveryMeter),
            ReadIntInRange(source, "brokerFavorAfter", 0, RelayRules.MaximumRecoveryMeter));
    }

    private static void ValidateSnapshot(ManifestSnapshot snapshot)
    {
        if (snapshot.CaseTemplateId == CaseContracts.CashCache &&
            (snapshot.CurrentOrdinal != 1 || snapshot.Phase is ManifestPhase.Offer1 or ManifestPhase.Offer2 or ManifestPhase.RelayPrepared or ManifestPhase.Confiscated ||
             snapshot.Phase != ManifestPhase.TicketPrepared && snapshot.LockedOrdinal != 1 ||
             snapshot.RelayStage != 1 || snapshot.LatestReceipt is not null || snapshot.Relay is not null ||
             snapshot.AvailableActions.CanLock || snapshot.AvailableActions.CanBurn || snapshot.AvailableActions.CanRelay))
            throw new ManifestSnapshotException("Cash Cache cannot publish offer choices or Relay state.");
        var terminalPhase = IsTerminal(snapshot.Phase);
        if (!terminalPhase && snapshot.CatalogSnapshotId is null)
        {
            throw new ManifestSnapshotException("An active Manifest must authenticate its catalog snapshot.");
        }
        if (snapshot.TicketCommitted != (snapshot.Phase != ManifestPhase.TicketPrepared))
        {
            throw new ManifestSnapshotException("The Manifest ticket marker contradicts its phase.");
        }
        if (snapshot.Phase == ManifestPhase.TicketPrepared)
        {
            if (!IsMongoId(snapshot.RecoveryCaseItemId) || snapshot.CurrentOrdinal != 1)
            {
                throw new ManifestSnapshotException("Ticket recovery is missing its exact case identity.");
            }
        }
        else if (snapshot.RecoveryCaseItemId is not null)
        {
            throw new ManifestSnapshotException("Only TicketPrepared may publish a recovery case identity.");
        }

        if (terminalPhase)
        {
            if (!snapshot.RelayTerminal || snapshot.CurrentLot is not null)
            {
                throw new ManifestSnapshotException("A terminal Manifest contains active-lot state.");
            }
        }
        else if (snapshot.Phase == ManifestPhase.TicketPrepared)
        {
            if (snapshot.RelayTerminal || snapshot.CurrentLot is not null)
            {
                throw new ManifestSnapshotException("TicketPrepared cannot reveal an active lot.");
            }
        }
        else if (snapshot.CurrentLot is null)
        {
            throw new ManifestSnapshotException("A non-terminal active Manifest must publish its current lot.");
        }

        if (snapshot.Phase == ManifestPhase.Offer1 && snapshot.CurrentOrdinal != 1 ||
            snapshot.Phase == ManifestPhase.Offer2 && snapshot.CurrentOrdinal != 2)
        {
            throw new ManifestSnapshotException("The offer phase contradicts the current ordinal.");
        }

        var requiresLockedOrdinal = snapshot.Phase is
            ManifestPhase.Entitlement or
            ManifestPhase.ClaimPrepared or
            ManifestPhase.RelayPrepared or
            ManifestPhase.RewardOwed or
            ManifestPhase.Granted or
            ManifestPhase.Confiscated;
        var forbidsLockedOrdinal = snapshot.Phase is
            ManifestPhase.TicketPrepared or
            ManifestPhase.Offer1 or
            ManifestPhase.Offer2;
        if (requiresLockedOrdinal && snapshot.LockedOrdinal is null ||
            forbidsLockedOrdinal && snapshot.LockedOrdinal is not null ||
            snapshot.LockedOrdinal is int locked && locked != snapshot.CurrentOrdinal)
        {
            throw new ManifestSnapshotException("The locked ordinal contradicts the Manifest phase.");
        }

        ValidateCurrentLot(snapshot);
        ValidateReceipt(snapshot);
        ValidateSeals(snapshot);
        ValidatePremiumChoices(snapshot);
        ValidateActions(snapshot);
        ValidateRelay(snapshot);
    }

    private static void ValidateSeals(ManifestSnapshot snapshot)
    {
        var expectedCount = CaseContracts.OfferCount(snapshot.CaseTemplateId);
        if (snapshot.FamilySeals.Count != expectedCount ||
            !snapshot.IsPremium && snapshot.FamilySeals.Select(seal => seal.FamilyId).Distinct(StringComparer.Ordinal).Count() != expectedCount)
        {
            throw new ManifestSnapshotException("A Manifest must publish three distinct family seals.");
        }

        for (var index = 0; index < snapshot.FamilySeals.Count; index++)
        {
            var seal = snapshot.FamilySeals[index];
            var ordinal = index + 1;
            if (seal.Ordinal != ordinal || seal.RiskBand != ManifestRiskBand.Mixed ||
                seal.Burned && seal.Locked ||
                (seal.Burned || seal.Locked) && !seal.Revealed ||
                seal.Locked != (snapshot.LockedOrdinal == ordinal))
            {
                throw new ManifestSnapshotException("The family seal sequence is contradictory.");
            }
        }

        if (snapshot.Phase == ManifestPhase.TicketPrepared)
        {
            if (snapshot.FamilySeals.Any(seal => seal.Revealed || seal.Burned || seal.Locked))
            {
                throw new ManifestSnapshotException("TicketPrepared cannot reveal a family seal.");
            }
            return;
        }

        if (snapshot.CurrentLot is not null)
        {
            var currentSeal = snapshot.FamilySeals[snapshot.CurrentOrdinal - 1];
            var familyMatches = string.Equals(
                currentSeal.FamilyId,
                CaseContracts.UsesTrackGroups(snapshot.CaseTemplateId) ? snapshot.CurrentLot.TrackId : snapshot.CurrentLot.FamilyId,
                StringComparison.Ordinal);
            // Historical Black Site commitments can use distinct authored
            // families with repeated tracks. The server retains those seals.
            familyMatches |= snapshot.CaseTemplateId == CaseContracts.BlackSite &&
                string.Equals(currentSeal.FamilyId, snapshot.CurrentLot.FamilyId, StringComparison.Ordinal);
            if (!currentSeal.Revealed || currentSeal.Burned ||
                !familyMatches && !LatestReceiptAuthenticatesReplacement(snapshot))
            {
                throw new ManifestSnapshotException("The current lot does not match its revealed family seal.");
            }
        }

        if (snapshot.IsPremium)
        {
            if (snapshot.FamilySeals.Any(seal => !seal.Revealed || seal.Burned))
                throw new ManifestSnapshotException("Premium choices must be revealed without discards.");
        }
        else if (!IsTerminal(snapshot.Phase))
        {
            for (var ordinal = 1; ordinal <= expectedCount; ordinal++)
            {
                var seal = snapshot.FamilySeals[ordinal - 1];
                if (ordinal < snapshot.CurrentOrdinal && (!seal.Revealed || !seal.Burned) ||
                    ordinal > snapshot.CurrentOrdinal && (seal.Revealed || seal.Burned || seal.Locked))
                {
                    throw new ManifestSnapshotException("The seal history contradicts the current ordinal.");
                }
            }
        }
    }

    private static void ValidatePremiumChoices(ManifestSnapshot snapshot)
    {
        if (snapshot.IsPremium && (snapshot.CaseTemplateId == CaseContracts.CashCache ||
                snapshot.Phase is ManifestPhase.TicketPrepared or ManifestPhase.Offer2 || snapshot.AvailableActions.CanBurn))
            throw new ManifestSnapshotException("The premium tier contradicts this opening.");
        var choosing = snapshot.IsPremium && snapshot.Phase == ManifestPhase.Offer1;
        if (snapshot.PremiumChoices.Count != (choosing ? 3 : 0))
            throw new ManifestSnapshotException("Premium openings must expose exactly three saved choices.");
        if (!choosing) return;
        if (snapshot.PremiumChoices.Select(lot => (lot.ProviderId, lot.LotId, lot.Fingerprint)).Distinct().Count() != 3)
            throw new ManifestSnapshotException("Premium choices must be distinct packages.");
        for (var index = 0; index < 3; index++)
        {
            var lot = snapshot.PremiumChoices[index];
            if (snapshot.OpeningTier == ManifestOpeningTier.Legendary ? lot.Grade != RewardRarity.BlackLabel
                : lot.Grade is not (RewardRarity.Restricted or RewardRarity.BlackLabel))
                throw new ManifestSnapshotException("A premium choice is below the promised grade.");
            var group = snapshot.FamilySeals[index].FamilyId;
            if (group != (CaseContracts.UsesTrackGroups(snapshot.CaseTemplateId) ? lot.TrackId : lot.FamilyId) &&
                !(snapshot.CaseTemplateId == CaseContracts.BlackSite && group == lot.FamilyId))
                throw new ManifestSnapshotException("Premium choice does not match its seal.");
        }
        if (snapshot.CurrentLot?.Fingerprint != snapshot.PremiumChoices[0].Fingerprint ||
            snapshot.CurrentLot.ProviderId != snapshot.PremiumChoices[0].ProviderId ||
            snapshot.CurrentLot.LotId != snapshot.PremiumChoices[0].LotId)
            throw new ManifestSnapshotException("Premium opening preview does not match its first saved choice.");
    }

    private static void RequirePropertiesWithExtension(JObject source, string label, string[] extension, params string[] required)
    {
        var present = extension.Any(name => source.Property(name) is not null);
        RequireExactProperties(source, label, present ? required.Concat(extension).ToArray() : required);
    }

    private static bool LatestReceiptAuthenticatesReplacement(ManifestSnapshot snapshot)
    {
        if (snapshot.Phase is not (ManifestPhase.Entitlement or
                ManifestPhase.ClaimPrepared or
                ManifestPhase.RewardOwed or
                ManifestPhase.RelayPrepared) ||
            snapshot.CurrentLot is not { } currentLot ||
            snapshot.LatestReceipt is not { } receipt ||
            receipt.Outcome == ManifestRelayResult.Confiscated)
        {
            return false;
        }

        return ReceiptAuthenticatesCurrentLot(receipt, currentLot);
    }

    private static bool ReceiptAuthenticatesCurrentLot(
        ManifestRelayReceiptSnapshot receipt,
        ManifestLotSnapshot currentLot) =>
        receipt.Outcome != ManifestRelayResult.Confiscated &&
        string.Equals(
            receipt.OutputDisplayName,
            currentLot.DisplayName,
            StringComparison.Ordinal) &&
        receipt.OutputGrade == currentLot.Grade;

    private static void ValidateCurrentLot(ManifestSnapshot snapshot)
    {
        var lot = snapshot.CurrentLot;
        if (lot is null)
        {
            return;
        }

        var allValuesPresent = lot.LiquidationValue is not null &&
            lot.UseValue is not null && lot.FootprintCells is not null;
        var allValuesMissing = lot.LiquidationValue is null &&
            lot.UseValue is null && lot.FootprintCells is null;
        var preparedRecovery = snapshot.Phase is ManifestPhase.ClaimPrepared or
            ManifestPhase.RewardOwed or ManifestPhase.RelayPrepared;
        var unpricedCashClaim = snapshot.CaseTemplateId == CaseContracts.CashCache &&
            snapshot.Phase == ManifestPhase.Entitlement;
        if (!allValuesPresent && !allValuesMissing ||
            allValuesMissing && !snapshot.MissingContentBlocked && !preparedRecovery && !unpricedCashClaim)
        {
            throw new ManifestSnapshotException("The current lot has a partial or unexplained valuation.");
        }

        if (!lot.Contents.Any(content =>
                string.Equals(content.TemplateId, lot.AnchorTemplateId, StringComparison.Ordinal)) ||
            lot.Contents.Select(content => content.TemplateId).Distinct(StringComparer.Ordinal).Count() != lot.Contents.Count)
        {
            throw new ManifestSnapshotException("The current lot content summary is not canonical.");
        }
        for (var index = 1; index < lot.Contents.Count; index++)
        {
            if (StringComparer.Ordinal.Compare(
                    lot.Contents[index - 1].TemplateId,
                    lot.Contents[index].TemplateId) >= 0)
            {
                throw new ManifestSnapshotException("The current lot contents are not in canonical template order.");
            }
        }
    }

    private static void ValidateActions(ManifestSnapshot snapshot)
    {
        var actions = snapshot.AvailableActions;
        if (actions.ExpectedOrdinal != snapshot.CurrentOrdinal ||
            actions.ExpectedPhase != snapshot.Phase ||
            actions.ExpectedRelayStage != snapshot.RelayStage)
        {
            throw new ManifestSnapshotException("The action preconditions do not authenticate the current state.");
        }

        var anyNormalAction = actions.CanLock || actions.CanBurn || actions.CanClaim || actions.CanRelay;
        if (snapshot.MissingContentBlocked)
        {
            if (snapshot.Phase is not (ManifestPhase.Offer1 or
                    ManifestPhase.Offer2 or ManifestPhase.Entitlement) ||
                anyNormalAction || !actions.CanForfeit)
            {
                throw new ManifestSnapshotException("Missing-content action availability is contradictory.");
            }
            return;
        }
        if (actions.CanForfeit)
        {
            throw new ManifestSnapshotException("Forfeit is available without a missing-content block.");
        }

        switch (snapshot.Phase)
        {
            case ManifestPhase.Offer1:
            case ManifestPhase.Offer2:
                if (!actions.CanLock || actions.CanClaim || actions.CanRelay)
                {
                    throw new ManifestSnapshotException("Offer action availability is contradictory.");
                }
                break;
            case ManifestPhase.Entitlement:
                if (actions.CanLock || actions.CanBurn || !actions.CanClaim ||
                    snapshot.RelayTerminal && actions.CanRelay)
                {
                    throw new ManifestSnapshotException("Entitlement action availability is contradictory.");
                }
                break;
            case ManifestPhase.ClaimPrepared:
            case ManifestPhase.RewardOwed:
                if (actions.CanLock || actions.CanBurn || !actions.CanClaim || actions.CanRelay)
                {
                    throw new ManifestSnapshotException("Claim recovery action availability is contradictory.");
                }
                break;
            case ManifestPhase.RelayPrepared:
                if (actions.CanLock || actions.CanBurn || actions.CanClaim || !actions.CanRelay)
                {
                    throw new ManifestSnapshotException("Relay recovery action availability is contradictory.");
                }
                break;
            default:
                if (anyNormalAction)
                {
                    throw new ManifestSnapshotException("A non-interactive Manifest publishes an action.");
                }
                break;
        }
    }

    private static void ValidateRelay(ManifestSnapshot snapshot)
    {
        var relay = snapshot.Relay;
        if (snapshot.AvailableActions.CanRelay && relay is null)
        {
            throw new ManifestSnapshotException("Relay is available without a published proposition.");
        }
        if (relay is null)
        {
            return;
        }
        if (snapshot.Phase is not (ManifestPhase.Entitlement or ManifestPhase.RelayPrepared) ||
            snapshot.CurrentLot is null ||
            relay.Stage != snapshot.RelayStage ||
            relay.FavorBefore != snapshot.BrokerFavor)
        {
            throw new ManifestSnapshotException("The Relay proposition does not describe the current entitlement.");
        }

        var expectedOdds = RelayRules.GetOdds(relay.Stage);
        if (relay.UpgradePercent != expectedOdds.UpgradePercent ||
            relay.SidegradePercent != expectedOdds.SidegradePercent ||
            relay.ConfiscatePercent != expectedOdds.ConfiscatePercent ||
            relay.FavorAfterOnLoss != Math.Min(
                RelayRules.MaximumRecoveryMeter,
                relay.FavorBefore + 1) ||
            relay.GuaranteeActive != (relay.FavorBefore == RelayRules.MaximumRecoveryMeter) ||
            !relay.SidegradeEndsChain)
        {
            throw new ManifestSnapshotException("The Relay proposition contradicts the published rules.");
        }

        var candidateValuesBothNull = relay.CandidateValueMin is null && relay.CandidateValueMax is null;
        var candidateValuesBothPresent = relay.CandidateValueMin is not null && relay.CandidateValueMax is not null;
        if (!candidateValuesBothNull && !candidateValuesBothPresent ||
            candidateValuesBothPresent && relay.CandidateValueMin > relay.CandidateValueMax)
        {
            throw new ManifestSnapshotException("The Relay candidate value range is invalid.");
        }

        var expectedUpgrade = snapshot.CurrentLot.Grade == RewardRarity.BlackLabel || snapshot.RelayTerminal
            ? (RewardRarity?)null
            : RelayRules.GetUpgradeRarity(
                snapshot.CurrentLot.Grade,
                snapshot.RarityLadderVersion);
        var preparedWithoutLiveValuation = snapshot.Phase == ManifestPhase.RelayPrepared &&
            candidateValuesBothNull;
        if (relay.UpgradeGrade != expectedUpgrade ||
            snapshot.AvailableActions.CanRelay &&
            (snapshot.RelayTerminal ||
             candidateValuesBothNull && !preparedWithoutLiveValuation ||
             relay.TerminalReason is not null) ||
            !snapshot.AvailableActions.CanRelay && relay.TerminalReason is null)
        {
            throw new ManifestSnapshotException("The Relay eligibility disclosure is contradictory.");
        }
    }

    private static void ValidateReceipt(ManifestSnapshot snapshot)
    {
        var receipt = snapshot.LatestReceipt;
        if (receipt is null)
        {
            if (snapshot.RelayStage != 1 ||
                snapshot.Phase == ManifestPhase.Confiscated ||
                !IsTerminal(snapshot.Phase) && snapshot.RelayTerminal)
            {
                throw new ManifestSnapshotException(
                    "The active post-Relay state is missing its committed Relay evidence.");
            }

            return;
        }
        if (snapshot.Phase is ManifestPhase.TicketPrepared or
            ManifestPhase.Offer1 or
            ManifestPhase.Offer2)
        {
            throw new ManifestSnapshotException(
                "A Manifest cannot publish Relay evidence before an entitlement exists.");
        }
        if (receipt.BrokerFavorAfter != snapshot.BrokerFavor)
        {
            throw new ManifestSnapshotException("The latest receipt does not match current Broker Favor.");
        }

        var confiscated = receipt.Outcome == ManifestRelayResult.Confiscated;
        var outputBothNull = receipt.OutputDisplayName is null && receipt.OutputGrade is null;
        var outputBothPresent = receipt.OutputDisplayName is not null && receipt.OutputGrade is not null;
        if (confiscated ? !outputBothNull : !outputBothPresent)
        {
            throw new ManifestSnapshotException("The latest receipt output contradicts its outcome.");
        }

        if (receipt.Outcome == ManifestRelayResult.Upgrade &&
            receipt.OutputGrade != RelayRules.GetUpgradeRarity(
                receipt.InputGrade,
                snapshot.RarityLadderVersion) ||
            receipt.Outcome == ManifestRelayResult.Sidegrade &&
            receipt.OutputGrade != receipt.InputGrade)
        {
            throw new ManifestSnapshotException("The latest receipt grade transition is invalid.");
        }

        var expectedRelayStage = receipt.Outcome == ManifestRelayResult.Upgrade &&
            receipt.Stage < RelayRules.MaximumStage
                ? receipt.Stage + 1
                : receipt.Stage;
        if (snapshot.RelayStage != expectedRelayStage)
        {
            throw new ManifestSnapshotException(
                "The latest Relay receipt does not authenticate the current Relay stage.");
        }
        if (confiscated != (snapshot.Phase == ManifestPhase.Confiscated))
        {
            throw new ManifestSnapshotException(
                "The latest Relay receipt outcome contradicts the Manifest phase.");
        }
        if (!IsTerminal(snapshot.Phase))
        {
            var expectedRelayTerminal = receipt.Outcome switch
            {
                ManifestRelayResult.Sidegrade => true,
                ManifestRelayResult.Upgrade =>
                    receipt.OutputGrade == RewardRarity.BlackLabel ||
                    receipt.Stage == RelayRules.MaximumStage,
                ManifestRelayResult.Confiscated => true,
                _ => throw new ArgumentOutOfRangeException()
            };
            if (snapshot.RelayTerminal != expectedRelayTerminal)
            {
                throw new ManifestSnapshotException(
                    "The latest Relay receipt contradicts the active Relay terminal marker.");
            }
        }
        if (snapshot.CurrentLot is { } currentLot &&
            !ReceiptAuthenticatesCurrentLot(receipt, currentLot))
        {
            throw new ManifestSnapshotException(
                "The latest Relay receipt does not authenticate the current lot.");
        }

        var expectedFavor = receipt.BrokerFavorBefore == RelayRules.MaximumRecoveryMeter
            ? receipt.Outcome == ManifestRelayResult.Upgrade
                ? 0
                : throw new ManifestSnapshotException("Full Broker Favor did not guarantee an upgrade.")
            : confiscated
                ? Math.Min(RelayRules.MaximumRecoveryMeter, receipt.BrokerFavorBefore + 1)
                : receipt.BrokerFavorBefore;
        if (receipt.BrokerFavorAfter != expectedFavor)
        {
            throw new ManifestSnapshotException("The latest receipt has an invalid Broker Favor transition.");
        }
    }

    private static bool IsTerminal(ManifestPhase phase) => phase is
        ManifestPhase.Granted or ManifestPhase.Confiscated or ManifestPhase.Forfeited;

    private static bool IsMongoId(string? value) =>
        value is { Length: 24 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static JObject RequireObject(JToken? token, string label) =>
        token as JObject ?? throw new ManifestSnapshotException($"The {label} must be an object.");

    private static JArray RequireArray(JToken token, string label, int minimum, int maximum)
    {
        if (token is not JArray array || array.Count < minimum || array.Count > maximum ||
            array.Any(item => item is null || item.Type == JTokenType.Null))
        {
            throw new ManifestSnapshotException($"The {label} list is malformed or exceeds its bound.");
        }

        return array;
    }

    private static void RequireExactProperties(JObject source, string label, params string[] names)
    {
        var actual = source.Properties().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length ||
            actual.Any(name => !names.Contains(name, StringComparer.Ordinal)) ||
            names.Any(name => source.Property(name, StringComparison.Ordinal) is null))
        {
            throw new ManifestSnapshotException($"The {label} contains missing or unknown properties.");
        }
    }

    private static JToken RequireProperty(JObject source, string name) =>
        source.Property(name, StringComparison.Ordinal)?.Value
        ?? throw new ManifestSnapshotException($"The required property '{name}' is missing.");

    private static bool ReadBoolean(JObject source, string name)
    {
        var token = RequireProperty(source, name);
        if (token.Type != JTokenType.Boolean)
        {
            throw new ManifestSnapshotException($"The property '{name}' must be a Boolean.");
        }

        return token.Value<bool>();
    }

    private static int ReadInt(JObject source, string name)
    {
        var value = ReadLong(source, name);
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new ManifestSnapshotException($"The property '{name}' exceeds the integer range.");
        }

        return (int)value;
    }

    private static int ReadIntInRange(JObject source, string name, int minimum, int maximum)
    {
        var value = ReadInt(source, name);
        if (value < minimum || value > maximum)
        {
            throw new ManifestSnapshotException($"The property '{name}' is outside its supported range.");
        }

        return value;
    }

    private static int? ReadNullableIntInRange(
        JObject source,
        string name,
        int minimum,
        int maximum)
    {
        var token = RequireProperty(source, name);
        return token.Type == JTokenType.Null
            ? null
            : ReadIntTokenInRange(token, name, minimum, maximum);
    }

    private static int ReadIntTokenInRange(JToken token, string name, int minimum, int maximum)
    {
        var value = ReadLongToken(token, name);
        if (value < minimum || value > maximum)
        {
            throw new ManifestSnapshotException($"The property '{name}' is outside its supported range.");
        }

        return (int)value;
    }

    private static long ReadLong(JObject source, string name) =>
        ReadLongToken(RequireProperty(source, name), name);

    private static long ReadLongToken(JToken token, string name)
    {
        if (token.Type != JTokenType.Integer)
        {
            throw new ManifestSnapshotException($"The property '{name}' must be an integer.");
        }

        try
        {
            return token.Value<long>();
        }
        catch (Exception exception) when (exception is OverflowException or FormatException)
        {
            throw new ManifestSnapshotException($"The property '{name}' exceeds the integer range.", exception);
        }
    }

    private static long ReadLongInRange(JObject source, string name, long minimum, long maximum)
    {
        var value = ReadLong(source, name);
        if (value < minimum || value > maximum)
        {
            throw new ManifestSnapshotException($"The property '{name}' is outside its supported range.");
        }

        return value;
    }

    private static long? ReadNullableLongInRange(
        JObject source,
        string name,
        long minimum,
        long maximum)
    {
        var token = RequireProperty(source, name);
        if (token.Type == JTokenType.Null)
        {
            return null;
        }

        var value = ReadLongToken(token, name);
        if (value < minimum || value > maximum)
        {
            throw new ManifestSnapshotException($"The property '{name}' is outside its supported range.");
        }

        return value;
    }

    private static string ReadIdentifier(JObject source, string name) =>
        ValidateString(source, name, ManifestProtocolValidation.RequireIdentifier);

    private static string? ReadNullableIdentifier(JObject source, string name)
    {
        var token = RequireProperty(source, name);
        return token.Type == JTokenType.Null
            ? null
            : ValidateStringToken(token, name, ManifestProtocolValidation.RequireIdentifier);
    }

    private static string ReadText(JObject source, string name) =>
        ValidateString(source, name, ManifestProtocolValidation.RequireText);

    private static string? ReadNullableText(JObject source, string name)
    {
        var token = RequireProperty(source, name);
        return token.Type == JTokenType.Null
            ? null
            : ValidateStringToken(token, name, ManifestProtocolValidation.RequireText);
    }

    private static string? ReadNullableString(
        JObject source,
        string name,
        bool allowEmpty = false)
    {
        var token = RequireProperty(source, name);
        if (token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token.Type != JTokenType.String)
        {
            throw new ManifestSnapshotException($"The property '{name}' must be a string or null.");
        }

        var value = token.Value<string>()!;
        if (allowEmpty && value.Length == 0)
        {
            return value;
        }

        return ManifestProtocolValidation.RequireIdentifier(value, name);
    }

    private static string ReadFingerprint(JObject source, string name)
    {
        var value = ReadIdentifier(source, name);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ManifestSnapshotException($"The property '{name}' is not a canonical SHA-256 digest.");
        }

        return value;
    }

    private static string ValidateString(
        JObject source,
        string name,
        Func<string?, string, string> validator) =>
        ValidateStringToken(RequireProperty(source, name), name, validator);

    private static string ValidateStringToken(
        JToken token,
        string name,
        Func<string?, string, string> validator)
    {
        if (token.Type != JTokenType.String)
        {
            throw new ManifestSnapshotException($"The property '{name}' must be a string.");
        }

        return validator(token.Value<string>(), name);
    }

    private static TEnum ReadEnum<TEnum>(JObject source, string name)
        where TEnum : struct, Enum
    {
        var value = ReadIdentifier(source, name);
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(typeof(TEnum), parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new ManifestSnapshotException($"The property '{name}' contains an unknown canonical value.");
        }

        return parsed;
    }

    private static TEnum? ReadNullableEnum<TEnum>(JObject source, string name)
        where TEnum : struct, Enum
    {
        var token = RequireProperty(source, name);
        if (token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token.Type != JTokenType.String)
        {
            throw new ManifestSnapshotException($"The property '{name}' must be a string or null.");
        }

        var value = token.Value<string>()!;
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(typeof(TEnum), parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new ManifestSnapshotException($"The property '{name}' contains an unknown canonical value.");
        }

        return parsed;
    }
}
