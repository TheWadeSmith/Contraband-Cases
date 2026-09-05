using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using ContrabandCases.Client.Compat;
using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;

namespace ContrabandCases.Client.Patches;

internal sealed class UnpackItemPatch(Harmony harmony, ManualLogSource log) : ModulePatch(harmony, log)
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
        var method = typeof(ItemUiContext).GetMethod(
            nameof(ItemUiContext.UnpackItem),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null,
            types: [typeof(Item)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(ItemUiContext).FullName, nameof(ItemUiContext.UnpackItem));
        if (method.ReturnType != typeof(Task<IResult>))
        {
            throw new InvalidOperationException("ItemUiContext.UnpackItem has an unexpected return type.");
        }

        return method;
    }

    [PatchPrefix]
    private static bool Prefix(ItemUiContext __instance, Item targetItem, ref Task<IResult> __result)
    {
        if (targetItem is null ||
            !CaseContracts.IsCase(targetItem.StringTemplateId))
        {
            return true;
        }

        try
        {
            RouletteController? controller;
            lock (Sync)
            {
                controller = _controller;
            }

            if (controller is null)
            {
                const string message = "Contraband Cases is not ready.";
                TryNotify(message);
                __result = new FailedResult(message).Task;
            }
            else
            {
                __result = controller.OpenAsync(__instance, targetItem);
            }
        }
        catch (Exception exception)
        {
            _log?.LogError($"Contraband Cases rejected opening before dispatch: {exception}");
            const string message = "Contraband Cases could not start the opening.";
            TryNotify(message);
            __result = new FailedResult(message).Task;
        }

        return false;
    }

    private static void TryNotify(string message)
    {
        try
        {
            NotificationManager.DisplayWarningNotification(message);
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Contraband Cases could not show Tarkov's fallback notification: {exception.Message}");
        }
    }
}
