using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using UnityEngine;
using UnityEngine.UI;

namespace ContrabandCases.Client.UI;

internal sealed partial class RouletteOverlay
{
    private Button? _decisionClose;
    private GameObject? _brokerPanel;
    private readonly Dictionary<string, Image> _brokerImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sprite?> _brokerSpriteCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Text> _brokerImageLabels = new(StringComparer.Ordinal);
    private RelayHoldButton? _brokerHold;
    private static Color BrokerSurface => new(0.045f, 0.055f, 0.062f, 0.99f);
    private static Color BrokerAction => new(0.48f, 0.35f, 0.16f);
    private static Color BrokerSecondary => new(0.14f, 0.18f, 0.21f);

    private void ResetBrokerPanels()
    {
        _brokerHold?.Clear();
        _brokerHold = null;
        if (_decisionClose != null)
        {
            _decisionClose.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_decisionClose.gameObject);
            _decisionClose = null;
        }
        if (_brokerPanel != null)
        {
            _brokerPanel.SetActive(false);
            UnityEngine.Object.Destroy(_brokerPanel);
            _brokerPanel = null;
        }
        _brokerImages.Clear();
        _brokerImageLabels.Clear();
    }

    private bool BeginBrokerPanel(string title, string subtitle, bool allowRebuild = false)
    {
        if (!Activate(allowRebuild)) return false;
        ResetPanels();
        ReleaseTiles();
        var panel = UiFactory.CreateImage("Broker", _confirmationPanel.transform.parent, BrokerSurface, true);
        UiFactory.Center(panel.rectTransform, 1240f, 900f);
        _brokerPanel = panel.gameObject;
        var line = UiFactory.CreateImage("Rule", panel.transform, new Color(0.48f, 0.57f, 0.62f));
        UiFactory.Center(line.rectTransform, 1160f, 3f, 0f, 300f);
        BrokerLabel("Heading", title, 38, 1120, 72, 0, 386, TextAnchor.MiddleLeft);
        BrokerLabel("Subheading", subtitle, 24, 1120, 58, 0, 324, TextAnchor.MiddleLeft);
        return true;
    }

    private Text BrokerLabel(string id, string text, int size, float width, float height, float x, float y,
        TextAnchor align = TextAnchor.MiddleLeft)
    {
        var label = UiFactory.CreateText(id, _brokerPanel!.transform, _font, size, align, Color.white);
        UiFactory.Center(label.rectTransform, width, height, x, y);
        label.text = text;
        return label;
    }

    private Button BrokerButton(string id, string label, float x, Action action, Action close,
        float width = 260, bool primary = false, float y = -385)
    {
        var button = UiFactory.CreateButton(id, _brokerPanel!.transform, _font, label,
            primary ? BrokerAction : BrokerSecondary);
        UiFactory.Center(button.GetComponent<RectTransform>(), width, 62, x, y);
        button.GetComponentInChildren<Text>().fontSize = 24;
        SetButton(button, action);
        SetCancelHandler(button, close);
        return button;
    }

    private Image BrokerImage(string id, float width, float height, float x, float y, Transform? parent = null)
    {
        var image = UiFactory.CreateImage(id, parent ?? _brokerPanel!.transform, new Color(0.12f, 0.15f, 0.17f));
        UiFactory.Center(image.rectTransform, width, height, x, y);
        image.preserveAspect = true;
        var label = UiFactory.CreateText("ArtworkStatus", image.transform, _font, 22, TextAnchor.MiddleCenter,
            new Color(0.72f, 0.78f, 0.8f));
        UiFactory.Stretch(label.rectTransform, 8, 8, -8, -8);
        label.text = width < 160 ? "Loading…" : "Loading preview…";
        _brokerImages[id] = image;
        _brokerImageLabels[id] = label;
        if (_brokerSpriteCache.TryGetValue(id, out var cached)) BindBrokerSprite(id, cached);
        return image;
    }

    private void LinkBrokerControls(Action close, params Selectable[] buttons)
    {
        var scrollAreas = _brokerPanel!.GetComponentsInChildren<OverlayScrollKeys>().Select(keys =>
        {
            var button = keys.gameObject.AddComponent<Button>();
            button.targetGraphic = keys.GetComponent<Image>();
            UiFactory.AddFocusTreatment(button);
            SetCancelHandler(button, close);
            return button;
        });
        var controls = buttons.Concat(scrollAreas).ToArray();
        if (controls.Length == 1) LinkSelf(controls[0]);
        else LinkHorizontal(controls);
        _brokerPanel.AddComponent<OverlayTabNavigation>().Controls = controls;
    }

    private void BindBrokerSprite(string id, Sprite? sprite)
    {
        if (!_brokerImages.TryGetValue(id, out var image) || image == null) return;
        image.sprite = sprite;
        image.color = sprite != null ? Color.white : new Color(0.12f, 0.15f, 0.17f);
        if (_brokerImageLabels.TryGetValue(id, out var label) && label != null)
        {
            label.text = sprite == null
                ? image.rectTransform.rect.width < 160 ? "No\npreview" : "Preview unavailable\nSee item details"
                : "";
            label.gameObject.SetActive(sprite == null);
        }
    }

    internal void CompleteSpriteLoading(IEnumerable<string> tileIds, bool catalog = false)
    {
        foreach (var id in tileIds)
        {
            if (catalog && !_brokerSpriteCache.ContainsKey(id)) _brokerSpriteCache[id] = null;
            if (_brokerImages.TryGetValue(id, out var image) && image != null && image.sprite == null)
                BindBrokerSprite(id, null);
            foreach (var tile in _leased.Where(tile => tile.RewardId == id)) tile.CompleteSpriteLoading();
        }
    }

    private bool ShowBrokerConfirmation(ManifestOpeningOddsSnapshot odds, bool changed, Action confirm, Action cancel)
    {
        if (odds is null) throw new ArgumentNullException(nameof(odds));
        _brokerSpriteCache.Clear();
        void Page(int page)
        {
            if (!BeginBrokerPanel(BrokerPresentation.CaseName(odds.CaseTemplateId),
                    BrokerPresentation.Theme(odds.CaseTemplateId), true)) return;
            var buttons = new List<Button>();
            if (page == 0)
            {
                BrokerImage("case:hero", 330, 260, -380, 130);
                BrokerLabel("CaseRules", BrokerPresentation.Overview(odds), 26, 700, 385, 190, 72);
                BrokerLabel("TierRates", BrokerPresentation.TierRates(odds), 24, 1120, 120, 0, -180);
                if (changed) BrokerLabel("Changed", "CATALOG UPDATED — REVIEW BEFORE OPENING", 24, 1120, 45, 0, -275);
            }
            else
            {
                var body = BuildScrollableText("OpeningOdds", _brokerPanel!.transform, _font, 24,
                    1100, 585, -10, -15, Color.white);
                body.Text.text = page == 1 ? BrokerPresentation.ReadableOdds(odds)
                    : ManifestPresentationPolicy.OpeningOddsText(odds);
                BrokerLabel("ScrollHint", "SCROLL / PAGE UP / PAGE DOWN • No reward is selected until you open", 22, 1120, 32, 0, -323);
            }
            buttons.Add(BrokerButton("Overview", "Case overview", -430, () => Page(0), cancel, 260));
            buttons.Add(BrokerButton("Odds", page == 1 ? "Exact audit" : "View odds", -145, () => Page(page == 1 ? 2 : 1), cancel, 260));
            buttons.Add(BrokerButton("Open", "Open case + 1 key", 150, confirm, cancel, 285, true));
            buttons.Add(BrokerButton("Close", "Close", 445, cancel, cancel, 220));
            LinkBrokerControls(cancel, buttons.ToArray());
            Select(buttons[page == 0 ? 3 : 1]);
        }
        Page(0);
        return _brokerPanel != null;
    }

    private bool ShowPremiumCards(ManifestSnapshot snapshot, int index, Action<int> browse, Action choose, Action close)
    {
        if (!snapshot.IsPremium || snapshot.PremiumChoices.Count != 3 || !snapshot.AvailableActions.CanLock || index is < 0 or > 2)
            throw new InvalidOperationException("Three saved premium choices and a valid selection are required.");
        if (!BeginBrokerPanel($"Surprise {snapshot.OpeningTier} opening",
                "Choose one of three packages • No extra key")) return false;
        var buttons = new List<Button>();
        for (var i = 0; i < 3; i++)
        {
            var ordinal = i;
            var lot = snapshot.PremiumChoices[i];
            var card = UiFactory.CreateButton($"Package{i + 1}", _brokerPanel!.transform, _font, "",
                i == index ? new Color(0.22f, 0.28f, 0.31f) : BrokerSecondary);
            var rect = card.GetComponent<RectTransform>();
            UiFactory.Center(rect, 356, 500, (i - 1) * 376, 15);
            SetButton(card, () => browse(ordinal));
            SetCancelHandler(card, close);
            BrokerImage($"premium:{i}", 300, 118, 0, 151, card.transform);
            void Label(string name, string value, int size, float height, float y)
            {
                var label = UiFactory.CreateText(name, card.transform, _font, size, TextAnchor.MiddleCenter, Color.white);
                UiFactory.Center(label.rectTransform, 328, height, 0, y);
                if (name is "PackageName" or "Preview" or "Purpose") UiFactory.SetCardName(label, value);
                else label.text = value;
            }
            Label("PackageName", BrokerPresentation.Text(lot.DisplayName), 26, 66, 51);
            Label("Grade", RewardRarities.GetInfo(lot.Grade).DisplayName, 24, 32, 0);
            Label("Purpose", BrokerPresentation.Text(lot.Purpose), 22, 52, -44);
            Label("Preview", BrokerPresentation.ContentsPreview(lot), 22, 104, -125);
            Label("Value", BrokerPresentation.Value(lot, compact: true), 18, 62, -213);
            Label("Selected", i == index ? "Selected" : $"Package {i + 1}", 22, 32, 230);
            var rarity = UiFactory.CreateImage("Rarity", card.transform, UiFactory.RarityColor(lot.Grade));
            UiFactory.Center(rarity.rectTransform, 356, 5, 0, -248);
            if (i == index)
            {
                var selectedEdge = UiFactory.CreateImage("SelectedEdge", card.transform, Color.white);
                UiFactory.Center(selectedEdge.rectTransform, 356, 3, 0, 248);
            }
            buttons.Add(card);
        }
        var selectedLot = snapshot.PremiumChoices[index];
        BrokerLabel("ChoiceTerms", "Choosing keeps this whole package and gives up the other two.\nNext: send it to Messenger, or risk the package on Relay.", 24, 1120, 80, 0, -290);
        buttons.Add(BrokerButton("Choose", "Choose This Package", -365, choose, close, 365, true));
        buttons.Add(BrokerButton("Details", "Package Details", 32,
            () => ShowBrokerDetails("Package details", BrokerPresentation.PackageDetails(selectedLot),
                () => ShowPremiumCards(snapshot, index, browse, choose, close)), close, 370));
        buttons.Add(BrokerButton("Save", "Save & Close", 420, close, close, 300));
        LinkBrokerControls(close, buttons.ToArray());
        Select(buttons[index]);
        return true;
    }

    private bool ShowBrokerDossier(string text, Action close, Action? resume)
    {
        if (!BeginBrokerPanel("BROKER DOSSIER", "SAVED REWARDS / FAVOR / RECENT HISTORY")) return false;
        var body = BuildScrollableText("Dossier", _brokerPanel!.transform, _font, 26,
            1100, 580, -10, -15, Color.white);
        body.Text.text = BrokerPresentation.Text(text);
        var buttons = new List<Button>();
        if (resume is not null) buttons.Add(BrokerButton("Resume", "RESUME SAVED REWARD", -190, resume, close, 390, true));
        buttons.Add(BrokerButton("Close", "CLOSE DOSSIER", resume is null ? 0 : 230, close, close, 320));
        LinkBrokerControls(close, buttons.ToArray());
        Select(buttons[0]);
        return true;
    }

    internal bool ShowBrokerHome(ManifestLibrarySnapshot library, Action browse, Action status, Action dossier,
        Action close, Action? resume)
    {
        if (!BeginBrokerPanel("CONTRABAND BROKER", "REWARDS / SAVED OPENINGS / FIELD RECORDS")) return false;
        BrokerLabel("Pending", library.HasPending == true ? "A REWARD IS WAITING FOR YOU"
            : library.HasPending == false ? "NO PENDING REWARD" : "CHECK SAVED REWARDS", 32, 1100, 60, 0, 218);
        BrokerLabel("Intro", "Browse what your installed cases can offer.\nCatalog previews never spend a key or reveal your next saved offer.\n\nOpen an owned case from its inventory menu to begin.",
            28, 1100, 220, 0, 60);
        var buttons = new List<Button>();
        if (resume is not null) buttons.Add(BrokerButton("Resume", library.HasPending == true ? "RESUME SAVED REWARD" : "CHECK SAVED REWARDS",
            0, resume, close, 600, true, -120));
        buttons.Add(BrokerButton("Browse", "BROWSE REWARDS", -300, browse, close, 500, false, -220));
        buttons.Add(BrokerButton("Dossier", "DOSSIER & FAVOR", 300, dossier, close, 500, false, -220));
        buttons.Add(BrokerButton("Status", "STATUS & SOUND", -300, status, close, 500));
        buttons.Add(BrokerButton("Close", "CLOSE", 300, close, close, 500));
        LinkBrokerControls(close, buttons.ToArray());
        Select(buttons[0]);
        return true;
    }

    internal bool ShowBrokerStatus(string serverStatus, Action back)
    {
        if (!BeginBrokerPanel("BROKER STATUS", "INSTALLED CONTENT / CONNECTION / AUDIO")) return false;
        var body = BuildScrollableText("Status", _brokerPanel!.transform, _font, 24, 1100, 570, -10, -10, Color.white);
        var status = $"CLIENT VERSION {ContrabandCases.Shared.ModConstants.ModVersion}\n" + BrokerPresentation.Text(serverStatus) +
            "\n\nArtwork uses Tarkov item previews. Missing pictures never change the reward.\nEffects volume: " +
            $"{_effectsVolume():P0}. Interface/master volume also applies. Test Sound does not spend or grant anything.";
        body.Text.text = status;
        var test = BrokerButton("Audio", "TEST SOUND", -200, () =>
        {
            _tickPlayer.PlayOutcome(null, RewardRarity.Uncommon, _effectsVolume());
            body.Text.text = status + "\n\n" + _tickPlayer.Status;
        }, back, 340, true);
        var close = BrokerButton("Back", "BACK TO BROKER", 220, back, back, 340);
        LinkBrokerControls(back, test, close);
        Select(test);
        return true;
    }

    internal bool ShowLibraryPackage(ManifestLotSnapshot lot, Action back)
    {
        if (!BeginBrokerPanel("PACKAGE CONTENTS", "CATALOG PREVIEW ONLY • NOTHING SPENT OR GRANTED")) return false;
        BrokerImage($"lot:{lot.Fingerprint}", 340, 290, -380, 90);
        BrokerLabel("Grade", RewardRarities.GetInfo(lot.Grade).DisplayName, 30, 330, 55, -380, -115, TextAnchor.MiddleCenter);
        var body = BuildScrollableText("Contents", _brokerPanel!.transform, _font, 26, 700, 580, 190, -12, Color.white);
        body.Text.text = BrokerPresentation.PackageDetails(lot);
        var close = BrokerButton("Back", "BACK TO REWARDS", 0, back, back, 420);
        LinkBrokerControls(back, close);
        Select(close);
        return true;
    }

    internal int RewardBrowserColumns => BrokerLibraryLayout.ForScreen(Screen.width, Screen.height).Columns;

    private void ShowBrokerDetails(string title, string text, Action back)
    {
        if (!BeginBrokerPanel(title, "Details • Your selection is unchanged")) return;
        var body = BuildScrollableText("Details", _brokerPanel!.transform, _font, 26, 1100, 590, -10, -14, Color.white);
        body.Text.text = text;
        var button = BrokerButton("Back", "Back to Decision", 0, back, back, 400);
        LinkBrokerControls(back, button);
        Select(button);
    }

    private void ShowBrokerFilter(string title, IReadOnlyList<(string Id, string Label)> options, string selected,
        Action<string> choose, Action back, int page = 0)
    {
        if (!BeginBrokerPanel(title, "Select a filter • No items are spent or granted")) return;
        var buttons = new List<Button>();
        var maxPage = Math.Max(0, (options.Count - 1) / 8);
        page = Math.Clamp(page, 0, maxPage);
        foreach (var (option, i) in options.Skip(page * 8).Take(8).Select((option, i) => (option, i)))
        {
            var button = BrokerButton($"Filter{i}", (option.Id == selected ? "✓ " : "") + option.Label,
                i % 2 == 0 ? -280 : 280, () => choose(option.Id), back, 530, option.Id == selected, 220 - i / 2 * 130);
            buttons.Add(button);
        }
        if (maxPage > 0)
        {
            var previous = BrokerButton("Previous", "Previous", -380,
                () => ShowBrokerFilter(title, options, selected, choose, back, page - 1), back, 300);
            previous.interactable = page > 0;
            var next = BrokerButton("Next", "Next", 380,
                () => ShowBrokerFilter(title, options, selected, choose, back, page + 1), back, 300);
            next.interactable = page < maxPage;
            buttons.Add(previous);
            buttons.Add(next);
        }
        buttons.Add(BrokerButton("Back", "Back to Rewards", 0, back, back, 340));
        LinkBrokerControls(back, buttons.Where(b => b.interactable).ToArray());
        Select(buttons[0]);
    }

    internal bool ShowRewardBrowser(ManifestLibrarySnapshot library, string caseId, string family, string search,
        int page, int maxPage, int columns, int total,
        IReadOnlyList<ManifestLotSnapshot> lots, Action<string, string, string, int> navigate,
        Action<ManifestLotSnapshot> inspect, Action back)
    {
        if (!BeginBrokerPanel("Reward browser", "Public catalog • These previews are not your upcoming rewards")) return false;
        var controls = new List<Selectable>();
        var searchImage = UiFactory.CreateImage("SearchInput", _brokerPanel!.transform, BrokerSecondary, true);
        UiFactory.Center(searchImage.rectTransform, 850, 54, -135, 174);
        var input = searchImage.gameObject.AddComponent<InputField>();
        input.targetGraphic = searchImage;
        input.characterLimit = 100;
        input.lineType = InputField.LineType.SingleLine;
        var text = UiFactory.CreateText("SearchText", input.transform, _font, 24, TextAnchor.MiddleLeft, Color.white);
        UiFactory.Stretch(text.rectTransform, 16, 6, -16, -6);
        text.supportRichText = false;
        input.textComponent = text;
        var placeholder = UiFactory.CreateText("SearchHint", input.transform, _font, 24, TextAnchor.MiddleLeft,
            new Color(0.78f, 0.82f, 0.84f));
        UiFactory.Stretch(placeholder.rectTransform, 16, 6, -16, -6);
        placeholder.text = "Search packages, items, purpose or mod…";
        input.placeholder = placeholder;
        input.text = search;
        UiFactory.AddFocusTreatment(input);
        SetCancelHandler(input, back);
        void Search() => navigate(caseId, family, input.text.Trim(), 0);
        input.gameObject.AddComponent<BrokerSearchSubmit>().Submit = Search;
        void Return() => navigate(caseId, family, search, page);
        void Cases()
        {
            var query = input.text.Trim();
            ShowBrokerFilter("Choose case",
                new[] { (Id: "", Label: "All cases") }.Concat(library.Cases.Keys.Select(id => (id, CaseContracts.ShortName(id)))).ToArray(),
                caseId, id => navigate(id, "", query, 0), Return);
        }
        void Categories()
        {
            var query = input.text.Trim();
            ShowBrokerFilter("Choose category",
                new[] { (Id: "", Label: "All categories") }.Concat(BrokerLibraryFilter.Lots(library, caseId, "")
                    .Select(l => l.FamilyId).Distinct().OrderBy(CargoFamilies.Label).Select(id => (id, CargoFamilies.Label(id)))).ToArray(),
                family, id => navigate(caseId, id, query, 0), Return);
        }
        controls.Add(BrokerButton("Case", "Case: " + (caseId == "" ? "All" : CaseContracts.ShortName(caseId)),
            -280, Cases, back, 530, false, 248));
        controls.Add(BrokerButton("Category", "Category: " + (family == "" ? "All" : CargoFamilies.Label(family)),
            280, Categories, back, 530, false, 248));
        controls.Add(input);
        controls.Add(BrokerButton("Search", "Search", 430, Search, back, 260, false, 174));

        var width = columns == 3 ? 356f : 544f;
        for (var i = 0; i < lots.Count; i++)
        {
            var lot = lots[i];
            var x = (i % columns - (columns - 1) * 0.5f) * (width + 20);
            var y = 32 - i / columns * 224;
            var button = UiFactory.CreateButton($"Inspect{i}", _brokerPanel!.transform, _font, "", BrokerSecondary);
            UiFactory.Center(button.GetComponent<RectTransform>(), width, 204, x, y);
            SetButton(button, () => inspect(lot));
            SetCancelHandler(button, back);
            BrokerImage($"browser:{i}", 102, 112, -width / 2 + 62, 28, button.transform);
            void Label(string id, string value, int size, float h, float cx, float cy, float w)
            {
                var label = UiFactory.CreateText(id, button.transform, _font, size, TextAnchor.MiddleLeft, Color.white);
                UiFactory.Center(label.rectTransform, w, h, cx, cy);
                UiFactory.SetCardName(label, value);
            }
            Label("Name", BrokerPresentation.Text(lot.DisplayName), 24, 90, 59, 45, width - 140);
            Label("Grade", RewardRarities.GetInfo(lot.Grade).DisplayName, 22, 30, 59, -22, width - 140);
            Label("Preview", BrokerPresentation.ContentsPreview(lot).Replace("\n", " • "), 22, 54, 0, -68, width - 24);
            var edge = UiFactory.CreateImage("GradeEdge", button.transform, UiFactory.RarityColor(lot.Grade));
            UiFactory.Center(edge.rectTransform, width, 4, 0, -100);
            controls.Add(button);
        }
        if (lots.Count == 0) BrokerLabel("Empty", "No packages match. Try a different search or filter.", 28, 1100, 100, 0, 0);
        BrokerLabel("Page", $"{total} package{(total == 1 ? "" : "s")} • Page {page + 1} of {maxPage + 1} • Select a card for full contents", 22, 1120, 38, 0, -322);
        var previous = BrokerButton("Previous", "Previous", -380, () => navigate(caseId, family, search, page - 1), back, 300);
        previous.interactable = page > 0;
        controls.Add(previous);
        var next = BrokerButton("Next", "Next", 0, () => navigate(caseId, family, search, page + 1), back, 300);
        next.interactable = page < maxPage;
        controls.Add(next);
        controls.Add(BrokerButton("Back", "Back to Broker", 380, back, back, 300));
        LinkBrokerControls(back, controls.Where(b => b.interactable).ToArray());
        // Arrow keys edit text; Tab still moves through every control.
        LinkSelf(input);
        var resize = _brokerPanel!.AddComponent<BrokerBrowserResize>();
        resize.Columns = columns;
        resize.Refresh = () =>
        {
            var newSize = RewardBrowserColumns * 2;
            navigate(caseId, family, search, page * columns * 2 / newSize);
        };
        Select(controls[0]);
        return true;
    }

    private void AddDecisionClose(Action close)
    {
        _decisionClose = UiFactory.CreateButton("SaveAndClose", _decisionPanel.transform, _font,
            "SAVE & CLOSE", new Color(0.16f, 0.20f, 0.22f));
        UiFactory.Center(_decisionClose.GetComponent<RectTransform>(), 240f, 48f, 420f, 350f);
        SetButton(_decisionClose, close);
        LinkHorizontal(_secureButton, _relayButton, _decisionClose);
        LinkDecisionDetails();
        foreach (var button in new[] { _secureButton, _relayButton, _decisionClose, _decisionDetailsButton })
            if (button != null) SetCancelHandler(button, close);
    }

    private void BrokerPackage(ManifestLotSnapshot lot)
    {
        BrokerImage($"lot:{lot.Fingerprint}", 340, 230, -380, 140);
        BrokerLabel("RewardGrade", RewardRarities.GetInfo(lot.Grade).DisplayName, 28, 340, 48, -380, -8, TextAnchor.MiddleCenter);
        var gradeEdge = UiFactory.CreateImage("RewardGradeEdge", _brokerPanel!.transform, UiFactory.RarityColor(lot.Grade));
        UiFactory.Center(gradeEdge.rectTransform, 340, 6, -380, -43);
        var name = BrokerLabel("PackageName", "", 32, 700, 82, 190, 236);
        UiFactory.SetCardName(name, BrokerPresentation.Text(lot.DisplayName));
        var purpose = BrokerLabel("Purpose", "", 24, 700, 44, 190, 172);
        UiFactory.SetCardName(purpose, BrokerPresentation.Text(lot.Purpose));
        BrokerLabel("Value", BrokerPresentation.Value(lot), 24, 700, 68, 190, 111);
        var body = BuildScrollableText("PackageContents", _brokerPanel!.transform, _font, 26, 700, 175, 190, -10, Color.white);
        body.Text.text = BrokerPresentation.FullContents(lot) +
            (lot.ProviderId == CashPayouts.Provider ? "" : "\n\n" + BrokerPresentation.ResaleBasis);
    }

    private void BrokerFavor(ManifestSnapshot snapshot)
    {
        if (snapshot.CaseTemplateId == CaseContracts.CashCache) return;
        for (var i = 0; i < snapshot.BrokerFavorMaximum; i++)
        {
            var segment = UiFactory.CreateImage($"Favor{i}", _brokerPanel!.transform,
                i < snapshot.BrokerFavor ? new Color(0.55f, 0.7f, 0.65f) : new Color(0.17f, 0.21f, 0.24f));
            UiFactory.Center(segment.rectTransform, 46, 12, -514 + i * 56, -305);
        }
        BrokerLabel("FavorLabel", $"FAVOR {snapshot.BrokerFavor}/{snapshot.BrokerFavorMaximum} • " +
            (snapshot.BrokerFavor == snapshot.BrokerFavorMaximum
                ? "Guaranteed upgrade on your next eligible Relay; still costs a key."
                : "Relay losses charge a guaranteed upgrade; progress persists."), 22, 900, 58, 100, -304);
    }

    private bool ShowBrokerOffer(ManifestSnapshot snapshot, Action keep, Action discard, Action close)
    {
        if (!snapshot.AvailableActions.CanLock) throw new InvalidOperationException("No safe offer decision was published.");
        if (!BeginBrokerPanel($"Offer {snapshot.CurrentOrdinal} of 3", BrokerPresentation.CaseName(snapshot.CaseTemplateId))) return false;
        BrokerPackage(snapshot.CurrentLot!);
        BrokerLabel("Decision", snapshot.AvailableActions.CanBurn
            ? "Choose this package, or permanently discard it to reveal the next offer.\nNext: Send to Messenger or Relay. No extra key for delivery."
            : snapshot.CurrentOrdinal >= 3
                ? "Final offer — choose this package to continue to Messenger or Relay.\nNo extra key for delivery."
                : "The next offer is unavailable. This package is still safe to choose.\nNo extra key for Messenger delivery.", 26, 1120, 120, 0, -191);
        BrokerFavor(snapshot);
        var accept = BrokerButton("Keep", "Choose This Package", -440, keep, close, 280, true);
        var burn = BrokerButton("Discard", "Discard & Reveal Next", -105, discard, close, 360);
        burn.interactable = snapshot.AvailableActions.CanBurn;
        if (!burn.interactable) burn.GetComponentInChildren<Text>().text = snapshot.CurrentOrdinal >= 3
            ? "Final offer — no discard" : "Discard unavailable";
        var details = BrokerButton("Details", "Details", 180,
            () => ShowBrokerDetails("Package details", BrokerPresentation.PackageDetails(snapshot.CurrentLot!),
                () => ShowBrokerOffer(snapshot, keep, discard, close)), close, 180);
        var save = BrokerButton("Save", "Save & Close", 430, close, close, 270);
        LinkBrokerControls(close, new[] { accept, burn, details, save }.Where(b => b.interactable).ToArray());
        Select(accept);
        return true;
    }

    private bool ShowBrokerEntitlement(ManifestSnapshot snapshot, bool retry, Action claim, Action relay, Action close)
    {
        if (!snapshot.AvailableActions.CanClaim) throw new InvalidOperationException("No safe claim was published.");
        var cash = snapshot.CaseTemplateId == CaseContracts.CashCache;
        if (!BeginBrokerPanel(retry ? "Delivery pending" : cash ? "Your cash payout" : "Your package",
                "Mechanic will send attachments • Collect items at your own pace")) return false;
        BrokerPackage(snapshot.CurrentLot!);
        BrokerLabel("DecisionTerms", cash ? "Send the exact payout to Mechanic's Messenger thread. No discard or Relay." :
            BrokerPresentation.RelayEssentials(snapshot) + "\nDelivery is free. Nothing goes directly into your stash.", 24, 1120, 150, 0, -193);
        BrokerFavor(snapshot);
        var accept = BrokerButton("Secure", retry ? "Retry Delivery" : "Send to Messenger", -440, claim, close, 280, true);
        accept.GetComponentInChildren<Text>().fontSize = 22;
        var buttons = new List<Button> { accept };
        if (snapshot.AvailableActions.CanRelay)
        {
            var risk = BrokerButton("Risk", "Risk on Relay — 1 Key", -105, () => { }, close, 360);
            risk.GetComponentInChildren<Text>().fontSize = 22;
            risk.onClick.RemoveAllListeners();
            _brokerHold = risk.gameObject.AddComponent<RelayHoldButton>();
            _brokerHold.Configure(true, "Risk on Relay — 1 Key", relay);
            buttons.Add(risk);
        }
        buttons.Add(BrokerButton("Details", "Details", 180,
            () => ShowBrokerDetails("Package & Relay details", BrokerPresentation.PackageDetails(snapshot.CurrentLot!) +
                "\n\nDelivery is through Mechanic in Messenger. Collect attachments individually as space allows. " +
                "Remaining attachments last 10 years; deleting the message discards them. " +
                "Save & Close leaves an unsent prize pending; it does not reroll it.\n\n" +
                BuildRelayDisclosure(snapshot), () => ShowBrokerEntitlement(snapshot, retry, claim, relay, close)), close, 180));
        buttons.Add(BrokerButton("Save", "Save & Close", 430, close, close, 270));
        LinkBrokerControls(close, buttons.ToArray());
        Select(accept);
        return true;
    }
}
