using BepInEx;
using Comfort.Common;
using ContrabandCases.Client.Configuration;
using ContrabandCases.Client.Opening;
using ContrabandCases.Client.Patches;
using ContrabandCases.Shared;
using EFT.Communications;
using HarmonyLib;
using UnityEngine;

namespace ContrabandCases.Client;

[BepInPlugin(ModConstants.ModId, ModConstants.ModName, ModConstants.ModVersion)]
[BepInProcess("EscapeFromTarkov.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private Harmony? _harmony;
    private RouletteController? _controller;
    private TestingModeConfig? _testing;
    private TestingInventoryGrantConfig? _testingGrants;
    private LibraryConfig? _library;
    private UI.BrokerLauncher? _brokerLauncher;
    private EFT.UI.MenuScreen? _brokerMenu;
    private RectTransform[] _brokerMenuObstacles = [];
    private readonly TestingInventoryGrantDispatcher _testingGrantDispatcher = new();

    private void Awake()
    {
        try
        {
            var modRoot = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "SPT_Runtime",
                "user",
                "mods",
                ModConstants.RuntimeModFolder);
            var configRoot = Path.Combine(modRoot, "config");
            IconCacheReset.RunOnce(configRoot, Logger);
            var presentation = LoadPresentationConfig(Path.Combine(configRoot, "config.jsonc"));
            presentation.BindPlayerSettings(Config);
            _testingGrants = TestingInventoryGrantConfig.Bind(Config);
            _testing = TestingModeConfig.Bind(Config);
            _library = new LibraryConfig(Config, () => _testing?.Enabled.Value == true);
            _brokerLauncher = new UI.BrokerLauncher(() => { if (_library is not null) _library.OpenDossier.Value = true; });

            _harmony = new Harmony(ModConstants.ModId);
            _controller = new RouletteController(
                this,
                Logger,
                presentation,
                Path.Combine(configRoot, "rewards.json"),
                () => _testing?.LifecycleDiagnostics.Value == true);
            UnpackItemPatch.Configure(_controller, Logger);
            InventoryScreenClosePatch.Configure(_controller, Logger);
            _ = new UnpackItemPatch(_harmony, Logger).Enable();
            _ = new InventoryScreenClosePatch(_harmony, Logger).Enable();
            Logger.LogInfo($"{ModConstants.ModName} {ModConstants.ModVersion} loaded; real openings use authoritative Manifest snapshots, while the local catalog is cosmetic/legacy only.");
        }
        catch
        {
            Shutdown();
            enabled = false;
            throw;
        }
    }

    private void Update()
    {
        ProcessCosmeticSelfTest();
        ProcessTestingInventoryGrant();
        ProcessLibrary();
        try
        {
            UpdateBrokerLauncher();
        }
        catch (Exception exception)
        {
            // Fail once, not every frame. MCM remains an independent entry point.
            var launcher = _brokerLauncher;
            _brokerLauncher = null;
            try { launcher?.Dispose(); }
            catch (Exception cleanup) { Logger.LogWarning($"Broker button cleanup failed: {cleanup.Message}"); }
            Logger.LogWarning($"Broker button unavailable; use Broker in MCM: {exception}");
        }
    }

    private void UpdateBrokerLauncher()
    {
        if (_brokerLauncher is null) return;
        if (_library?.ShowBrokerButton.Value != true)
        {
            _brokerLauncher.SetVisible(false);
            return;
        }
        var menu = MonoBehaviourSingleton<EFT.UI.CommonUI>.Instantiated
            ? MonoBehaviourSingleton<EFT.UI.CommonUI>.Instance.MenuScreen : null;
        var mainMenuActive = menu != null && menu.isActiveAndEnabled &&
            EFT.UI.Screens.EftScreenManager._instance?.CurrentBaseScreenController is
                EFT.UI.MenuScreen.MainMenuScreenController { Closed: false } controller && controller.Screen == menu;
        if (!UI.BrokerLauncherPlacement.ShouldShow(_library?.ShowBrokerButton.Value == true,
                _controller?.CanOpenBroker == true, mainMenuActive))
        {
            _brokerLauncher?.SetVisible(false);
            return;
        }
        if (_brokerMenu != menu)
        {
            _brokerMenu = menu;
            // Cache the native controls, not their positions: bounds follow layout/scale changes.
            _brokerMenuObstacles = menu!.GetComponentsInChildren<EFT.UI.ButtonFeedback>(true)
                .Select(button => button.transform).Concat(menu.GetComponentsInChildren<UnityEngine.UI.Selectable>(true)
                    .Select(button => button.transform)).OfType<RectTransform>().Distinct().ToArray();
        }
        _brokerLauncher?.SetVisible(true, menu!.transform as RectTransform,
            menu._tradeButton != null ? menu._tradeButton.transform as RectTransform : null, _brokerMenuObstacles);
    }

    private void ProcessLibrary()
    {
        var settings = _library;
        if (settings is null) return;
        try
        {
            // The native shortcut checks exact modifiers and GetKeyDown, so holding it
            // does not repeat. Do not persist/queue a hotkey as an MCM action flag.
            var shortcut = false;
            if (Application.isFocused && settings.OpenBrokerShortcut.Value.IsDown())
            {
                var current = EFT.UI.Screens.EftScreenManager._instance?.CurrentBaseScreenController;
                var menuOrStash = current switch
                {
                    EFT.UI.MenuScreen.MainMenuScreenController { Closed: false } menu =>
                        menu.Screen != null && menu.Screen.isActiveAndEnabled,
                    EFT.UI.InventoryScreen.InventoryScreenController { Closed: false } stash =>
                        stash.Screen != null && stash.Screen.isActiveAndEnabled,
                    _ => false
                };
                var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                var editing = selected != null && selected.GetComponent<UnityEngine.UI.InputField>()?.isFocused == true;
                shortcut = BrokerShortcutInput.ShouldOpen(true, Application.isFocused,
                    _controller?.CanOpenBroker == true, menuOrStash, editing || IsConfigurationMenuOpen());
            }
            var dossier = settings.OpenDossier.Value || shortcut;
            if (!dossier && !settings.OpenGallery.Value) return;
            var gallery = settings.OpenGallery.Value && !dossier;
            settings.OpenDossier.Value = false;
            settings.OpenGallery.Value = false;
            if (gallery && _testing?.Enabled.Value != true)
            {
                NotificationManager.DisplayWarningNotification("Turn on Enable previews under Animation preview in MCM before opening the catalog preview.");
                return;
            }
            if (_controller?.TryOpenLibrary(gallery, settings) != true)
                NotificationManager.DisplayWarningNotification("Return to the main menu or stash and close other case windows before opening broker records.");
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Read-only broker records could not open: {exception}");
        }
    }

    private static bool IsConfigurationMenuOpen()
    {
        if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(
                "com.bepis.bepinex.configurationmanager", out var manager) || manager.Instance == null) return false;
        // Optional native public contract: no hard ConfigurationManager dependency.
        // An unrecognized manager blocks only the shortcut; the MCM action still works.
        return manager.Instance.GetType().GetProperty("DisplayingWindow")?.GetValue(manager.Instance) is not false;
    }

    private void ProcessCosmeticSelfTest()
    {
        var testing = _testing;
        if (testing is null)
        {
            return;
        }

        try
        {
            var requested = testing.RunSelfTest.Value;
            var request = requested
                ? new CosmeticSelfTestRequest(
                    testing.Enabled.Value,
                    testing.Outcome.Value,
                    testing.AnimationSeconds.Value)
                : default;
            Func<CosmeticSelfTestRequest, CosmeticSelfTestLaunchResult>? launch = null;
            if (_controller is { } controller)
            {
                launch = selfTest =>
                {
                    var started = controller.TryStartCosmeticSelfTest(
                        selfTest.TestingMode,
                        selfTest.Selection,
                        selfTest.DurationSeconds,
                        out var message);
                    return new CosmeticSelfTestLaunchResult(started, message);
                };
            }

            var result = CosmeticSelfTestPolicy.ProcessTrigger(
                requested,
                () => testing.RunSelfTest.Value = false,
                request,
                launch);
            if (result is null)
            {
                return;
            }

            if (result.Value.Started)
            {
                Logger.LogInfo(result.Value.Message);
                return;
            }

            Logger.LogWarning(result.Value.Message);
            NotificationManager.DisplayWarningNotification(result.Value.Message);
        }
        catch (Exception exception)
        {
            Logger.LogError($"Contraband Cases cosmetic self-test trigger recovered from an error: {exception}");
            try
            {
                NotificationManager.DisplayWarningNotification(
                    "The cosmetic roulette self-test could not start. Check the BepInEx log.");
            }
            catch
            {
                // The testing control must never destabilize the Tarkov UI when notifications are unavailable.
            }
        }
    }

    private void ProcessTestingInventoryGrant()
    {
        var testing = _testingGrants;
        if (testing is null || !testing.GrantNow.Value)
        {
            return;
        }

        // Consume the one-shot trigger before any validation or dispatch so MCM
        // cannot resend a rejected or slow request every frame.
        testing.GrantNow.Value = false;
        var caseCount = testing.CaseCount.Value;
        var keyCount = testing.KeyCount.Value;
        try
        {
            var crateType = testing.ResolveSelection();
            var started = _testingGrantDispatcher.TryDispatch(
                testing.Enabled.Value,
                _controller?.IsPresentationBusy == true,
                caseCount,
                keyCount,
                crateType,
                result => OnTestingInventoryGrantCompleted(caseCount, keyCount, result),
                out var message);
            if (started)
            {
                Logger.LogInfo(message);
                NotificationManager.DisplayMessageNotification(message);
                return;
            }

            Logger.LogWarning(message);
            NotificationManager.DisplayWarningNotification(message);
        }
        catch (ArgumentException exception)
        {
            Logger.LogWarning($"Testing selection rejected: {exception.Message}");
            NotificationManager.DisplayWarningNotification(exception.Message);
        }
        catch (Exception exception)
        {
            Logger.LogError($"Contraband Cases testing inventory grant could not be sent: {exception}");
            try
            {
                NotificationManager.DisplayWarningNotification(
                    "The testing inventory grant could not be sent. Check the BepInEx log.");
            }
            catch
            {
                // A testing control must never destabilize the Tarkov UI.
            }
        }
    }

    private void OnTestingInventoryGrantCompleted(int caseCount, int keyCount, IResult result)
    {
        try
        {
            if (result is not null && result.Succeed && !result.Failed)
            {
                var message = $"Granted {caseCount} BR-12 cases and {keyCount} BR-12 keys.";
                Logger.LogInfo(message);
                NotificationManager.DisplayMessageNotification(message);
                return;
            }

            const string rejection =
                "The server rejected the testing grant. Enable server grants, stay in the stash, and make room for the items.";
            Logger.LogWarning(rejection);
            NotificationManager.DisplayWarningNotification(rejection);
        }
        catch (Exception exception)
        {
            Logger.LogError($"Contraband Cases testing grant callback recovered from an error: {exception}");
        }
    }

    private PresentationConfig LoadPresentationConfig(string path)
    {
        try
        {
            return PresentationConfig.Parse(File.ReadAllText(path));
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Using safe presentation defaults because config could not be loaded: {exception.Message}");
            return PresentationConfig.Parse("{\"animationDurationSeconds\":4.5,\"reducedMotionDefault\":false,\"debugLogging\":false}");
        }
    }

    private void OnDestroy() => Shutdown();

    private void Shutdown()
    {
        try
        {
            UnpackItemPatch.Clear();
            InventoryScreenClosePatch.Clear();
            _controller?.Dispose();
            _brokerLauncher?.Dispose();
            _brokerLauncher = null;
        }
        catch (Exception exception)
        {
            Logger.LogError($"Contraband Cases teardown recovered from an error: {exception}");
        }
        finally
        {
            UI.LootArtwork.Clear();
            _controller = null;
            _testing = null;
            _testingGrants = null;
            _brokerMenu = null;
            _brokerMenuObstacles = [];
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception exception)
            {
                Logger.LogError($"Contraband Cases could not unpatch its Harmony owner cleanly: {exception}");
            }
            finally
            {
                _harmony = null;
            }
        }
    }
}
