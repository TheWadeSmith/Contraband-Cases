using ContrabandCases.Client.Configuration;
using Comfort.Common;
using EFT;

namespace ContrabandCases.Client.Opening;

internal sealed partial class ManifestPresentationCoordinator
{
    internal bool TryOpenLibrary(IClientSession session, Profile profile, bool gallery, LibraryConfig settings)
    {
        if (_disposed || IsBusy) return false;
        var state = settings.State.Value;
        var rarity = settings.Rarity.Value;
        var lotId = settings.LotId.Value.Trim();
        var longName = settings.LongName.Value;
        var missingArt = settings.MissingArtwork.Value;
        var run = new ManifestRun(checked(++_generation), session, profile, string.Empty) { LibraryOnly = true };
        _active = run;
        try
        {
            if (!_overlay.ShowManifestPending("READING BROKER RECORDS", "Read-only catalog and saved history. No items will change.", allowRebuild: true))
            {
                End(run, null, operationPending: false);
                return false;
            }
            Observe(run, _transport.FetchLibraryAsync(), library =>
            {
                RequireProfile(run);
                if (!gallery)
                {
                    run.Stage = ManifestClientStage.Decision;
                    void Close() { if (IsCurrentAt(run, ManifestClientStage.Decision)) End(run, null, operationPending: false); }
                    Action? resume = library.HasPending == false ? null : () => ResumeFromDossier(run);
                    void Home()
                    {
                        NavigateLibrary(run, () => _overlay.ShowBrokerHome(library, () => Browse("", "", "", 0),
                            () => NavigateLibrary(run, () => _overlay.ShowBrokerStatus(library.Status, Home)),
                            () => NavigateLibrary(run, () => _overlay.ShowDossier(library.Dossier, Home, resume)), Close, resume));
                    }
                    void Details(ManifestLotSnapshot lot, string caseId, string family, string search, int page)
                    {
                        NavigateLibrary(run, () =>
                        {
                            if (!_overlay.ShowLibraryPackage(lot, () => Browse(caseId, family, search, page))) return false;
                            BeginSpriteBinding(run, PrepareSpriteLoads(run, ManifestSpritePlan.ForLot(lot)), run.PresentationGeneration);
                            return true;
                        });
                    }
                    void Browse(string caseId, string family, string search, int page)
                    {
                        NavigateLibrary(run, () =>
                        {
                            var filtered = BrokerLibraryFilter.Lots(library, caseId, family, search);
                            var layout = BrokerLibraryLayout.ForScreen(UnityEngine.Screen.width, UnityEngine.Screen.height);
                            var maxPage = layout.LastPage(filtered.Count);
                            page = Math.Max(0, Math.Min(page, maxPage));
                            var visible = filtered.Skip(page * layout.PageSize).Take(layout.PageSize).ToArray();
                            var savedPage = page;
                            if (!_overlay.ShowRewardBrowser(library, caseId, family, search, page, maxPage, layout.Columns,
                                filtered.Count, visible, Browse, lot => Details(lot, caseId, family, search, savedPage), Home)) return false;
                            if (visible.Length > 0)
                            {
                                var tiles = visible.Select((lot, i) => new ManifestTilePresentation($"browser:{i}", lot.DisplayName,
                                    "CATALOG PREVIEW", lot.Grade, lot.AnchorTemplateId));
                                BeginSpriteBinding(run, PrepareSpriteLoads(run, ManifestSpritePlan.Create(tiles, "browser:0")), run.PresentationGeneration);
                            }
                            return true;
                        });
                    }
                    Home();
                    return;
                }
                var lots = library.Lots.Where(lot => ManifestGallery.Matches(lot, lotId, rarity)).ToArray();
                if (lots.Length == 0)
                {
                    Fail(run, "No installed catalog lots match that ID and rarity. Clear the filters or restore the required item mod.", null);
                    return;
                }
                run.Stage = ManifestClientStage.Decision;
                void Show(int index)
                {
                    NavigateLibrary(run, () =>
                    {
                        var lot = ManifestGallery.DisplayLot(lots[index % lots.Length], longName);
                        var preview = ManifestGallery.Create(lot, state, longName: false);
                        var displayedState = lot.ProviderId == ContrabandCases.Shared.Catalog.CashPayouts.Provider
                            ? GalleryState.ReadyToClaim : state;
                        if (!_overlay.ShowGalleryPreview(preview, lot, displayedState, index % lots.Length + 1, lots.Length,
                                () => Show(index + 1), () => End(run, null, operationPending: false)))
                            return false;
                        if (!missingArt)
                        {
                            var batch = PrepareSpriteLoads(run, ManifestSpritePlan.ForLot(lot));
                            BeginSpriteBinding(run, batch, run.PresentationGeneration);
                        }
                        else _overlay.CompleteSpriteLoading(ManifestSpritePlan.ForLot(lot).Requests.SelectMany(r => r.TileIds));
                        return true;
                    });
                }
                Show(0);
            }, exception => Fail(run, "Broker records could not be loaded. No items changed.", exception), "read-only broker library");
            return true;
        }
        catch (Exception exception)
        {
            Fail(run, "Broker records could not be opened. No items changed.", exception);
            return false;
        }
    }

    private void NavigateLibrary(ManifestRun run, Func<bool> show)
    {
        if (!IsCurrentAt(run, ManifestClientStage.Decision) || !run.LibraryOnly) return;
        try
        {
            RequireProfile(run);
            if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Broker records are menu-only.");
            StopSpriteBinding(run);
            run.PresentationGeneration++;
            if (!show()) throw new InvalidOperationException("The Broker window is no longer available.");
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Broker navigation closed safely: {exception}");
            End(run, "Broker records closed. No items changed; reopen Broker to try again.", operationPending: false);
        }
    }

    private void ResumeFromDossier(ManifestRun dossier)
    {
        if (!IsCurrentAt(dossier, ManifestClientStage.Decision) || !dossier.LibraryOnly) return;
        try
        {
            RequireProfile(dossier);
            if (Singleton<GameWorld>.Instantiated)
                throw new InvalidOperationException("Pending payouts can only be resumed outside a raid.");

            // Close the read-only run; gallery/dossier authority never changes in place.
            End(dossier, null, operationPending: false);
            var recovery = new ManifestRun(checked(++_generation), dossier.Session, dossier.Profile, string.Empty)
            {
                RecoveryOnly = true
            };
            _active = recovery;
            if (!_overlay.ShowManifestPending("CHECKING PENDING PAYOUT",
                    "Resuming your saved server state. No new case or key is required.", allowRebuild: true))
                throw new InvalidOperationException("Tarkov's UI event system is unavailable.");
            BeginInitialFetch(recovery);
        }
        catch (Exception exception)
        {
            if (_active is { } active)
                Fail(active, "The pending payout could not be resumed. Its saved server state is unchanged.", exception);
        }
    }
}
