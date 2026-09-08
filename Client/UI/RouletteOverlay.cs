using System.Globalization;
using ContrabandCases.Client.Audio;
using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ContrabandCases.Client.UI;

internal sealed partial class RouletteOverlay : IDisposable
{
    private const string RootName = "ContrabandCasesRoulette";
    public const int RevealTileCount = 36;
    public const int LandingIndex = 29;
    public const float TileWidth = 260f;
    public const float TileHeight = 300f;
    public const float TileSpacing = 12f;
    public const float ViewportWidth = 1200f;
    private const float ReferenceWidth = 1920f;
    private const float ReferenceHeight = 1080f;
    private const float MaximumPanelWidth = 1380f;
    private const float MaximumPanelHeight = 900f;
    private const float SafeMargin = 24f;

    private readonly string _ownedRootName = $"{RootName}-{Guid.NewGuid():N}";
    private readonly Action<string>? _diagnostic;
    private readonly Func<float> _effectsVolume;
    private readonly Func<bool> _reducedMotion;
    private readonly List<RewardTileView> _pool = [];
    private readonly List<RewardTileView> _leased = [];
    private readonly List<CatalogTileView> _catalogPool = [];
    private readonly List<CatalogTileView> _catalogLeased = [];
    private readonly List<GameObject> _catalogHeaders = [];
    private OverlayTree? _tree;
    private GameObject? _previousSelection;
    private string? _displayedResultRewardId;
    private string? _displayedDecisionRewardId;
    private ManifestLotSnapshot? _manifestLot;
    private RewardArtworkBinding? _artwork;
    private Button? _galleryNextButton;
    private Button? _decisionDetailsButton;
    private RelayHoldButton? _relayHold;
    private bool _runActive;
    private bool _disposed;
    private readonly TickSoundPlayer _tickPlayer;
    private readonly TensionTonePlayer _tensionPlayer;
    private RectTransform? _confettiLayer;
    private readonly List<ConfettiPiece> _confettiPieces = [];
    private readonly Dictionary<string, Image> _contentsImages = new(StringComparer.Ordinal);
    private GameObject? _contentsRow;

    public RouletteOverlay(Action<string>? diagnostic = null, Func<float>? effectsVolume = null, Func<bool>? reducedMotion = null,
        Action<string>? audioDiagnostic = null)
    {
        _diagnostic = diagnostic;
        _effectsVolume = effectsVolume ?? (() => 0.7f);
        _reducedMotion = reducedMotion ?? (() => false);
        _tickPlayer = new TickSoundPlayer(audioDiagnostic);
        _tensionPlayer = new TensionTonePlayer(audioDiagnostic);
    }

    private CanvasGroup _canvasGroup => RequireLiveTree().CanvasGroup;
    private Font _font => RequireLiveTree().Font;
    private GameObject _confirmationPanel => RequireLiveTree().ConfirmationPanel;
    private Text _catalogText => RequireLiveTree().CatalogText;
    private RectTransform _catalogGridContent => RequireLiveTree().CatalogGridContent;
    private ScrollRect _confirmationScroll => RequireLiveTree().ConfirmationScroll;
    private Text _confirmationNote => RequireLiveTree().ConfirmationNote;
    private Button _confirmationDetailsButton => RequireLiveTree().ConfirmationDetailsButton;
    private Button _confirmButton => RequireLiveTree().ConfirmButton;
    private Button _cancelButton => RequireLiveTree().CancelButton;
    private GameObject _revealPanel => RequireLiveTree().RevealPanel;
    private GameObject _stripViewport => RequireLiveTree().StripViewport;
    private Text _revealHeader => RequireLiveTree().RevealHeader;
    private Text _revealFooter => RequireLiveTree().RevealFooter;
    private RectTransform _stripContent => RequireLiveTree().StripContent;
    private Button _skipButton => RequireLiveTree().SkipButton;
    private GameObject _resultPanel => RequireLiveTree().ResultPanel;
    private Text _resultTitle => RequireLiveTree().ResultTitle;
    private Text _resultText => RequireLiveTree().ResultText;
    private Image _resultImage => RequireLiveTree().ResultImage;
    private Button _resultCloseButton => RequireLiveTree().ResultCloseButton;
    private GameObject _decisionPanel => RequireLiveTree().DecisionPanel;
    private Text _decisionTitle => RequireLiveTree().DecisionTitle;
    private Text _decisionReward => RequireLiveTree().DecisionReward;
    private Text _decisionOdds => RequireLiveTree().DecisionOdds;
    private Text _decisionCandidates => RequireLiveTree().DecisionCandidates;
    private ScrollRect _decisionScroll => RequireLiveTree().DecisionScroll;
    private Text _decisionMeter => RequireLiveTree().DecisionMeter;
    private Image _decisionImage => RequireLiveTree().DecisionImage;
    private Button _secureButton => RequireLiveTree().SecureButton;
    private Button _relayButton => RequireLiveTree().RelayButton;
    private GameObject _errorPanel => RequireLiveTree().ErrorPanel;
    private Text _errorTitle => RequireLiveTree().ErrorTitle;
    private Text _errorText => RequireLiveTree().ErrorText;
    private ScrollRect _errorScroll => RequireLiveTree().ErrorScroll;
    private Button _errorCloseButton => RequireLiveTree().ErrorCloseButton;

    public bool ShowConfirmation(
        IReadOnlyList<ValidatedReward> rewards,
        Action confirm,
        Action cancel)
    {
        if (!Activate(allowRebuild: true))
        {
            return false;
        }

        ResetPanels();
        _confirmationPanel.SetActive(true);
        _manifestLot = null;
        _confirmationDetailsButton.gameObject.SetActive(false);
        ShowCatalogGrid(rewards);
        _confirmationNote.text = "Review the published catalog before opening.";
        ResetConfirmationScroll();
        SetButton(_confirmButton, confirm);
        SetButton(_cancelButton, cancel);
        LinkHorizontal(_confirmButton, _cancelButton);
        SetCancelHandler(_confirmButton, CreatePhaseCancelHandler(OpeningPhase.Confirming, cancel));
        SetCancelHandler(_cancelButton, CreatePhaseCancelHandler(OpeningPhase.Confirming, cancel));
        Select(_cancelButton);
        return true;
    }

    public bool ShowManifestConfirmation(ManifestOpeningOddsSnapshot openingOdds, bool catalogChanged,
        Action confirm, Action cancel) => ShowBrokerConfirmation(openingOdds, catalogChanged, confirm, cancel);

    public void ShowPending(IReadOnlyList<ValidatedReward> rewards)
    {
        ResetPanels();
        _revealPanel.SetActive(true);
        _revealHeader.text = "AUTHORIZING SETTLEMENT";
        _revealFooter.text = "The server is validating the case, key, catalog, and inventory space…";
        _skipButton.gameObject.SetActive(false);
        var pendingCancel = CreatePhaseCancelHandler(OpeningPhase.Pending, () => { });
        SetCancelHandler(_confirmButton, pendingCancel);
        SetCancelHandler(_cancelButton, pendingCancel);
        SetCancelHandler(_skipButton, pendingCancel);

        var ids = Enumerable.Range(0, 20)
            .Select(index => rewards[index % rewards.Count].Id)
            .ToArray();
        ConfigureTiles(ids, rewards);
        SetStripX(0f);
    }

    public bool ShowManifestPending(string title, string detail, bool allowRebuild)
    {
        if (!Activate(allowRebuild))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _revealPanel.SetActive(true);
        _stripViewport.SetActive(false);
        _revealHeader.text = title;
        _revealFooter.text = detail;
        _skipButton.gameObject.SetActive(false);
        return true;
    }

    public void AdvancePending(float unscaledDeltaTime)
    {
        var x = _stripContent.anchoredPosition.x - 165f * unscaledDeltaTime;
        var stride = TileWidth + TileSpacing;
        if (x <= -stride)
        {
            x += stride;
        }

        SetStripX(x);
    }

    public void ShowReveal(
        IReadOnlyList<string> rewardIds,
        IReadOnlyList<ValidatedReward> rewards,
        Action skip)
    {
        ResetPanels();
        _revealPanel.SetActive(true);
        _revealHeader.text = "SETTLEMENT COMMITTED — REVEALING REWARD";
        _revealFooter.text = "Skip unlocks after 1.2 seconds. Closing the reveal cannot change the award.";
        ConfigureTiles(rewardIds, rewards);
        SetStripX(0f);
        _skipButton.gameObject.SetActive(true);
        SetButton(_skipButton, skip);
        _skipButton.interactable = false;
        SetCancelHandler(_skipButton, CreatePhaseCancelHandler(OpeningPhase.Revealing, skip));
    }

    public bool ShowManifestReveal(ManifestRevealPresentation reveal, Action skip)
    {
        if (reveal is null)
        {
            throw new ArgumentNullException(nameof(reveal));
        }
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        _revealPanel.SetActive(true);
        _revealHeader.text = reveal.Header;
        _revealFooter.text =
            "Pointers mark your reward • Decorative reel; card frequency is not the published odds";
        ConfigureManifestTiles(reveal.Motion.Strip, reveal.Tiles, reveal.Motion.LandingIndex);
        SetStripX((float)(reveal.Motion.UsesScrolling ? reveal.Motion.StartX : reveal.Motion.FinalX));
        _skipButton.gameObject.SetActive(true);
        SetButton(_skipButton, skip);
        _skipButton.interactable = false;
        SetCancelHandler(_skipButton, skip);
        return true;
    }

    public bool ShowCosmeticSelfTest(
        IReadOnlyList<string> rewardIds,
        IReadOnlyList<ValidatedReward> rewards,
        Action skip)
    {
        if (!Activate(allowRebuild: true))
        {
            return false;
        }

        ResetPanels();
        _revealPanel.SetActive(true);
        _revealHeader.text = "COSMETIC SELF-TEST — NO ITEMS OR ODDS USED";
        _revealFooter.text = "Local presentation preview only • no server request • no inventory or profile changes";
        ConfigureTiles(rewardIds, rewards);
        SetStripX(0f);
        _skipButton.gameObject.SetActive(true);
        SetButton(_skipButton, skip);
        _skipButton.interactable = true;
        SetCancelHandler(_skipButton, skip);
        Select(_skipButton);
        return true;
    }

    public void SetStripX(float x)
    {
        var position = _stripContent.anchoredPosition;
        position.x = x;
        _stripContent.anchoredPosition = position;
    }

    public void SetSkipEnabled(bool enabled)
    {
        var becameEnabled = OpeningPresentationGuard.ShouldFocusSkip(
            _skipButton.interactable,
            enabled);
        _skipButton.interactable = enabled;
        if (becameEnabled)
        {
            Select(_skipButton);
        }
    }

    public void SetPresentationAlpha(float alpha) =>
        _canvasGroup.alpha = Mathf.Clamp01(alpha);

    public void BindSprite(string rewardId, Sprite? sprite)
    {
        BindBrokerSprite(rewardId, sprite);
        if (sprite != null && _decisionPanel.activeSelf && _contentsImages.TryGetValue(rewardId, out var contentImage) && contentImage != null)
        {
            contentImage.sprite = sprite;
        }
        foreach (var tile in _leased.Where(tile =>
                     string.Equals(tile.RewardId, rewardId, StringComparison.Ordinal)))
        {
            tile.SetSprite(sprite);
        }
    }

    /// Pushes a resolved (or definitively unavailable) sprite onto every
    /// currently-leased catalog-grid tile bound to this reward. Safe to
    /// call for a reward that has no leased tile right now (the
    /// confirmation screen may have already closed, or never showed the
    /// legacy catalog grid at all) -- it is then simply a no-op, mirroring
    /// BindSprite's tolerance of a superseded/closed presentation.
    public void BindCatalogSprite(string rewardId, Sprite? sprite)
    {
        _brokerSpriteCache[rewardId] = sprite;
        BindBrokerSprite(rewardId, sprite);
        foreach (var tile in _catalogLeased.Where(tile =>
                     string.Equals(tile.RewardId, rewardId, StringComparison.Ordinal)))
        {
            tile.SetSprite(sprite);
        }
    }

    public bool ShowResult(ValidatedReward reward, Sprite? sprite, Action close)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _resultPanel.SetActive(true);
        _resultTitle.text = "REWARD SECURED";
        _displayedResultRewardId = reward.Id;
        _artwork = new RewardArtworkBinding(reward.Id, reward.Rarity);
        var rarity = RewardRarities.GetInfo(reward.Rarity);
        _resultText.text =
            $"<b>{reward.DisplayName}</b>\n" +
            $"<color=#{UiFactory.Hex(UiFactory.RarityColor(reward.Rarity))}>●</color> {rarity.DisplayName}\n\n" +
            "The reward is already committed to your stash or sorting table.";
        _resultImage.sprite = LootArtwork.Seal(reward.Rarity);
        _resultImage.color = Color.white;
        SetResultSprite(reward.Id, sprite);
        SetButton(_resultCloseButton, close);
        SetCancelHandler(_resultCloseButton, CreatePhaseCancelHandler(OpeningPhase.Result, close));
        Select(_resultCloseButton);
        return true;
    }

    public bool ShowRelayStatusLoading(
        ValidatedReward reward,
        Sprite? sprite,
        Action secure)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _decisionPanel.SetActive(true);
        _displayedDecisionRewardId = reward.Id;
        _decisionTitle.text = "VERIFYING RELAY STATUS";
        _artwork = new RewardArtworkBinding(reward.Id, reward.Rarity);
        _decisionReward.text = $"<b>{reward.DisplayName}</b>\nThe reward is committed. Loading the authoritative risk ladder…";
        _decisionOdds.text = "No Relay action is available until the server state is verified.";
        _decisionCandidates.text = string.Empty;
        _decisionMeter.text = "SECURE REWARD remains the safe default.";
        SetDecisionSprite(sprite);
        ResetDecisionScroll();
        SetButton(_secureButton, secure);
        _secureButton.GetComponentInChildren<Text>().text = "SECURE REWARD";
        _relayButton.gameObject.SetActive(true);
        DisableRelayHold("RELAY UNAVAILABLE");
        LinkHorizontal(_secureButton, _relayButton);
        SetCancelHandler(_secureButton, secure);
        SetCancelHandler(_relayButton, secure);
        Select(_secureButton);
        return true;
    }

    public bool ShowRelayDecision(
        ValidatedReward reward,
        Sprite? sprite,
        RelaySnapshot snapshot,
        RelayDecisionAvailability availability,
        Action secure,
        Action relay)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        var status = snapshot.Status;
        ResetPanels();
        ReleaseTiles();
        _decisionPanel.SetActive(true);
        _displayedDecisionRewardId = reward.Id;
        _artwork = new RewardArtworkBinding(reward.Id, reward.Rarity);
        var rarity = RewardRarities.GetInfo(reward.Rarity);
        SetDecisionSprite(sprite);
        SetButton(_secureButton, secure);
        _secureButton.GetComponentInChildren<Text>().text = "SECURE REWARD";

        if (!status.RelayEligible)
        {
            _decisionTitle.text = "RELAY COMPLETE — TERMINAL REWARD";
            _decisionReward.text =
                $"<b>{reward.DisplayName}</b>  •  {rarity.DisplayName}\n" +
                "This reward cannot enter another Relay. Secure keeps the complete reward.";
            _decisionOdds.text = "<b>NO RELAY ODDS APPLY TO THIS TERMINAL REWARD</b>";
            _decisionCandidates.text = BuildCandidateText(snapshot, reward.Rarity);
            _decisionMeter.text =
                $"RECOVERY {status.RecoveryMeter}/{status.RecoveryMeterMaximum}  •  no key or stake is required";
            ResetDecisionScroll();
            _relayButton.gameObject.SetActive(false);
            LinkSelf(_secureButton);
            SetCancelHandler(_secureButton, secure);
            Select(_secureButton);
            return true;
        }

        var stage = snapshot.PublishedLadder.Single(entry => entry.Stage == status.Stage);
        _decisionTitle.text = $"RELAY DECISION — STAGE {status.Stage}";
        _decisionReward.text =
            $"<b>{reward.DisplayName}</b>  •  {rarity.DisplayName}\n" +
            "SECURE keeps this complete reward. Relay risks it and one BR-12 Relay Key.";
        _decisionOdds.text = status.GuaranteedUpgradeReady
            ? "<b>RECOVERY GUARANTEE ACTIVE — THIS ELIGIBLE RELAY IS A RARITY UPGRADE</b>"
            : $"RARITY UPGRADE {stage.UpgradePercent}%   •   SAME-RARITY SIDEGRADE {stage.SidegradePercent}%   •   CONFISCATED {stage.ConfiscatePercent}%";
        _decisionCandidates.text = BuildCandidateText(snapshot, reward.Rarity);
        _decisionMeter.text =
            $"RECOVERY {status.RecoveryMeter}/{status.RecoveryMeterMaximum}" +
            (status.GuaranteedUpgradeReady
                ? "  •  guarantee resets after the upgrade"
                : "  •  confiscation charges the meter by stage depth");
        ResetDecisionScroll();
        _relayButton.gameObject.SetActive(true);
        var relayEnabled = status.RelayEligible && availability.RelayEnabled && !status.SettlementPending;
        ConfigureRelayHold(relayEnabled, relay);
        if (!availability.RelayEnabled)
        {
            _relayButton.GetComponentInChildren<Text>().text = availability.DisabledReason ?? "RELAY UNAVAILABLE";
            _decisionMeter.text += $"\n<color=#E8A076>{availability.DisabledReason}</color>";
        }
        LinkHorizontal(_secureButton, _relayButton);
        SetCancelHandler(_secureButton, secure);
        SetCancelHandler(_relayButton, secure);
        Select(_secureButton);
        return true;
    }

    public bool ShowDossier(string text, Action close, Action? resume = null) =>
        ShowBrokerDossier(text, close, resume);

    public bool ShowGalleryPreview(ManifestSnapshot snapshot, ManifestLotSnapshot lot, GalleryState state,
        int ordinal, int total, Action next, Action close)
    {
        var terminal = snapshot.Phase is ManifestPhase.Granted or ManifestPhase.Confiscated;
        var shown = terminal ? ShowManifestTerminal(snapshot, close, snapshot.CurrentLot ?? lot) :
            snapshot.AvailableActions.CanLock ? ShowManifestOffer(snapshot, next, next, close) :
            ShowManifestEntitlement(snapshot, false, next, next, close);
        if (!shown) return false;
        var label = $"PREVIEW ONLY • {state} • LOT {ordinal}/{total}";
        if (terminal)
        {
            _resultTitle.text = label;
            _resultText.text = $"<b>{lot.DisplayName.Replace("<", "‹").Replace(">", "›")}</b>\n" +
                (snapshot.Phase == ManifestPhase.Confiscated ? "Confiscated layout" : "Secured layout") +
                "\n\nCatalog preview only. Nothing was spent, lost or granted.";
            _galleryNextButton = UiFactory.CreateButton("NextLot", _resultPanel.transform, _font, "NEXT LOT", new Color(0.20f, 0.26f, 0.28f, 1f));
            UiFactory.Center(_galleryNextButton.GetComponent<RectTransform>(), 230f, 60f, -140f, -255f);
            UiFactory.Center(_resultCloseButton.GetComponent<RectTransform>(), 230f, 60f, 140f, -255f);
            SetButton(_galleryNextButton, next);
            SetCancelHandler(_galleryNextButton, close);
            LinkHorizontal(_galleryNextButton, _resultCloseButton);
            Select(_galleryNextButton);
        }
        else
        {
            _brokerPanel!.transform.Find("Heading").GetComponent<Text>().text = label;
            _brokerPanel.transform.Find("Subheading").GetComponent<Text>().text =
                "TESTING • SAMPLE STATE • NO ITEMS SPENT OR GRANTED";
            var save = _brokerPanel.transform.Find("Save").GetComponent<Button>();
            save.GetComponentInChildren<Text>().text = "CLOSE PREVIEW";
        }
        return true;
    }

    public bool ShowManifestOffer(
        ManifestSnapshot snapshot,
        Action lockLot,
        Action burnLot,
        Action? close = null)
    {
        if (close is not null) return ShowBrokerOffer(snapshot, lockLot, burnLot, close);
        if (!Activate(allowRebuild: false))
        {
            return false;
        }
        if (!snapshot.AvailableActions.CanLock)
        {
            throw new InvalidOperationException("The snapshot does not publish a safe offer decision.");
        }

        ResetPanels();
        ReleaseTiles();
        _decisionPanel.SetActive(true);
        _displayedDecisionRewardId = TrackManifestAnchor(snapshot);
        _decisionTitle.text = $"OFFER {snapshot.CurrentOrdinal} OF 3";
        _decisionReward.text = ManifestPresentationPolicy.LotHeading(snapshot);
        _decisionOdds.text = ManifestPresentationPolicy.OfferDecisionPrompt(snapshot);
        _decisionCandidates.text = ManifestPresentationPolicy.LotDetails(snapshot) +
            (snapshot.AvailableActions.CanBurn
                ? string.Empty
                : "\n\n<color=#E8A076>The next persisted offer no longer matches installed content. " +
                  "Lock this lot, or restore the missing reward pack before trying Burn.</color>");
        _decisionMeter.text = BuildSealHistory(snapshot);
        ShowContents(snapshot);
        ResetDecisionScroll();
        _secureButton.GetComponentInChildren<Text>().text = "LOCK THIS LOT";
        _relayButton.gameObject.SetActive(true);
        _relayButton.GetComponentInChildren<Text>().text =
            ManifestPresentationPolicy.OfferDiscardActionLabel(snapshot);
        SetButton(_secureButton, lockLot);
        if (snapshot.AvailableActions.CanBurn)
        {
            SetButton(_relayButton, burnLot);
        }
        else
        {
            _relayButton.onClick.RemoveAllListeners();
            _relayButton.interactable = false;
            _relayButton.GetComponentInChildren<Text>().text = "DISCARD UNAVAILABLE";
        }
        _relayHold?.Clear();
        if (snapshot.AvailableActions.CanBurn)
        {
            LinkHorizontal(_secureButton, _relayButton);
        }
        else
        {
            LinkSelf(_secureButton);
        }
        SetCancelHandler(_secureButton, null);
        SetCancelHandler(_relayButton, null);
        ConfigureManifestDetails(snapshot, entitlement: false);
        if (close is not null) AddDecisionClose(close);
        Select(_secureButton);
        return true;
    }

    public bool ShowManifestPremiumChoice(ManifestSnapshot snapshot, int index, Action<int> browse,
        Action choose, Action close) => ShowPremiumCards(snapshot, index, browse, choose, close);

    public bool ShowManifestEntitlement(
        ManifestSnapshot snapshot,
        bool claimRetry,
        Action claim,
        Action relay,
        Action? close = null,
        int? keyCount = null)
    {
        if (close is not null) return ShowBrokerEntitlement(snapshot, claimRetry, claim, relay, close, keyCount);
        if (!Activate(allowRebuild: false))
        {
            return false;
        }
        if (!snapshot.AvailableActions.CanClaim)
        {
            throw new InvalidOperationException("The snapshot does not publish Claim.");
        }

        ResetPanels();
        ReleaseTiles();
        _decisionPanel.SetActive(true);
        _displayedDecisionRewardId = TrackManifestAnchor(snapshot);
        _decisionTitle.text = claimRetry
            ? "DELIVERY PENDING"
            : snapshot.CaseTemplateId == CaseContracts.CashCache ? "YOUR CASH PAYOUT"
            : snapshot.LatestReceipt?.Outcome == ManifestRelayResult.Sidegrade
                ? "REPLACEMENT READY — RELAY ENDED"
                : "KEEP YOUR REWARD OR RISK IT";
        _decisionReward.text = ManifestPresentationPolicy.LotHeading(snapshot);
        _decisionOdds.text = ManifestPresentationPolicy.RelayOdds(snapshot);
        _decisionCandidates.text =
            ManifestPresentationPolicy.LotDetails(snapshot) + "\n\n" + BuildRelayDisclosure(snapshot);
        _decisionMeter.text =
            $"BROKER FAVOR {snapshot.BrokerFavor}/{snapshot.BrokerFavorMaximum}" +
            (claimRetry
                ? "\n<color=#E8A076>Your reward is saved. Retry delivery to Messenger.</color>"
                : "\nSend this exact prize to Mechanic's Messenger thread, or stake it + 1 key on Relay.");
        if (snapshot.CaseTemplateId == CaseContracts.CashCache)
        {
            _decisionCandidates.text = ManifestPresentationPolicy.LotDetails(snapshot);
            _decisionOdds.text = "ONE COMMITTED PAYOUT — NO ADDITIONAL KEY";
            _decisionMeter.text = claimRetry
                ? "Delivery is pending. Your original payout is saved."
                : "Collect attachments from Mechanic at your own pace. No discard or Relay.";
        }
        ShowContents(snapshot);
        ResetDecisionScroll();
        _secureButton.GetComponentInChildren<Text>().text = claimRetry ? "RETRY DELIVERY" : "SEND TO MESSENGER";
        SetButton(_secureButton, claim);
        _relayButton.gameObject.SetActive(snapshot.AvailableActions.CanRelay);
        if (snapshot.AvailableActions.CanRelay)
        {
            ConfigureHold(true, "RISK IT — 1 KEY", relay);
            LinkHorizontal(_secureButton, _relayButton);
            SetCancelHandler(_relayButton, null);
        }
        else
        {
            _relayHold?.Clear();
            LinkSelf(_secureButton);
        }
        SetCancelHandler(_secureButton, null);
        ConfigureManifestDetails(snapshot, entitlement: true);
        if (close is not null) AddDecisionClose(close);
        Select(_secureButton);
        return true;
    }

    public bool ShowManifestMissingContent(
        ManifestSnapshot snapshot,
        Action keepBlocked,
        Action forfeit)
    {
        if (!snapshot.MissingContentBlocked || !snapshot.AvailableActions.CanForfeit)
        {
            throw new InvalidOperationException("Forfeit may be shown only for a server-published missing-content block.");
        }
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _decisionPanel.SetActive(true);
        _displayedDecisionRewardId = TrackManifestAnchor(snapshot);
        _decisionTitle.text = "REWARD PACK MISSING — MANIFEST BLOCKED";
        _decisionReward.text = ManifestPresentationPolicy.LotHeading(snapshot);
        _decisionOdds.text = "NO LOCK, BURN, CLAIM, OR RELAY ACTION IS SAFE WHILE CONTENT IS MISSING";
        _decisionCandidates.text =
            ManifestPresentationPolicy.LotDetails(snapshot) + "\n\n" +
            "Restore the exact mod/reward pack and reopen this Manifest to continue.\n" +
            "Forfeit permanently clears this Manifest without granting a lot. It cannot be undone.";
        _decisionMeter.text =
            $"BROKER FAVOR {snapshot.BrokerFavor}/{snapshot.BrokerFavorMaximum} • Forfeit does not reroll or grant content.";
        SetDecisionSprite(null);
        ResetDecisionScroll();
        _secureButton.GetComponentInChildren<Text>().text = "KEEP MANIFEST BLOCKED";
        SetButton(_secureButton, keepBlocked);
        _relayButton.gameObject.SetActive(true);
        ConfigureHold(true, "FORFEIT MANIFEST", forfeit);
        LinkHorizontal(_secureButton, _relayButton);
        SetCancelHandler(_secureButton, keepBlocked);
        SetCancelHandler(_relayButton, keepBlocked);
        Select(_secureButton);
        return true;
    }

    public bool ShowManifestTerminal(ManifestSnapshot snapshot, Action close, ManifestLotSnapshot? displayedLot = null)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _resultPanel.SetActive(true);
        var lot = displayedLot ?? _manifestLot;
        _artwork = lot is null ? null : RewardArtworkBinding.ForLot(lot);
        _displayedResultRewardId = _artwork?.AnchorId;
        _resultTitle.text = snapshot.Phase switch
        {
            ManifestPhase.Granted => snapshot.DeliveredToMessenger ? "SENT TO MESSENGER" : "REWARD ALREADY CLAIMED",
            ManifestPhase.Confiscated => "LOT CONFISCATED",
            ManifestPhase.Forfeited => "MANIFEST FORFEITED",
            _ => "MANIFEST SETTLED"
        };
        _resultText.text = snapshot.Phase switch
        {
            ManifestPhase.Granted when snapshot.DeliveredToMessenger =>
                "<b>Mechanic has sent your reward.</b>\n\nOpen Messenger → Mechanic to collect individual attachments. " +
                "Leave the rest until you have space. No extra key is needed.\n\n" +
                "Attachments are held for 10 years; deleting the message discards uncollected items.",
            ManifestPhase.Granted when snapshot.CaseTemplateId == CaseContracts.CashCache =>
                "<b>Your committed payout was collected.</b>\n\nCheck your stash or sorting table.",
            ManifestPhase.Granted =>
                "<b>The exact server-authoritative mixed lot was granted.</b>\n\n" +
                "Check your stash or sorting table for every item.",
            ManifestPhase.Confiscated =>
                "<b>The staked lot and Relay Key were confiscated.</b>\n\n" +
                $"Broker Favor: {snapshot.BrokerFavor}/{snapshot.BrokerFavorMaximum}" +
                BuildReceiptFavor(snapshot),
            ManifestPhase.Forfeited =>
                "The blocked Manifest was permanently cleared. No lot was granted.",
            _ => "The Manifest is settled."
        };
        _resultImage.sprite = LootArtwork.Seal(lot?.Grade ?? snapshot.LatestReceipt?.InputGrade ?? RewardRarity.ScavGrade);
        _resultImage.color = Color.white;
        _resultCloseButton.GetComponentInChildren<Text>().text = "CLOSE";
        SetButton(_resultCloseButton, close);
        SetCancelHandler(_resultCloseButton, close);
        Select(_resultCloseButton);
        return true;
    }

    public void ShowRelayPending(IReadOnlyList<ValidatedReward> rewards, string actionLabel)
    {
        ShowPending(rewards);
        _revealHeader.text = actionLabel;
        _revealFooter.text = "The server owns this transition. Closing the screen cannot alter or reroll it.";
    }

    public void ShowRelayReveal(
        IReadOnlyList<string> rewardIds,
        IReadOnlyList<ValidatedReward> rewards,
        RelayOutcome outcome,
        Action skip)
    {
        ShowReveal(rewardIds, rewards, skip);
        _revealHeader.text = outcome == RelayOutcome.RarityUpgrade
            ? "RARITY UPGRADE COMMITTED — REVEALING"
            : "SAME-RARITY SIDEGRADE COMMITTED — REVEALING";
        _revealFooter.text = "The authoritative Relay result is already applied. The roulette is presentation only.";
    }

    public void ShowConfiscationSuspense(RelayReceipt receipt)
    {
        ResetPanels();
        ReleaseTiles();
        _revealPanel.SetActive(true);
        _stripViewport.SetActive(false);
        _revealHeader.text = "RELAY SIGNAL LOST";
        _revealFooter.text =
            $"Resolving confiscation…  Recovery {receipt.RecoveryMeter}/{receipt.RecoveryMeterMaximum}";
        _skipButton.gameObject.SetActive(false);
    }

    public void SetConfiscationProgress(double progress)
    {
        var clamped = Math.Max(0d, Math.Min(1d, progress));
        var dots = new string('•', Math.Max(1, (int)Math.Ceiling(clamped * 12d)));
        _revealFooter.text = $"SIGNAL INTERCEPT {dots}";
    }

    public bool ShowRelayTerminal(
        ValidatedReward? reward,
        Sprite? sprite,
        RelayReceipt receipt,
        Action close)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        var outcome = RelaySnapshotEnvelope.ParseOutcome(receipt);
        ResetPanels();
        ReleaseTiles();
        _resultPanel.SetActive(true);
        _displayedResultRewardId = reward?.Id;
        _artwork = reward is null ? null : new RewardArtworkBinding(reward.Id, reward.Rarity);
        _resultTitle.text = receipt.DeliveredToMessenger ? "SENT TO MESSENGER" : outcome switch
        {
            RelayOutcome.Secured => "REWARD SECURED",
            RelayOutcome.SameRaritySidegrade => "SIDEGRADE SECURED",
            RelayOutcome.RarityUpgrade => "RELAY COMPLETE",
            RelayOutcome.Confiscated => "CONFISCATED",
            _ => "RELAY COMPLETE"
        };
        if (outcome == RelayOutcome.Confiscated)
        {
            _resultText.text =
                "<b>The staked weapon tree and BR-12 Relay Key were confiscated.</b>\n\n" +
                $"Recovery meter: {receipt.RecoveryMeter}/{receipt.RecoveryMeterMaximum}.";
            _resultImage.sprite = null;
            _resultImage.color = new Color(0.20f, 0.055f, 0.045f, 1f);
        }
        else
        {
            var resolved = reward
                ?? throw new InvalidOperationException("A non-confiscation Relay result requires a verified reward.");
            _resultText.text =
                $"<b>{resolved.DisplayName}</b>\n" +
                (receipt.DeliveredToMessenger
                    ? "Collect your saved prize from Mechanic in Messenger.\n\nItems can be claimed separately as space allows. No new roll occurs."
                    : $"{OutcomeLabel(outcome)}\n\nRecovery meter: {receipt.RecoveryMeter}/{receipt.RecoveryMeterMaximum}.");
            _resultImage.sprite = LootArtwork.Seal(resolved.Rarity);
            _resultImage.color = Color.white;
            SetResultSprite(resolved.Id, sprite);
        }

        _resultCloseButton.GetComponentInChildren<Text>().text = "CLOSE";
        SetButton(_resultCloseButton, close);
        SetCancelHandler(_resultCloseButton, close);
        Select(_resultCloseButton);
        return true;
    }

    public bool ShowCosmeticRelayPreview(
        CosmeticRelayPreviewPlan plan,
        IReadOnlyList<ValidatedReward> rewards,
        Action close)
    {
        if (!Activate(allowRebuild: true))
        {
            return false;
        }

        var current = CatalogGridLayout.TierOrder
            .Select(tier => rewards.FirstOrDefault(reward => reward.Rarity == tier))
            .FirstOrDefault(reward => reward is not null) ?? rewards.First();
        var target = plan.Kind == CosmeticRelayPreviewKind.Upgrade &&
                     RelayRules.CanRelay(current.Rarity, stage: 1)
            ? rewards.FirstOrDefault(reward =>
                reward.Rarity == RelayRules.GetUpgradeRarity(current.Rarity)) ?? current
            : rewards.FirstOrDefault(reward => reward.Rarity == current.Rarity && reward.Id != current.Id) ?? rewards.Last();
        ResetPanels();
        ReleaseTiles();
        _resultPanel.SetActive(true);
        _displayedResultRewardId = null;
        _manifestLot = null;
        _artwork = null;
        _displayedDecisionRewardId = null;
        _resultTitle.text = plan.Kind switch
        {
            CosmeticRelayPreviewKind.Upgrade => "COSMETIC RARITY-UPGRADE PREVIEW",
            CosmeticRelayPreviewKind.Sidegrade => "COSMETIC SIDEGRADE PREVIEW",
            CosmeticRelayPreviewKind.Confiscation => "COSMETIC CONFISCATION PREVIEW",
            _ => "COSMETIC RELAY PREVIEW"
        };
        _resultText.text = plan.Kind == CosmeticRelayPreviewKind.Confiscation
            ? "<b>CONFISCATED</b>\n\nPreview only: no weapon, key, odds, recovery meter, server request, inventory, or profile was touched."
            : $"<b>{target.DisplayName}</b>\n{(plan.Kind == CosmeticRelayPreviewKind.Upgrade ? "RARITY UPGRADE" : "SAME-RARITY SIDEGRADE")}\n\nPreview only: no server request, inventory, or profile change.";
        _resultImage.sprite = null;
        _resultImage.color = plan.Kind == CosmeticRelayPreviewKind.Confiscation
            ? new Color(0.20f, 0.055f, 0.045f, 1f)
            : new Color(0.12f, 0.13f, 0.13f, 1f);
        _resultCloseButton.GetComponentInChildren<Text>().text = "CLOSE PREVIEW";
        SetButton(_resultCloseButton, close);
        SetCancelHandler(_resultCloseButton, close);
        Select(_resultCloseButton);
        return true;
    }

    public bool ShowCosmeticSelfTestResult(ValidatedReward reward, Action close)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _resultPanel.SetActive(true);
        _resultTitle.text = "COSMETIC SELF-TEST COMPLETE";
        _displayedResultRewardId = null;
        var rarity = RewardRarities.GetInfo(reward.Rarity);
        _resultText.text =
            $"<b>{reward.DisplayName}</b>\n" +
            $"<color=#{UiFactory.Hex(UiFactory.RarityColor(reward.Rarity))}>●</color> {rarity.DisplayName}\n\n" +
            "Preview only. No case, key, reward, odds, or profile state was changed.";
        _resultImage.sprite = null;
        _resultImage.color = new Color(0.12f, 0.13f, 0.13f, 1f);
        SetButton(_resultCloseButton, close);
        SetCancelHandler(_resultCloseButton, close);
        Select(_resultCloseButton);
        return true;
    }

    public bool ShowError(string message, Action close)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _errorPanel.SetActive(true);
        _errorTitle.text = "OPENING COULD NOT BE SHOWN";
        _errorText.text = message;
        ResetErrorScroll();
        _errorCloseButton.GetComponentInChildren<Text>().text = "CLOSE";
        SetButton(_errorCloseButton, close);
        SetCancelHandler(_errorCloseButton, CreatePhaseCancelHandler(OpeningPhase.Failed, close));
        Select(_errorCloseButton);
        return true;
    }

    /// <summary>
    /// Same terminal error panel as <see cref="ShowError"/>, titled for the single most common,
    /// expected rejection reason (no BR-12 Relay Key present) instead of the generic fallback title.
    /// Presentation, close handling, and cancel-on-Escape all match <see cref="ShowError"/> exactly;
    /// only the title differs.
    /// </summary>
    public bool ShowKeyRequiredError(string message, Action close)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _errorPanel.SetActive(true);
        _errorTitle.text = "KEY REQUIRED";
        _errorText.text = message;
        ResetErrorScroll();
        _errorCloseButton.GetComponentInChildren<Text>().text = "CLOSE";
        SetButton(_errorCloseButton, close);
        SetCancelHandler(_errorCloseButton, CreatePhaseCancelHandler(OpeningPhase.Failed, close));
        Select(_errorCloseButton);
        return true;
    }

    public bool ShowRelayVerificationError(string message, Action retryVerification)
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _errorPanel.SetActive(true);
        _errorTitle.text = "RELAY VERIFICATION REQUIRED";
        _errorText.text =
            $"{message}\n\nNo additional item action will be sent. Retry reads the authoritative receipt only.";
        ResetErrorScroll();
        _errorCloseButton.GetComponentInChildren<Text>().text = "RETRY VERIFICATION";
        SetButton(_errorCloseButton, retryVerification);
        SetCancelHandler(_errorCloseButton, retryVerification);
        Select(_errorCloseButton);
        return true;
    }

    public bool ShowManifestVerificationError(
        string message,
        string actionLabel,
        Action retryVerification,
        string title = "OPENING NEEDS ATTENTION")
    {
        if (!Activate(allowRebuild: false))
        {
            return false;
        }

        ResetPanels();
        ReleaseTiles();
        _errorPanel.SetActive(true);
        _errorTitle.text = title;
        _errorText.text = message;
        ResetErrorScroll();
        _errorCloseButton.GetComponentInChildren<Text>().text = actionLabel;
        SetButton(_errorCloseButton, retryVerification);
        SetCancelHandler(_errorCloseButton, null);
        Select(_errorCloseButton);
        return true;
    }

    public void EndRun()
    {
        // Audio outlives the UI tree, so stop it even if the tree/run is gone.
        _tensionPlayer.Stop();
        if (_disposed || !_runActive)
        {
            return;
        }

        DisableRunInput();
        ClearRunListeners();
        ReleaseRunTiles();
        RestoreRunSelection();
        DeactivateRunRoot();
    }

    public void DisableRunInput()
    {
        var group = _tree?.CanvasGroup;
        if (group == null)
        {
            return;
        }

        group.blocksRaycasts = false;
        group.interactable = false;
    }

    public void ClearRunListeners()
    {
        _brokerHold?.Clear();
        if (LiveTreeOrNull() is null)
        {
            return;
        }

        ClearButtons();
        SetCancelHandler(_confirmationDetailsButton, null);
        SetCancelHandler(_confirmButton, null);
        SetCancelHandler(_cancelButton, null);
        SetCancelHandler(_skipButton, null);
        SetCancelHandler(_resultCloseButton, null);
        SetCancelHandler(_secureButton, null);
        SetCancelHandler(_relayButton, null);
        SetCancelHandler(_errorCloseButton, null);
        _relayHold?.Clear();
    }

    public void ReleaseRunTiles()
    {
        _brokerSpriteCache.Clear();
        _displayedResultRewardId = null;
        _displayedDecisionRewardId = null;
        _manifestLot = null;
        _artwork = null;
        var tree = LiveTreeOrNull();
        if (tree is not null)
        {
            tree.ResultImage.sprite = null;
            tree.DecisionImage.sprite = null;
        }

        ReleaseTiles(resetStrip: tree is not null);
        ReleaseCatalogTiles();
    }

    public void RestoreRunSelection() => RestoreSelection();

    public void DeactivateRunRoot()
    {
        _tensionPlayer.Stop();
        ResetBrokerPanels();
        var tree = LiveTreeOrNull();
        if (tree is not null)
        {
            tree.CanvasGroup.alpha = 1f;
            ResetPanels();
            tree.Root.SetActive(false);
        }
        else if (_tree?.Root != null) _tree.Root.SetActive(false);

        _runActive = false;
    }

    public void SetResultSprite(string rewardId, Sprite? sprite)
    {
        if ((!_resultPanel.activeSelf && !_decisionPanel.activeSelf) ||
            _artwork?.TryAccept(rewardId, sprite != null) != true) return;
        if (_resultPanel.activeSelf)
        {
            _resultImage.sprite = sprite;
            _resultImage.color = Color.white;
        }
        if (_decisionPanel.activeSelf)
        {
            _decisionImage.sprite = sprite;
            _decisionImage.color = Color.white;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EndRun();
        _disposed = true;
        _tickPlayer.Dispose();
        _tensionPlayer.Dispose();
        DiscardOwnedTree("disposed");
    }

    private bool Activate(bool allowRebuild)
    {
        var eventSystem = EventSystem.current;
        var decision = OverlayTreeLifecycle.DecideActivation(
                _disposed,
                eventSystem != null,
                _tree?.Root != null,
                HasEssentialSubtree(_tree));
        if (decision == OverlayActivationDecision.Reject)
        {
            return false;
        }

        if (decision == OverlayActivationDecision.Rebuild)
        {
            if (!allowRebuild || _runActive)
            {
                Diagnostic("Overlay tree became unavailable during an active presentation; it will be rebuilt before the next run.");
                return false;
            }

            DiscardOwnedTree("native Unity tree was missing or incomplete");
            BuildOwnedTree();
        }

        var tree = RequireLiveTree();
        ApplyResponsiveScale(tree.Scaler);
        if (!tree.Root.activeSelf)
        {
            _previousSelection = eventSystem!.currentSelectedGameObject;
            tree.Root.SetActive(true);
        }

        _runActive = true;
        tree.CanvasGroup.alpha = 1f;
        tree.CanvasGroup.interactable = true;
        tree.CanvasGroup.blocksRaycasts = true;
        return true;
    }

    private void ConfigureTiles(
        IReadOnlyList<string> rewardIds,
        IReadOnlyList<ValidatedReward> rewards)
    {
        ReleaseTiles();
        var byId = rewards.ToDictionary(reward => reward.Id, StringComparer.Ordinal);
        var stride = TileWidth + TileSpacing;
        for (var index = 0; index < rewardIds.Count; index++)
        {
            if (!byId.TryGetValue(rewardIds[index], out var reward))
            {
                throw new InvalidOperationException($"Cosmetic strip contains unknown reward '{rewardIds[index]}'.");
            }

            var tile = LeaseTile();
            tile.Root.anchorMin = new Vector2(0f, 0.5f);
            tile.Root.anchorMax = new Vector2(0f, 0.5f);
            tile.Root.pivot = new Vector2(0f, 0.5f);
            tile.Root.sizeDelta = new Vector2(TileWidth, TileHeight);
            tile.Root.anchoredPosition = new Vector2(index * stride, 0f);
            tile.Bind(reward, sprite: null);
        }

        _stripContent.sizeDelta = new Vector2(
            rewardIds.Count * stride - TileSpacing,
            TileHeight);
    }

    private void ConfigureManifestTiles(
        IReadOnlyList<string> tileIds,
        IReadOnlyDictionary<string, ManifestTilePresentation> tiles,
        int landingIndex)
    {
        if (landingIndex < 0 || landingIndex >= tileIds.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(landingIndex));
        }

        ReleaseTiles();
        var stride = TileWidth + TileSpacing;
        for (var index = 0; index < tileIds.Count; index++)
        {
            if (!tiles.TryGetValue(tileIds[index], out var presentation))
            {
                throw new InvalidOperationException(
                    $"Manifest strip contains unknown disclosed tile '{tileIds[index]}'.");
            }

            var tile = LeaseTile();
            tile.Root.anchorMin = new Vector2(0f, 0.5f);
            tile.Root.anchorMax = new Vector2(0f, 0.5f);
            tile.Root.pivot = new Vector2(0f, 0.5f);
            tile.Root.sizeDelta = new Vector2(TileWidth, TileHeight);
            tile.Root.anchoredPosition = new Vector2(index * stride, 0f);
            tile.Bind(presentation);
        }

        _stripContent.sizeDelta = new Vector2(
            tileIds.Count * stride - TileSpacing,
            TileHeight);
    }

    /// Reveals the landing tile'''s selection outline. Called only once the
    /// spin animation has actually settled on its final resting position, so
    /// the winner is never visible before the reel lands on it.
    public void RevealLandingSelection(int landingIndex)
    {
        if (landingIndex < 0 || landingIndex >= _leased.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(landingIndex));
        }

        _leased[landingIndex].SetSelected(true);
    }

    /// Drives the instant-of-reveal scale-pulse/flash on the landing tile.
    /// Purely cosmetic -- never touches which tile is the committed winner.
    public void SetLandingPulse(int landingIndex, float scale, float flashIntensity)
    {
        if (landingIndex < 0 || landingIndex >= _leased.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(landingIndex));
        }

        _leased[landingIndex].SetPulse(_reducedMotion() ? 1f : scale, _reducedMotion() ? 0f : flashIntensity);
    }

    /// Plays one procedural tick. See TickSoundPlayer -- best-effort, never
    /// throws, and is silently a no-op if Unity's audio pipeline is
    /// unavailable.
    public void PlayTick(float pitch, float volume) => _tickPlayer.Play(pitch, volume * _effectsVolume());

    public void PlayLatch() => _tickPlayer.PlayLatch(_effectsVolume());

    public void AdvanceSpinAudio(float stripX, float progress, ref int lastTickIndex)
    {
        if (_reducedMotion())
        {
            StopTension();
            return;
        }
        var frame = RouletteAnimationMath.SpinAudio(stripX, progress, TileWidth + TileSpacing, LandingIndex);
        SetTensionProgress(progress);
        if (frame.TickIndex == lastTickIndex) return;
        PlayTick(frame.Pitch, frame.Volume);
        lastTickIndex = frame.TickIndex;
    }

    public void PlayOutcome(ManifestRelayResult? outcome, RewardRarity rarity) =>
        _tickPlayer.PlayOutcome(outcome, rarity, _effectsVolume());

    /// Drives the rising tension drone from the reveal's own 0..1 spin
    /// progress. See TensionTonePlayer -- best-effort, never throws, and
    /// is silently a no-op if Unity's audio pipeline is unavailable.
    public void SetTensionProgress(float progress) => _tensionPlayer.SetProgress(progress, _effectsVolume());

    /// Cuts the tension drone immediately. Safe to call even if it was
    /// never started.
    public void StopTension() => _tensionPlayer.Stop();

    /// Dims every tile except the landing tile, so the winner reads as
    /// spotlit against a darkened strip once it settles. Purely
    /// cosmetic -- never touches which tile is the committed winner.
    public void SetSpotlight(int landingIndex, float dimAmount)
    {
        if (landingIndex < 0 || landingIndex >= _leased.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(landingIndex));
        }

        var clamped = _reducedMotion() ? 0f : Mathf.Clamp01(dimAmount);
        for (var index = 0; index < _leased.Count; index++)
        {
            var tile = _leased[index];
            if (!tile.IsAvailable)
            {
                continue;
            }

            tile.SetDim(index == landingIndex ? 0f : clamped);
        }
    }

    private const float ShimmerCycleSeconds = 1.1f;

    /// Drives the idle shimmer on any currently-visible BlackLabel tile
    /// while the reel is still spinning -- a continuous, gentle pulsing
    /// glow, independent of which tile eventually wins. Seal placeholder
    /// tiles are never graded BlackLabel (see
    /// ManifestPresentationFlow.CreateReveal) so they never qualify.
    /// <param name="elapsedSeconds">The reveal's own running clock, so
    /// every qualifying tile pulses in sync rather than drifting with
    /// frame rate.</param>
    public void AdvanceShimmer(float elapsedSeconds)
    {
        if (_reducedMotion())
        {
            ResetShimmer();
            return;
        }
        if (_leased.Count == 0)
        {
            return;
        }

        var wave = 0.5f + 0.5f * Mathf.Sin(elapsedSeconds * (Mathf.PI * 2f / ShimmerCycleSeconds));
        // Only visible cards need per-frame graphic updates. Off-screen shimmer is reset at landing.
        var stride = TileWidth + TileSpacing;
        var stripX = _stripContent.anchoredPosition.x;
        var first = Math.Max(0, (int)Math.Floor(-stripX / stride));
        var last = Math.Min(_leased.Count - 1, (int)Math.Floor((ViewportWidth - stripX) / stride));
        for (var index = first; index <= last; index++)
        {
            var tile = _leased[index];
            if (tile.IsAvailable && RarityCelebrationTuning.ShouldShimmer(tile.Grade)) tile.SetShimmer(wave);
        }
    }

    /// Clears the idle shimmer from every currently-leased tile. Called
    /// once the reel lands, so the landing-pulse/spotlight treatment
    /// starts from a clean slate.
    public void ResetShimmer()
    {
        foreach (var tile in _leased)
        {
            if (tile.IsAvailable)
            {
                tile.SetShimmer(0f);
            }
        }
    }

    private readonly struct ConfettiPiece
    {
        public ConfettiPiece(RectTransform rect, Vector2 velocity, float angularVelocityDegrees)
        {
            Rect = rect;
            Velocity = velocity;
            AngularVelocityDegrees = angularVelocityDegrees;
        }

        public RectTransform Rect { get; }

        public Vector2 Velocity { get; }

        public float AngularVelocityDegrees { get; }
    }

    private const int ConfettiPieceCount = 28;

    private static readonly Color[] ConfettiColors =
    {
        new(0.96f, 0.72f, 0.20f, 1f),
        new(0.90f, 0.30f, 0.32f, 1f),
        new(0.35f, 0.68f, 0.94f, 1f),
        new(0.55f, 0.86f, 0.42f, 1f),
        new(0.82f, 0.42f, 0.90f, 1f),
    };

    /// Bursts a shower of small colored pieces across the reveal panel --
    /// the one visual flourish reserved for a BlackLabel win. Safe to
    /// call repeatedly; clears any stale pieces from a previous call
    /// first. Purely cosmetic: a failure anywhere here degrades to "no
    /// confetti," never to a broken reveal.
    public void BeginConfetti()
    {
        if (_reducedMotion()) return;
        try
        {
            EndConfetti();
            var tree = LiveTreeOrNull();
            if (tree is null)
            {
                return;
            }

            var layer = UiFactory.CreateRect("ConfettiLayer", tree.RevealPanel.transform);
            UiFactory.Stretch(layer);
            layer.SetAsLastSibling();
            _confettiLayer = layer;

            for (var i = 0; i < ConfettiPieceCount; i++)
            {
                var color = ConfettiColors[UnityEngine.Random.Range(0, ConfettiColors.Length)];
                var piece = UiFactory.CreateImage($"Piece{i.ToString(CultureInfo.InvariantCulture)}", layer, color);
                var width = UnityEngine.Random.Range(6f, 11f);
                var height = UnityEngine.Random.Range(10f, 18f);
                var x = UnityEngine.Random.Range(-660f, 660f);
                var y = UnityEngine.Random.Range(220f, 320f);
                UiFactory.Center(piece.rectTransform, width, height, x, y);
                piece.rectTransform.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));

                var velocity = new Vector2(UnityEngine.Random.Range(-40f, 40f), UnityEngine.Random.Range(-360f, -220f));
                var angularVelocity = UnityEngine.Random.Range(-260f, 260f);
                _confettiPieces.Add(new ConfettiPiece(piece.rectTransform, velocity, angularVelocity));
            }
        }
        catch (Exception)
        {
            EndConfetti();
        }
    }

    /// Advances the active confetti burst by one frame. No-op if no burst
    /// is active.
    public void AdvanceConfetti(float deltaSeconds)
    {
        if (_confettiLayer == null || _confettiPieces.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var piece in _confettiPieces)
            {
                if (piece.Rect == null)
                {
                    continue;
                }

                piece.Rect.anchoredPosition += piece.Velocity * deltaSeconds;
                piece.Rect.Rotate(0f, 0f, piece.AngularVelocityDegrees * deltaSeconds);
            }
        }
        catch (Exception)
        {
            EndConfetti();
        }
    }

    /// Removes any active confetti burst. Safe to call even if none is
    /// active, and safe to call more than once.
    public void EndConfetti()
    {
        try
        {
            if (_confettiLayer != null)
            {
                UnityEngine.Object.Destroy(_confettiLayer.gameObject);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
        finally
        {
            _confettiLayer = null;
            _confettiPieces.Clear();
        }
    }

    private RewardTileView LeaseTile()
    {
        var tile = _pool.FirstOrDefault(candidate =>
            candidate.IsAvailable && !_leased.Contains(candidate));
        if (tile is null)
        {
            tile = new RewardTileView(_stripContent, _font);
            _pool.Add(tile);
        }

        _leased.Add(tile);
        return tile;
    }

    private void ReleaseTiles(bool resetStrip = true)
    {
        EndConfetti();
        foreach (var tile in _leased)
        {
            tile.Release();
        }

        _leased.Clear();
        if (resetStrip)
        {
            SetStripX(0f);
        }
    }

    private void ResetPanels()
    {
        ResetBrokerPanels();
        // A new phase (including errors) must never inherit the spinning drone.
        _tensionPlayer.Stop();
        _artwork = null;
        if (_decisionDetailsButton != null)
        {
            _decisionDetailsButton.onClick.RemoveAllListeners();
            _decisionDetailsButton.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_decisionDetailsButton.gameObject);
            _decisionDetailsButton = null;
        }
        if (_galleryNextButton != null)
        {
            _galleryNextButton.onClick.RemoveAllListeners();
            _galleryNextButton.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_galleryNextButton.gameObject);
            _galleryNextButton = null;
        }
        UiFactory.Center(_resultCloseButton.GetComponent<RectTransform>(), 230f, 60f, 0f, -255f);
        LinkSelf(_resultCloseButton);
        if (_contentsRow != null) _contentsRow.SetActive(false);
        _contentsImages.Clear();
        _confirmationPanel.SetActive(false);
        _revealPanel.SetActive(false);
        _resultPanel.SetActive(false);
        _decisionPanel.SetActive(false);
        _errorPanel.SetActive(false);
        _stripViewport.SetActive(true);
        _relayHold?.Clear();
    }

    private void ResetConfirmationScroll()
    {
        Canvas.ForceUpdateCanvases();
        _confirmationScroll.StopMovement();
        _confirmationScroll.verticalNormalizedPosition = 1f;
    }

    private void ResetDecisionScroll()
    {
        Canvas.ForceUpdateCanvases();
        _decisionScroll.StopMovement();
        _decisionScroll.verticalNormalizedPosition = 1f;
    }

    private void ResetErrorScroll()
    {
        Canvas.ForceUpdateCanvases();
        _errorScroll.StopMovement();
        _errorScroll.verticalNormalizedPosition = 1f;
    }

    private void ClearButtons()
    {
        foreach (var button in new[]
                 {
                     _confirmButton,
                     _cancelButton,
                     _confirmationDetailsButton,
                     _skipButton,
                     _resultCloseButton,
                     _secureButton,
                     _relayButton,
                     _errorCloseButton
                 })
        {
            button.onClick.RemoveAllListeners();
            button.interactable = true;
        }
    }

    private static void SetButton(Button button, Action action)
    {
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => action());
    }

    private static void SetCancelHandler(Selectable button, Action? action)
    {
        var handler = button.GetComponent<OverlayCancelHandler>();
        if (handler == null)
        {
            handler = button.gameObject.AddComponent<OverlayCancelHandler>();
        }

        handler.Handler = action;
    }

    private Action? CreatePhaseCancelHandler(OpeningPhase phase, Action action)
    {
        if (OpeningCancelPolicy.Resolve(phase, _skipButton.interactable) == OpeningCancelIntent.Ignore &&
            phase != OpeningPhase.Revealing)
        {
            return null;
        }

        return () =>
        {
            if (OpeningCancelPolicy.Resolve(phase, _skipButton.interactable) != OpeningCancelIntent.Ignore)
            {
                action();
            }
        };
    }

    private static void LinkHorizontal(params Selectable[] buttons)
    {
        if (buttons.Length < 2)
        {
            throw new ArgumentException("At least two buttons are required for horizontal navigation.", nameof(buttons));
        }

        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.navigation = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                selectOnLeft = buttons[(index + buttons.Length - 1) % buttons.Length],
                selectOnRight = buttons[(index + 1) % buttons.Length],
                selectOnUp = button,
                selectOnDown = button
            };
        }
    }

    private static void LinkSelf(Selectable button)
    {
        button.navigation = new Navigation
        {
            mode = Navigation.Mode.Explicit,
            selectOnLeft = button,
            selectOnRight = button,
            selectOnUp = button,
            selectOnDown = button
        };
    }

    private static void Select(Selectable selectable)
    {
        EventSystem.current?.SetSelectedGameObject(selectable.gameObject);
        selectable.Select();
    }

    private void RestoreSelection()
    {
        var eventSystem = EventSystem.current;
        if (_previousSelection != null &&
            _previousSelection.activeInHierarchy &&
            eventSystem != null)
        {
            eventSystem.SetSelectedGameObject(_previousSelection);
        }

        _previousSelection = null;
    }

    private void BuildOwnedTree()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RouletteOverlay));
        }

        var root = new GameObject(
            _ownedRootName,
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster),
            typeof(CanvasGroup));
        root.SetActive(false);
        try
        {
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            ApplyResponsiveScale(scaler);
            var raycaster = root.GetComponent<GraphicRaycaster>();
            var canvasGroup = root.GetComponent<CanvasGroup>();
            var font = UiFactory.ResolveFont();

            var blocker = UiFactory.CreateImage(
                "Blocker",
                root.transform,
                new Color(0.015f, 0.02f, 0.02f, 0.88f),
                raycastTarget: true);
            UiFactory.Stretch(blocker.rectTransform);

            var confirmation = BuildConfirmation(blocker.transform, font);
            var reveal = BuildReveal(blocker.transform, font);
            var result = BuildResult(blocker.transform, font);
            var decision = BuildDecision(blocker.transform, font);
            var error = BuildError(blocker.transform, font);
            foreach (var button in new[]
                     {
                         confirmation.Details,
                         confirmation.Confirm,
                         confirmation.Cancel,
                         reveal.Skip,
                         result.Close,
                         decision.Secure,
                         decision.Relay,
                         error.Close
                     })
            {
                UiFactory.AddFocusTreatment(button);
            }
            LinkSelf(reveal.Skip);
            LinkSelf(result.Close);
            LinkSelf(error.Close);
            _relayHold = decision.Relay.gameObject.AddComponent<RelayHoldButton>();

            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
            _tree = new OverlayTree(
                root,
                canvas,
                scaler,
                raycaster,
                canvasGroup,
                font,
                blocker.gameObject,
                confirmation.Panel,
                confirmation.Catalog,
                confirmation.CatalogGrid,
                confirmation.Scroll,
                confirmation.Note,
                confirmation.Details,
                confirmation.Confirm,
                confirmation.Cancel,
                reveal.Panel,
                reveal.Viewport,
                reveal.Header,
                reveal.Footer,
                reveal.Content,
                reveal.Skip,
                result.Panel,
                result.Title,
                result.Text,
                result.Image,
                result.Close,
                decision.Panel,
                decision.Title,
                decision.Reward,
                decision.Odds,
                decision.Candidates,
                decision.Scroll,
                decision.Meter,
                decision.Image,
                decision.Secure,
                decision.Relay,
                error.Panel,
                error.Title,
                error.Text,
                error.Scroll,
                error.Close);
            Diagnostic("Created a fresh owned overlay tree.");
        }
        catch
        {
            if (root != null)
            {
                root.SetActive(false);
                UnityEngine.Object.Destroy(root);
            }

            _tree = null;
            throw;
        }
    }

    internal static float ResponsiveScreenMatch(float screenWidth, float screenHeight)
    {
        if (float.IsNaN(screenWidth) ||
            float.IsInfinity(screenWidth) ||
            float.IsNaN(screenHeight) ||
            float.IsInfinity(screenHeight) ||
            screenWidth <= 0f ||
            screenHeight <= 0f)
        {
            return 0.5f;
        }

        var widthScale = screenWidth / ReferenceWidth;
        var heightScale = screenHeight / ReferenceHeight;
        var fitScale = Math.Min(
            screenWidth / (MaximumPanelWidth + SafeMargin * 2f),
            screenHeight / (MaximumPanelHeight + SafeMargin * 2f));
        var targetScale = Math.Max(
            Math.Min(widthScale, heightScale),
            Math.Min(Math.Max(widthScale, heightScale), fitScale));
        var logWidth = Math.Log(widthScale, 2d);
        var logHeight = Math.Log(heightScale, 2d);
        if (Math.Abs(logHeight - logWidth) < 0.000001d)
        {
            return 0.5f;
        }

        var match = (Math.Log(targetScale, 2d) - logWidth) / (logHeight - logWidth);
        return (float)Math.Max(0d, Math.Min(1d, match));
    }

    private static void ApplyResponsiveScale(CanvasScaler scaler) =>
        scaler.matchWidthOrHeight = ResponsiveScreenMatch(Screen.width, Screen.height);

    private void DiscardOwnedTree(string reason)
    {
        ResetBrokerPanels();
        _brokerSpriteCache.Clear();
        var tree = _tree;
        _tree = null;
        _pool.Clear();
        _leased.Clear();
        _catalogPool.Clear();
        _catalogLeased.Clear();
        _catalogHeaders.Clear();
        _previousSelection = null;
        _displayedResultRewardId = null;
        _displayedDecisionRewardId = null;
        _manifestLot = null;
        _artwork = null;
        _relayHold = null;
        _runActive = false;
        if (tree?.Root != null)
        {
            tree.Root.SetActive(false);
            UnityEngine.Object.Destroy(tree.Root);
        }

        Diagnostic($"Released the owned overlay tree: {reason}.");
    }

    private OverlayTree? LiveTreeOrNull()
    {
        var tree = _tree;
        return tree is not null && HasEssentialSubtree(tree)
            ? tree
            : null;
    }

    private OverlayTree RequireLiveTree() =>
        LiveTreeOrNull()
        ?? throw new InvalidOperationException(
            "The Contraband Cases overlay was destroyed by Unity and is no longer available for this presentation.");

    private static bool HasEssentialSubtree(OverlayTree? tree) =>
        tree is not null &&
        tree.Root != null &&
        tree.Canvas != null &&
        tree.Scaler != null &&
        tree.Raycaster != null &&
        tree.CanvasGroup != null &&
        tree.Font != null &&
        tree.Blocker != null &&
        tree.ConfirmationPanel != null &&
        tree.CatalogText != null &&
        tree.CatalogGridContent != null &&
        tree.ConfirmationScroll != null &&
        tree.ConfirmationNote != null &&
        tree.ConfirmationDetailsButton != null &&
        tree.ConfirmButton != null &&
        tree.CancelButton != null &&
        tree.RevealPanel != null &&
        tree.StripViewport != null &&
        tree.RevealHeader != null &&
        tree.RevealFooter != null &&
        tree.StripContent != null &&
        tree.SkipButton != null &&
        tree.ResultPanel != null &&
        tree.ResultTitle != null &&
        tree.ResultText != null &&
        tree.ResultImage != null &&
        tree.ResultCloseButton != null &&
        tree.DecisionPanel != null &&
        tree.DecisionTitle != null &&
        tree.DecisionReward != null &&
        tree.DecisionOdds != null &&
        tree.DecisionCandidates != null &&
        tree.DecisionScroll != null &&
        tree.DecisionMeter != null &&
        tree.DecisionImage != null &&
        tree.SecureButton != null &&
        tree.RelayButton != null &&
        tree.ErrorPanel != null &&
        tree.ErrorTitle != null &&
        tree.ErrorText != null &&
        tree.ErrorScroll != null &&
        tree.ErrorCloseButton != null;

    private void Diagnostic(string message) => _diagnostic?.Invoke(message);

    // Reference-resolution constants for the catalog grid, sized to sit
    // inside the same CatalogViewport (1060 wide) the plain-text catalog
    // already scrolls inside of. Six columns keeps individual tiles
    // legible while still fitting the full ~80-entry manifest catalog in a
    // handful of screens' worth of scrolling.
    private const float CatalogGridContentWidth = 1024f;
    private const float CatalogTileWidth = 150f;
    private const float CatalogTileHeight = 150f;
    private const float CatalogColumnSpacing = 12f;
    private const float CatalogRowSpacing = 12f;
    private const float CatalogHeaderHeight = 34f;
    private const float CatalogHeaderGap = 10f;
    private const float CatalogSectionGap = 22f;

    /// Switches the confirmation panel's shared catalog viewport to its
    /// plain-text mode -- the Manifest ladder confirmation's short
    /// server-published family-odds summary -- and releases any leased
    /// icon-grid tiles so a stale grid doesn't sit hidden behind it. The
    /// legacy single-catalog confirmation (ShowConfirmation) never uses
    /// this mode at all; it only ever shows the icon grid.
    private void ShowCatalogText()
    {
        _catalogGridContent.gameObject.SetActive(false);
        _catalogText.gameObject.SetActive(true);
        _confirmationScroll.content = _catalogText.rectTransform;
        ReleaseCatalogTiles();
    }

    /// Switches the confirmation panel's shared catalog viewport to the
    /// icon grid and (re)builds it for the given reward list. Used by the
    /// legacy single-catalog confirmation (ShowConfirmation).
    private void ShowCatalogGrid(IReadOnlyList<ValidatedReward> rewards)
    {
        _catalogText.gameObject.SetActive(false);
        _catalogGridContent.gameObject.SetActive(true);
        _confirmationScroll.content = _catalogGridContent;
        BuildCatalogGrid(rewards);
    }

    /// Rebuilds the confirmation screen's reward catalog as a grid of
    /// compact icon tiles, grouped worst-to-best by rarity tier with a
    /// colored section header per tier, each tile carrying the item's
    /// icon (or the "ITEM" text fallback until/unless a sprite resolves),
    /// name, and exact published weight percentage. Every reward always
    /// gets a tile immediately (bound with sprite: null, i.e. the
    /// fallback) -- artwork is applied later, asynchronously, by
    /// BindCatalogSprite as the caller's sprite batch resolves, exactly
    /// the way the spin strip's tiles are bound ahead of their artwork.
    ///
    /// Built once per confirmation open rather than pooled/leased across a
    /// visible window like the spin strip's tiles: this screen's whole
    /// point is a static, scrollable "read every possible reward" view
    /// (not a per-frame animation), so there is no small "visible subset"
    /// to virtualize -- the tiles themselves ARE still pooled/reused
    /// across repeated opens via LeaseCatalogTile, the same idiom
    /// LeaseTile already uses for the spin strip, so reopening the
    /// confirmation screen doesn't re-allocate ~80 GameObjects every time.
    private void BuildCatalogGrid(IReadOnlyList<ValidatedReward> rewards)
    {
        ReleaseCatalogTiles();
        var content = _catalogGridContent;
        var font = _font;
        var sections = CatalogGridLayout.GroupByTier(rewards);
        var columns = CatalogGridLayout.ColumnCount(
            CatalogGridContentWidth,
            CatalogTileWidth,
            CatalogColumnSpacing);
        var gridWidth = columns * (CatalogTileWidth + CatalogColumnSpacing) - CatalogColumnSpacing;
        var leftPad = (CatalogGridContentWidth - gridWidth) * 0.5f;

        var y = 0f;
        for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            var section = sections[sectionIndex];
            var rarityColor = UiFactory.RarityColor(section.Tier);
            var rarityInfo = RewardRarities.GetInfo(section.Tier);

            var header = UiFactory.CreateImage(
                "CatalogSectionHeader",
                content,
                new Color(rarityColor.r, rarityColor.g, rarityColor.b, 0.16f));
            _catalogHeaders.Add(header.gameObject);
            header.rectTransform.anchorMin = new Vector2(0f, 1f);
            header.rectTransform.anchorMax = new Vector2(0f, 1f);
            header.rectTransform.pivot = new Vector2(0f, 1f);
            header.rectTransform.sizeDelta = new Vector2(gridWidth, CatalogHeaderHeight);
            header.rectTransform.anchoredPosition = new Vector2(leftPad, -y);
            var headerLabel = UiFactory.CreateText(
                "Label",
                header.transform,
                font,
                17,
                TextAnchor.MiddleLeft,
                rarityColor);
            UiFactory.Stretch(headerLabel.rectTransform, 14f, 0f, -14f, 0f);
            headerLabel.fontStyle = FontStyle.Bold;
            var combinedWeight = section.Rewards.Sum(reward => reward.Weight);
            headerLabel.text =
                $"{rarityInfo.DisplayName.ToUpperInvariant()}  —  {section.Rewards.Count} ITEM" +
                (section.Rewards.Count == 1 ? string.Empty : "S") +
                $"  —  {combinedWeight.ToString("0.##%", CultureInfo.InvariantCulture)} COMBINED";
            y += CatalogHeaderHeight + CatalogHeaderGap;

            for (var index = 0; index < section.Rewards.Count; index++)
            {
                var reward = section.Rewards[index];
                var (cellX, cellY) = CatalogGridLayout.CellOffset(
                    index,
                    columns,
                    CatalogTileWidth,
                    CatalogTileHeight,
                    CatalogColumnSpacing,
                    CatalogRowSpacing);
                var tile = LeaseCatalogTile();
                tile.Root.anchorMin = new Vector2(0f, 1f);
                tile.Root.anchorMax = new Vector2(0f, 1f);
                tile.Root.pivot = new Vector2(0f, 1f);
                tile.Root.sizeDelta = new Vector2(CatalogTileWidth, CatalogTileHeight);
                tile.Root.anchoredPosition = new Vector2(leftPad + (float)cellX, -(y + (float)cellY));
                tile.Bind(reward, sprite: null);
            }

            y += (float)CatalogGridLayout.GridHeight(
                section.Rewards.Count,
                columns,
                CatalogTileHeight,
                CatalogRowSpacing);
            if (sectionIndex < sections.Count - 1)
            {
                y += CatalogSectionGap;
            }
        }

        content.sizeDelta = new Vector2(-36f, y);
    }

    private CatalogTileView LeaseCatalogTile()
    {
        var tile = _catalogPool.FirstOrDefault(candidate =>
            candidate.IsAvailable && !_catalogLeased.Contains(candidate));
        if (tile is null)
        {
            tile = new CatalogTileView(_catalogGridContent, _font);
            _catalogPool.Add(tile);
        }

        _catalogLeased.Add(tile);
        return tile;
    }

    private void ReleaseCatalogTiles()
    {
        foreach (var tile in _catalogLeased)
        {
            tile.Release();
        }

        _catalogLeased.Clear();

        // Section headers aren't pooled: there are at most four of them
        // (one per rarity tier) per build, versus up to ~80 tiles, so the
        // GC cost of rebuilding them each time is negligible and it avoids
        // a second, barely-used pooling scheme just for headers.
        foreach (var header in _catalogHeaders)
        {
            if (header != null)
            {
                UnityEngine.Object.Destroy(header);
            }
        }

        _catalogHeaders.Clear();
    }

    private string? TrackManifestAnchor(ManifestSnapshot snapshot)
    {
        if (snapshot.CurrentLot is not { } lot)
        {
            return null;
        }

        return TrackManifestAnchor(lot);
    }

    private string? TrackManifestAnchor(ManifestLotSnapshot lot)
    {
        _manifestLot = lot;
        _artwork = RewardArtworkBinding.ForLot(lot);
        return _artwork.AnchorId;
    }

    private void SetDecisionSprite(Sprite? sprite)
    {
        if (sprite != null && _artwork is not null) _artwork.TryAccept(_artwork.AnchorId, true);
        _decisionImage.sprite = sprite != null ? sprite : LootArtwork.Seal(_artwork?.Grade ?? RewardRarity.ScavGrade);
        _decisionImage.gameObject.SetActive(true);
        _decisionImage.color = Color.white;
        UiFactory.Center(
            _decisionReward.rectTransform,
            730f,
            145f,
            140f,
            230f);
    }

    private void ShowContents(ManifestSnapshot snapshot)
    {
        ShowContents(snapshot.CurrentLot!);
    }

    private void ShowContents(ManifestLotSnapshot lot)
    {
        SetDecisionSprite(null);
        if (_contentsRow != null) UnityEngine.Object.Destroy(_contentsRow);
        _contentsImages.Clear();
        _contentsRow = UiFactory.CreateRect("Contents", _decisionPanel.transform).gameObject;
        var overflow = lot.Contents.Count > 9;
        var visible = lot.Contents.Take(overflow ? 8 : 9).ToArray();
        var total = visible.Length + (overflow ? 1 : 0);
        for (var i = 0; i < visible.Length; i++)
        {
            var content = visible[i];
            var item = UiFactory.CreateImage("Content", _contentsRow.transform, Color.white);
            var x = (i - (total - 1) / 2f) * 108f;
            UiFactory.Center(item.rectTransform, 84f, 60f, x, 101f);
            item.preserveAspect = true;
            item.sprite = LootArtwork.Seal(lot.Grade);
            _contentsImages[$"content:{content.TemplateId}"] = item;
            var quantity = UiFactory.CreateText("Count", item.transform, _font, 14, TextAnchor.LowerRight, Color.white);
            UiFactory.Stretch(quantity.rectTransform);
            quantity.text = $"×{content.Quantity}";
            var name = UiFactory.CreateText("Name", _contentsRow.transform, _font, 12, TextAnchor.MiddleCenter, Color.white);
            UiFactory.Center(name.rectTransform, 104f, 24f, x, 58f);
            name.supportRichText = false;
            name.text = content.DisplayName.Length <= 24 ? content.DisplayName : content.DisplayName.Substring(0, 23) + "…";
        }
        if (overflow)
        {
            var more = UiFactory.CreateText("MoreContents", _contentsRow.transform, _font, 15, TextAnchor.MiddleCenter, Color.white);
            UiFactory.Center(more.rectTransform, 104f, 74f, (total - 1) / 2f * 108f, 91f);
            more.text = $"+{lot.Contents.Count - visible.Length} more\nFull list below";
        }
    }

    private void ConfigureManifestDetails(ManifestSnapshot snapshot, bool entitlement)
    {
        var details = _decisionCandidates.text;
        var lot = snapshot.CurrentLot!;
        var value = BrokerPresentation.Value(lot);
        if (snapshot.CaseTemplateId == CaseContracts.CashCache && lot.UseValue is long cashValue)
            value = $"{CashPayouts.ValueLabel(lot.AnchorTemplateId)}: ₽{cashValue.ToString("N0", CultureInfo.InvariantCulture)}.";
        var summary = entitlement
            ? $"<b>KEEP</b>  Every item shown. No additional key.\n{value}\n" + BuildRelayDisclosure(snapshot)
            : $"{value}\n{lot.Contents.Count} item types included; see Contents & Details for every quantity.\n" +
                "Lock keeps this package. Discard gives up the whole package permanently.";
        if (!entitlement && !snapshot.AvailableActions.CanBurn)
            summary += "\n<color=#E8A076>The next offer's content is missing. Discard is unavailable.</color>";
        _decisionCandidates.text = summary;
        _decisionDetailsButton = UiFactory.CreateButton("ManifestDetails", _decisionPanel.transform, _font,
            "CONTENTS & DETAILS", new Color(0.16f, 0.19f, 0.20f, 1f));
        UiFactory.Center(_decisionDetailsButton.GetComponent<RectTransform>(), 300f, 30f, 0f, 26f);
        _decisionDetailsButton.GetComponentInChildren<Text>().fontSize = 15;
        var expanded = false;
        SetButton(_decisionDetailsButton, () =>
        {
            expanded = !expanded;
            _decisionCandidates.text = expanded ? details : summary;
            _decisionDetailsButton.GetComponentInChildren<Text>().text = expanded ? "BACK TO DECISION" : "CONTENTS & DETAILS";
            ResetDecisionScroll();
        });
        LinkDecisionDetails();
        ResetDecisionScroll();
    }

    private void LinkDecisionDetails()
    {
        if (_decisionDetailsButton == null) return;
        _decisionDetailsButton.navigation = new Navigation
        {
            mode = Navigation.Mode.Explicit,
            selectOnDown = _secureButton,
            selectOnUp = _secureButton
        };
        foreach (var button in new[] { _secureButton, _relayButton })
        {
            var navigation = button.navigation;
            navigation.selectOnUp = _decisionDetailsButton;
            button.navigation = navigation;
        }
    }

    private void ConfigureRelayHold(bool enabled, Action confirmed)
    {
        ConfigureHold(enabled, "RELAY", confirmed);
    }

    private void ConfigureHold(bool enabled, string actionLabel, Action confirmed)
    {
        var hold = _relayHold
            ?? throw new InvalidOperationException("The hold control is unavailable.");
        _relayButton.onClick.RemoveAllListeners();
        _relayButton.interactable = enabled;
        hold.Configure(enabled, actionLabel, confirmed);
    }

    private void DisableRelayHold(string label)
    {
        var hold = _relayHold
            ?? throw new InvalidOperationException("The Relay hold control is unavailable.");
        _relayButton.onClick.RemoveAllListeners();
        _relayButton.interactable = false;
        hold.Configure(enabled: false, () => { });
        _relayButton.GetComponentInChildren<Text>().text = label;
    }

    private static string BuildCandidateText(RelaySnapshot snapshot, RewardRarity currentRarity)
    {
        if (!snapshot.Status.RelayEligible)
        {
            return currentRarity == RewardRarity.BlackLabel
                ? "<b>LEGENDARY IS THE TOP TIER.</b> Secure this reward; it cannot be staked again."
                : "<b>THIS RELAY CHAIN IS TERMINAL.</b> No further candidate pool is available.";
        }

        var upgradeRarity = RewardRarities.GetInfo(RelayRules.GetUpgradeRarity(currentRarity)).DisplayName;
        var currentRarityName = RewardRarities.GetInfo(currentRarity).DisplayName;
        return
            $"<b>RARITY UPGRADE → {upgradeRarity}</b>\n" +
            CandidateLines(snapshot.UpgradeCandidates) +
            $"\n\n<b>SAME-RARITY SIDEGRADE → {currentRarityName}</b>\n" +
            CandidateLines(snapshot.SidegradeCandidates);
    }

    private static string CandidateLines(IReadOnlyList<RelayCandidate> candidates) =>
        string.Join("   •   ", candidates.Select(candidate =>
            $"{candidate.DisplayName} ({candidate.HandbookValue.ToString("N0", CultureInfo.InvariantCulture)}₽)"));

    private static string BuildSealHistory(ManifestSnapshot snapshot) =>
        string.Join("   •   ", snapshot.FamilySeals.Select(seal =>
        {
            var state = seal.Locked
                ? "LOCKED"
                : seal.Burned
                    ? "DISCARDED"
                    : seal.Revealed && seal.Ordinal == snapshot.CurrentOrdinal
                        ? "CURRENT"
                        : seal.Revealed
                            ? "REVEALED"
                            : "SEALED";
            var label = ManifestPresentationPolicy.FamilySealLabel(seal);
            return $"{seal.Ordinal.ToString(CultureInfo.InvariantCulture)}  {label}  —  {state}";
        }));

    private static string BuildRelayDisclosure(ManifestSnapshot snapshot)
    {
        if (snapshot.CaseTemplateId == CaseContracts.CashCache)
            return "Collect the exact payout shown. No Relay or Favor applies to Cash Cache.";
        var relay = snapshot.Relay;
        if (relay is null || !snapshot.AvailableActions.CanRelay)
        {
            return (relay?.TerminalReason is string reason ? BrokerPresentation.Text(reason) :
                "Relay is unavailable for this reward.") + " Send your saved prize to Messenger.";
        }

        var grade = relay.UpgradeGrade is RewardRarity next
            ? RewardRarities.GetInfo(next).DisplayName
            : "terminal";
        var range = relay.CandidateValueMin is long minimum && relay.CandidateValueMax is long maximum
            ? $"Published candidate use-value range: {minimum.ToString("N0", CultureInfo.InvariantCulture)}–{maximum.ToString("N0", CultureInfo.InvariantCulture)}₽."
            : "No complete candidate range is available.";
        return
            $"<b>RELAY STAGE {relay.Stage} → {grade.ToUpperInvariant()}</b>  •  Costs {relay.KeyCost} key\n" +
            "Replace: a different same-rarity reward; it can be worth less and ends the chain.\n" +
            "Further wagers depend on available upgrades; three is the maximum, not a promised climb.\n" +
            (relay.GuaranteeActive
                ? "Favor guarantee: upgrade is certain; Favor resets to 0/3."
                : $"Loss: reward and key are lost. Favor {relay.FavorBefore}/3 → {relay.FavorAfterOnLoss}/3.") +
            $"\n{range}";
    }

    private static string BuildReceiptFavor(ManifestSnapshot snapshot)
    {
        var receipt = snapshot.LatestReceipt;
        return receipt is null
            ? string.Empty
            : $" (was {receipt.BrokerFavorBefore}; now {receipt.BrokerFavorAfter})";
    }

    private static string OutcomeLabel(RelayOutcome outcome) => outcome switch
    {
        RelayOutcome.Secured => "SECURED WITHOUT RELAY",
        RelayOutcome.RarityUpgrade => "RARITY UPGRADE",
        RelayOutcome.SameRaritySidegrade => "SAME-RARITY SIDEGRADE",
        RelayOutcome.Confiscated => "CONFISCATED",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static (ScrollRect Scroll, Text Text) BuildScrollableText(
        string name,
        Transform parent,
        Font font,
        int fontSize,
        float width,
        float height,
        float x,
        float y,
        Color textColor)
    {
        var viewport = UiFactory.CreateImage(
            $"{name}Viewport",
            parent,
            new Color(0.025f, 0.031f, 0.031f, 0.78f),
            raycastTarget: true);
        UiFactory.Center(viewport.rectTransform, width, height, x, y);
        viewport.gameObject.AddComponent<RectMask2D>();

        var text = UiFactory.CreateText(name, viewport.transform, font, fontSize, TextAnchor.UpperLeft, textColor);
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.rectTransform.anchorMin = new Vector2(0f, 1f);
        text.rectTransform.anchorMax = new Vector2(1f, 1f);
        text.rectTransform.pivot = new Vector2(0.5f, 1f);
        text.rectTransform.anchoredPosition = new Vector2(0f, -14f);
        text.rectTransform.sizeDelta = new Vector2(-34f, 0f);
        var fitter = text.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scrollbarTrack = UiFactory.CreateImage(
            $"{name}Scrollbar",
            parent,
            new Color(0.11f, 0.13f, 0.13f, 1f),
            raycastTarget: true);
        UiFactory.Center(scrollbarTrack.rectTransform, 14f, height, x + width * 0.5f + 10f, y);
        var slidingArea = UiFactory.CreateRect("SlidingArea", scrollbarTrack.transform);
        UiFactory.Stretch(slidingArea, 2f, 2f, -2f, -2f);
        var handle = UiFactory.CreateImage(
            "Handle",
            slidingArea,
            new Color(0.72f, 0.75f, 0.73f, 1f),
            raycastTarget: true);
        UiFactory.Stretch(handle.rectTransform);
        var scrollbar = scrollbarTrack.gameObject.AddComponent<Scrollbar>();
        scrollbar.handleRect = handle.rectTransform;
        scrollbar.targetGraphic = handle;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.content = text.rectTransform;
        scroll.viewport = viewport.rectTransform;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.scrollSensitivity = 34f;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        viewport.gameObject.AddComponent<OverlayScrollKeys>().Scroll = scroll;
        return (scroll, text);
    }

    private static (
        GameObject Panel,
        Text Catalog,
        RectTransform CatalogGrid,
        ScrollRect Scroll,
        Text Note,
        Button Details,
        Button Confirm,
        Button Cancel) BuildConfirmation(
        Transform parent,
        Font font)
    {
        // A cool, restrained slate accent for every structural/decorative element on this screen -- frame,
        // title outline, subtitle badge, dividers, viewport outline. Reads as "military/manifest terminal"
        // against the near-black panel rather than the carnival-amber the whole screen used to share.
        // Amber/gold is reserved for exactly one job on this screen: the OPEN CASE button below, via its own
        // ctaAccent -- everywhere else that used to reuse the same amber now uses this instead.
        var accent = new Color(0.50f, 0.58f, 0.64f);
        var ctaAccent = new Color(0.90f, 0.71f, 0.32f);

        // A slightly larger accent-tinted frame sitting behind the panel reads as a bordered document rather
        // than a flat rectangle -- cheap to build with UI primitives alone (no bundled art asset needed).
        var frame = UiFactory.CreateImage("ConfirmationFrame", parent, new Color(accent.r, accent.g, accent.b, 0.55f));
        UiFactory.Center(frame.rectTransform, 1248f, 908f);

        // The frame owns the screen so phase changes hide both together.
        var panel = UiFactory.CreateImage("Confirmation", frame.transform, new Color(0.055f, 0.065f, 0.065f, 0.99f));
        UiFactory.Center(panel.rectTransform, 1240f, 900f);

        // A bright accent strip along the very top edge anchors the eye immediately, the way a letterhead or
        // classified-document banner would.
        var topBar = UiFactory.CreateImage("TopBar", panel.transform, accent);
        UiFactory.Center(topBar.rectTransform, 1240f, 7f, 0f, 448.5f);

        var title = UiFactory.CreateText("Title", panel.transform, font, 40, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(title.rectTransform, 1120f, 64f, 0f, 382f);
        title.fontStyle = FontStyle.Bold;
        title.text = "BR-12 CONTRABAND MANIFEST";
        var titleShadow = title.gameObject.AddComponent<Shadow>();
        titleShadow.effectColor = new Color(0f, 0f, 0f, 0.65f);
        titleShadow.effectDistance = new Vector2(0f, -2f);
        var titleOutline = title.gameObject.AddComponent<Outline>();
        titleOutline.effectColor = new Color(accent.r, accent.g, accent.b, 0.35f);
        titleOutline.effectDistance = new Vector2(1f, -1f);

        var subtitleBadge = UiFactory.CreateImage("SubtitleBadge", panel.transform, new Color(accent.r, accent.g, accent.b, 0.18f));
        UiFactory.Center(subtitleBadge.rectTransform, 560f, 40f, 0f, 330f);
        var subtitle = UiFactory.CreateText("Subtitle", panel.transform, font, 21, TextAnchor.MiddleCenter, accent);
        UiFactory.Center(subtitle.rectTransform, 1120f, 42f, 0f, 330f);
        subtitle.fontStyle = FontStyle.Bold;
        subtitle.text = "COST  •  1 BR-12 CASE + 1 RELAY KEY";

        var headerDivider = UiFactory.CreateImage("HeaderDivider", panel.transform, new Color(accent.r, accent.g, accent.b, 0.4f));
        UiFactory.Center(headerDivider.rectTransform, 1120f, 2f, 0f, 300f);

        var viewport = UiFactory.CreateImage(
            "CatalogViewport",
            panel.transform,
            new Color(0.025f, 0.031f, 0.031f, 0.92f),
            raycastTarget: true);
        UiFactory.Center(viewport.rectTransform, 1060f, 520f, -10f, 20f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var viewportOutline = viewport.gameObject.AddComponent<Outline>();
        viewportOutline.effectColor = new Color(accent.r, accent.g, accent.b, 0.5f);
        viewportOutline.effectDistance = new Vector2(1.5f, -1.5f);

        // Two alternative contents share this one viewport/scroll: a plain scrolling text block (the
        // Manifest ladder confirmation's server-published odds summary/audit toggle) and the icon grid (the
        // legacy single-catalog confirmation's reward list). RouletteOverlay swaps ScrollRect.content and
        // each object's active state between the two; only one is ever visible at a time.
        var catalog = UiFactory.CreateText(
            "Catalog",
            viewport.transform,
            font,
            19,
            TextAnchor.UpperLeft,
            Color.white);
        catalog.verticalOverflow = VerticalWrapMode.Overflow;
        catalog.rectTransform.anchorMin = new Vector2(0f, 1f);
        catalog.rectTransform.anchorMax = new Vector2(1f, 1f);
        catalog.rectTransform.pivot = new Vector2(0.5f, 1f);
        catalog.rectTransform.anchoredPosition = new Vector2(0f, -14f);
        catalog.rectTransform.sizeDelta = new Vector2(-36f, 0f);
        var fitter = catalog.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var catalogGrid = UiFactory.CreateRect("CatalogGrid", viewport.transform);
        catalogGrid.anchorMin = new Vector2(0f, 1f);
        catalogGrid.anchorMax = new Vector2(1f, 1f);
        catalogGrid.pivot = new Vector2(0.5f, 1f);
        catalogGrid.anchoredPosition = new Vector2(0f, -14f);
        catalogGrid.sizeDelta = new Vector2(-36f, 0f);
        catalogGrid.gameObject.SetActive(false);

        var scrollbarTrack = UiFactory.CreateImage(
            "CatalogScrollbar",
            panel.transform,
            new Color(0.11f, 0.13f, 0.13f, 1f),
            raycastTarget: true);
        UiFactory.Center(scrollbarTrack.rectTransform, 14f, 520f, 532f, 20f);
        var slidingArea = UiFactory.CreateRect("SlidingArea", scrollbarTrack.transform);
        UiFactory.Stretch(slidingArea, 2f, 2f, -2f, -2f);
        // Plain neutral handle, matching every other scrollbar in this overlay (BuildScrollableText's
        // Details/Error/Decision handles) rather than standing out with the accent color.
        var handle = UiFactory.CreateImage(
            "Handle",
            slidingArea,
            new Color(0.72f, 0.75f, 0.73f, 1f),
            raycastTarget: true);
        UiFactory.Stretch(handle.rectTransform);
        var scrollbar = scrollbarTrack.gameObject.AddComponent<Scrollbar>();
        scrollbar.handleRect = handle.rectTransform;
        scrollbar.targetGraphic = handle;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.content = catalog.rectTransform;
        scroll.viewport = viewport.rectTransform;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.scrollSensitivity = 38f;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        var footerDivider = UiFactory.CreateImage("FooterDivider", panel.transform, new Color(accent.r, accent.g, accent.b, 0.4f));
        UiFactory.Center(footerDivider.rectTransform, 1120f, 2f, 0f, -302f);

        var note = UiFactory.CreateText("Note", panel.transform, font, 16, TextAnchor.MiddleCenter, new Color(0.75f, 0.78f, 0.76f));
        UiFactory.Center(note.rectTransform, 1080f, 44f, 0f, -278f);
        note.text = "Full server odds and exact audit values remain available before opening.";
        var details = UiFactory.CreateButton(
            "Details",
            panel.transform,
            font,
            "FULL ODDS & AUDIT DETAILS",
            new Color(0.18f, 0.20f, 0.20f, 1f));
        UiFactory.Center(details.GetComponent<RectTransform>(), 320f, 62f, -280f, -382f);
        var detailsLabel = details.GetComponentInChildren<Text>();
        detailsLabel.fontSize = 18;
        detailsLabel.resizeTextForBestFit = true;
        detailsLabel.resizeTextMinSize = 15;
        detailsLabel.resizeTextMaxSize = 18;

        // The primary action gets a brighter fill plus a warm gold accent border so it visually leads the
        // other two buttons, rather than all three reading with equal weight -- the one sanctioned use of a
        // warm accent as this screen's single call-to-action highlight.
        var confirmAccent = UiFactory.CreateImage("ConfirmAccent", panel.transform, ctaAccent);
        UiFactory.Center(confirmAccent.rectTransform, 296f, 68f, 45f, -382f);
        var confirm = UiFactory.CreateButton("Confirm", panel.transform, font, "OPEN CASE", new Color(0.56f, 0.36f, 0.10f, 1f));
        UiFactory.Center(confirm.GetComponent<RectTransform>(), 290f, 62f, 45f, -382f);
        var confirmLabel = confirm.GetComponentInChildren<Text>();
        confirmLabel.fontStyle = FontStyle.Bold;

        var cancel = UiFactory.CreateButton("Cancel", panel.transform, font, "CANCEL", new Color(0.20f, 0.22f, 0.22f, 1f));
        UiFactory.Center(cancel.GetComponent<RectTransform>(), 230f, 62f, 325f, -382f);
        return (frame.gameObject, catalog, catalogGrid, scroll, note, details, confirm, cancel);
    }

    private static (GameObject Panel, GameObject Viewport, Text Header, Text Footer, RectTransform Content, Button Skip) BuildReveal(
        Transform parent,
        Font font)
    {
        var panel = UiFactory.CreateImage("Reveal", parent, new Color(0.045f, 0.052f, 0.052f, 0.99f));
        UiFactory.Center(panel.rectTransform, 1380f, 620f);
        var header = UiFactory.CreateText("Header", panel.transform, font, 30, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(header.rectTransform, 1250f, 62f, 0f, 258f);
        header.fontStyle = FontStyle.Bold;
        header.resizeTextForBestFit = true;
        header.resizeTextMinSize = 22;
        header.resizeTextMaxSize = 30;

        var viewport = UiFactory.CreateImage("StripViewport", panel.transform, new Color(0.02f, 0.025f, 0.025f, 1f));
        UiFactory.Center(viewport.rectTransform, ViewportWidth, 340f, 0f, 48f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = UiFactory.CreateRect("StripContent", viewport.transform);
        content.anchorMin = new Vector2(0f, 0.5f);
        content.anchorMax = new Vector2(0f, 0.5f);
        content.pivot = new Vector2(0f, 0.5f);
        content.anchoredPosition = Vector2.zero;
        // A restrained cool-neutral marker (matching the confirmation screen's slate accent family) rather
        // than amber, so amber stays reserved for BlackLabel rarity and doesn't compete with it here.
        foreach (var direction in new[] { -1f, 1f })
        {
            var marker = UiFactory.CreateImage(direction > 0 ? "TopPointer" : "BottomPointer",
                viewport.transform, new Color(0.82f, 0.88f, 0.91f));
            UiFactory.Center(marker.rectTransform, 17f, 17f, 0f, direction * 157f);
            marker.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
        }

        var footer = UiFactory.CreateText("Footer", panel.transform, font, 24, TextAnchor.MiddleCenter, new Color(0.74f, 0.78f, 0.76f));
        UiFactory.Center(footer.rectTransform, 1190f, 70f, 0f, -165f);
        var skip = UiFactory.CreateButton("Skip", panel.transform, font, "SKIP REVEAL", new Color(0.29f, 0.30f, 0.28f, 1f));
        UiFactory.Center(skip.GetComponent<RectTransform>(), 230f, 58f, 0f, -238f);
        return (panel.gameObject, viewport.gameObject, header, footer, content, skip);
    }

    private static (GameObject Panel, Text Title, Text Text, Image Image, Button Close) BuildResult(
        Transform parent,
        Font font)
    {
        var panel = UiFactory.CreateImage("Result", parent, new Color(0.055f, 0.065f, 0.065f, 0.99f));
        UiFactory.Center(panel.rectTransform, 820f, 620f);
        var title = UiFactory.CreateText("Title", panel.transform, font, 34, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(title.rectTransform, 700f, 60f, 0f, 250f);
        title.fontStyle = FontStyle.Bold;
        title.text = "REWARD SECURED";
        var image = UiFactory.CreateImage("RewardImage", panel.transform, new Color(0.12f, 0.13f, 0.13f, 1f));
        UiFactory.Center(image.rectTransform, 360f, 230f, 0f, 88f);
        image.preserveAspect = true;
        var text = UiFactory.CreateText("RewardText", panel.transform, font, 23, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(text.rectTransform, 690f, 170f, 0f, -108f);
        // Neutral gray, matching the confirmation screen's Cancel button -- Close isn't a call-to-action.
        var close = UiFactory.CreateButton("Close", panel.transform, font, "CLOSE", new Color(0.20f, 0.22f, 0.22f, 1f));
        UiFactory.Center(close.GetComponent<RectTransform>(), 230f, 60f, 0f, -255f);
        return (panel.gameObject, title, text, image, close);
    }

    private static (
        GameObject Panel,
        Text Title,
        Text Reward,
        Text Odds,
        Text Candidates,
        ScrollRect Scroll,
        Text Meter,
        Image Image,
        Button Secure,
        Button Relay) BuildDecision(Transform parent, Font font)
    {
        var panel = UiFactory.CreateImage("RelayDecision", parent, new Color(0.045f, 0.052f, 0.052f, 0.995f));
        UiFactory.Center(panel.rectTransform, 1180f, 780f);
        var title = UiFactory.CreateText("Title", panel.transform, font, 34, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(title.rectTransform, 1100f, 54f, 0f, 330f);
        title.fontStyle = FontStyle.Bold;
        title.resizeTextForBestFit = true;
        title.resizeTextMinSize = 24;
        title.resizeTextMaxSize = 34;
        var image = UiFactory.CreateImage("RewardImage", panel.transform, new Color(0.12f, 0.13f, 0.13f, 1f));
        UiFactory.Center(image.rectTransform, 250f, 165f, -400f, 220f);
        image.preserveAspect = true;
        var reward = UiFactory.CreateText("Reward", panel.transform, font, 20, TextAnchor.UpperLeft, Color.white);
        UiFactory.Center(reward.rectTransform, 730f, 145f, 140f, 230f);
        reward.verticalOverflow = VerticalWrapMode.Overflow;
        reward.resizeTextForBestFit = true;
        reward.resizeTextMinSize = 16;
        reward.resizeTextMaxSize = 20;
        // Plain light neutral -- this is informational text (the published Relay odds), not a rarity signal
        // or a call-to-action, so it no longer shares BlackLabel/CTA amber. Matches the meter text below it.
        var odds = UiFactory.CreateText("Odds", panel.transform, font, 20, TextAnchor.MiddleCenter, new Color(0.78f, 0.84f, 0.80f));
        UiFactory.Center(odds.rectTransform, 1060f, 80f, 0f, -187f);
        odds.fontStyle = FontStyle.Bold;
        odds.resizeTextForBestFit = true;
        odds.resizeTextMinSize = 16;
        odds.resizeTextMaxSize = 20;
        var candidates = BuildScrollableText(
            "Candidates",
            panel.transform,
            font,
            17,
            1030f,
            120f,
            -8f,
            -75f,
            Color.white);
        var meter = UiFactory.CreateText("Meter", panel.transform, font, 19, TextAnchor.MiddleCenter, new Color(0.78f, 0.84f, 0.80f));
        UiFactory.Center(meter.rectTransform, 1080f, 70f, 0f, -268f);
        meter.resizeTextForBestFit = true;
        meter.resizeTextMinSize = 15;
        meter.resizeTextMaxSize = 19;
        var secure = UiFactory.CreateButton("Secure", panel.transform, font, "SECURE REWARD", new Color(0.20f, 0.42f, 0.27f, 1f));
        UiFactory.Center(secure.GetComponent<RectTransform>(), 350f, 60f, -205f, -340f);
        var relay = UiFactory.CreateButton("Relay", panel.transform, font, "RELAY — HOLD TO CONFIRM", new Color(0.52f, 0.20f, 0.10f, 1f));
        UiFactory.Center(relay.GetComponent<RectTransform>(), 430f, 60f, 205f, -340f);
        var relayLabel = relay.GetComponentInChildren<Text>();
        relayLabel.resizeTextForBestFit = true;
        relayLabel.resizeTextMinSize = 16;
        relayLabel.resizeTextMaxSize = 22;
        return (panel.gameObject, title, reward, odds, candidates.Text, candidates.Scroll, meter, image, secure, relay);
    }

    private static (GameObject Panel, Text Title, Text Text, ScrollRect Scroll, Button Close) BuildError(
        Transform parent,
        Font font)
    {
        var panel = UiFactory.CreateImage("Error", parent, new Color(0.075f, 0.055f, 0.055f, 0.99f));
        UiFactory.Center(panel.rectTransform, 820f, 470f);
        var title = UiFactory.CreateText("Title", panel.transform, font, 32, TextAnchor.MiddleCenter, new Color(0.94f, 0.62f, 0.48f));
        UiFactory.Center(title.rectTransform, 690f, 65f, 0f, 170f);
        title.fontStyle = FontStyle.Bold;
        title.resizeTextForBestFit = true;
        title.resizeTextMinSize = 22;
        title.resizeTextMaxSize = 32;
        title.text = "OPENING COULD NOT BE SHOWN";
        var body = BuildScrollableText(
            "ErrorText",
            panel.transform,
            font,
            20,
            650f,
            205f,
            -8f,
            20f,
            Color.white);
        body.Text.alignment = TextAnchor.MiddleCenter;
        var close = UiFactory.CreateButton("Close", panel.transform, font, "CLOSE", new Color(0.34f, 0.24f, 0.20f, 1f));
        UiFactory.Center(close.GetComponent<RectTransform>(), 230f, 60f, 0f, -180f);
        return (panel.gameObject, title, body.Text, body.Scroll, close);
    }

    private sealed class OverlayTree
    {
        public OverlayTree(
            GameObject root,
            Canvas canvas,
            CanvasScaler scaler,
            GraphicRaycaster raycaster,
            CanvasGroup canvasGroup,
            Font font,
            GameObject blocker,
            GameObject confirmationPanel,
            Text catalogText,
            RectTransform catalogGridContent,
            ScrollRect confirmationScroll,
            Text confirmationNote,
            Button confirmationDetailsButton,
            Button confirmButton,
            Button cancelButton,
            GameObject revealPanel,
            GameObject stripViewport,
            Text revealHeader,
            Text revealFooter,
            RectTransform stripContent,
            Button skipButton,
            GameObject resultPanel,
            Text resultTitle,
            Text resultText,
            Image resultImage,
            Button resultCloseButton,
            GameObject decisionPanel,
            Text decisionTitle,
            Text decisionReward,
            Text decisionOdds,
            Text decisionCandidates,
            ScrollRect decisionScroll,
            Text decisionMeter,
            Image decisionImage,
            Button secureButton,
            Button relayButton,
            GameObject errorPanel,
            Text errorTitle,
            Text errorText,
            ScrollRect errorScroll,
            Button errorCloseButton)
        {
            Root = root;
            Canvas = canvas;
            Scaler = scaler;
            Raycaster = raycaster;
            CanvasGroup = canvasGroup;
            Font = font;
            Blocker = blocker;
            ConfirmationPanel = confirmationPanel;
            CatalogText = catalogText;
            CatalogGridContent = catalogGridContent;
            ConfirmationScroll = confirmationScroll;
            ConfirmationNote = confirmationNote;
            ConfirmationDetailsButton = confirmationDetailsButton;
            ConfirmButton = confirmButton;
            CancelButton = cancelButton;
            RevealPanel = revealPanel;
            StripViewport = stripViewport;
            RevealHeader = revealHeader;
            RevealFooter = revealFooter;
            StripContent = stripContent;
            SkipButton = skipButton;
            ResultPanel = resultPanel;
            ResultTitle = resultTitle;
            ResultText = resultText;
            ResultImage = resultImage;
            ResultCloseButton = resultCloseButton;
            DecisionPanel = decisionPanel;
            DecisionTitle = decisionTitle;
            DecisionReward = decisionReward;
            DecisionOdds = decisionOdds;
            DecisionCandidates = decisionCandidates;
            DecisionScroll = decisionScroll;
            DecisionMeter = decisionMeter;
            DecisionImage = decisionImage;
            SecureButton = secureButton;
            RelayButton = relayButton;
            ErrorPanel = errorPanel;
            ErrorTitle = errorTitle;
            ErrorText = errorText;
            ErrorScroll = errorScroll;
            ErrorCloseButton = errorCloseButton;
        }

        public GameObject Root { get; }
        public Canvas Canvas { get; }
        public CanvasScaler Scaler { get; }
        public GraphicRaycaster Raycaster { get; }
        public CanvasGroup CanvasGroup { get; }
        public Font Font { get; }
        public GameObject Blocker { get; }
        public GameObject ConfirmationPanel { get; }
        public Text CatalogText { get; }
        public RectTransform CatalogGridContent { get; }
        public ScrollRect ConfirmationScroll { get; }
        public Text ConfirmationNote { get; }
        public Button ConfirmationDetailsButton { get; }
        public Button ConfirmButton { get; }
        public Button CancelButton { get; }
        public GameObject RevealPanel { get; }
        public GameObject StripViewport { get; }
        public Text RevealHeader { get; }
        public Text RevealFooter { get; }
        public RectTransform StripContent { get; }
        public Button SkipButton { get; }
        public GameObject ResultPanel { get; }
        public Text ResultTitle { get; }
        public Text ResultText { get; }
        public Image ResultImage { get; }
        public Button ResultCloseButton { get; }
        public GameObject DecisionPanel { get; }
        public Text DecisionTitle { get; }
        public Text DecisionReward { get; }
        public Text DecisionOdds { get; }
        public Text DecisionCandidates { get; }
        public ScrollRect DecisionScroll { get; }
        public Text DecisionMeter { get; }
        public Image DecisionImage { get; }
        public Button SecureButton { get; }
        public Button RelayButton { get; }
        public GameObject ErrorPanel { get; }
        public Text ErrorTitle { get; }
        public Text ErrorText { get; }
        public ScrollRect ErrorScroll { get; }
        public Button ErrorCloseButton { get; }
    }

    private sealed class RelayHoldButton : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerExitHandler,
        IDeselectHandler
    {
        private Button? _button;
        private Text? _label;
        private RelayHoldInput? _hold;
        private Action? _confirmed;
        private bool _enabled;
        private int _displayedProgress = -1;

        public void Configure(bool enabled, Action confirmed) =>
            Configure(enabled, "RELAY", confirmed);

        public void Configure(bool enabled, string actionLabel, Action confirmed)
        {
            _button = GetComponent<Button>()
                ?? throw new InvalidOperationException("Relay hold control requires a Button.");
            _label = GetComponentInChildren<Text>()
                ?? throw new InvalidOperationException("Relay hold control requires a label.");
            _confirmed = confirmed ?? throw new ArgumentNullException(nameof(confirmed));
            _hold = new RelayHoldInput();
            _enabled = enabled;
            _displayedProgress = -1;
            ActionLabel = string.IsNullOrWhiteSpace(actionLabel)
                ? throw new ArgumentException("A hold action label is required.", nameof(actionLabel))
                : actionLabel;
            _button.interactable = enabled;
            _label.text = enabled
                ? $"{ActionLabel}\nHold to confirm"
                : $"{ActionLabel} UNAVAILABLE";
        }

        private string ActionLabel { get; set; } = "RELAY";

        public void Clear()
        {
            _hold?.Interrupt();
            _confirmed = null;
            _enabled = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_enabled && _button != null && _button.interactable)
            {
                _hold?.PointerDown(eventData.button == PointerEventData.InputButton.Left, Application.isFocused);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _hold?.PointerUp(eventData.button == PointerEventData.InputButton.Left);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            CancelHold();
        }

        public void OnDeselect(BaseEventData eventData)
        {
            CancelHold();
        }

        private void OnApplicationFocus(bool focused) { if (!focused) CancelHold(); }
        private void OnApplicationPause(bool paused) { if (paused) CancelHold(); }
        private void OnDisable() => CancelHold();

        private void Update()
        {
            if (!_enabled || _button == null || !_button.interactable || _hold is null)
            {
                return;
            }

            var selected = EventSystem.current?.currentSelectedGameObject == gameObject;
            if (_hold.Advance(Time.unscaledDeltaTime, Application.isFocused, selected, IsSubmitHeld()))
            {
                _enabled = false;
                _button.interactable = false;
                if (_label != null)
                {
                    _label.text = $"{ActionLabel} CONFIRMED";
                }

                var confirmed = _confirmed;
                _confirmed = null;
                confirmed?.Invoke();
                return;
            }

            var progress = Mathf.RoundToInt((float)(_hold.Progress * 100d));
            if (_label != null && progress != _displayedProgress)
            {
                _displayedProgress = progress;
                _label.text = progress == 0 ? $"{ActionLabel}\nHold to confirm" : $"{ActionLabel}\nHold {progress}%";
            }
        }

        private void CancelHold()
        {
            _hold?.Interrupt();
            _displayedProgress = -1;
            if (_label != null && _enabled)
            {
                _label.text = $"{ActionLabel}\nHold to confirm";
            }
        }

        private static bool IsSubmitHeld()
        {
            try
            {
                return Input.GetButton("Submit") ||
                    Input.GetKey(KeyCode.Return) ||
                    Input.GetKey(KeyCode.KeypadEnter) ||
                    Input.GetKey(KeyCode.Space);
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class OverlayCancelHandler : MonoBehaviour, ICancelHandler
    {
        public Action? Handler { get; set; }

        public void OnCancel(BaseEventData eventData) => Handler?.Invoke();
    }
}
