using System.Reflection;
using BepInEx.Logging;
using ContrabandCases.Client.Compat;
using ContrabandCases.Client.Opening;
using EFT.UI;
using HarmonyLib;

namespace ContrabandCases.Client.Patches;

internal sealed class InventoryScreenClosePatch(Harmony harmony, ManualLogSource log) : ModulePatch(harmony, log)
{
    private static readonly object Sync = new();
    private static RouletteController? _controller;
    private static ManualLogSource? _log;

    public static void Configure(RouletteController controller, ManualLogSource log)
    {
        if (controller is null)
        {
            throw new ArgumentNullException(nameof(controller));
        }

        if (log is null)
        {
            throw new ArgumentNullException(nameof(log));
        }

        lock (Sync)
        {
            _controller = controller;
            _log = log;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _controller = null;
            _log = null;
        }
    }

    protected override MethodBase GetTargetMethod()
    {
        var controllerType = typeof(InventoryScreen.InventoryScreenController);
        var method = controllerType.GetMethod(
            nameof(InventoryScreen.InventoryScreenController.CloseAction),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null,
            types: [typeof(bool)],
            modifiers: null)
            ?? throw new MissingMethodException(
                controllerType.FullName,
                nameof(InventoryScreen.InventoryScreenController.CloseAction));
        if (method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException("InventoryScreenController.CloseAction has an unexpected return type.");
        }

        return method;
    }

    [PatchPrefix]
    private static void Prefix()
    {
        try
        {
            RouletteController? controller;
            lock (Sync)
            {
                controller = _controller;
            }

            controller?.HandleInventoryScreenClosing();
        }
        catch (Exception exception)
        {
            _log?.LogError($"Contraband Cases inventory-screen teardown signal recovered from an error: {exception}");
        }
    }
}
