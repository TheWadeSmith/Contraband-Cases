using BepInEx.Configuration;
using UnityEngine;

namespace ContrabandCases.Client.Configuration;

// Configuration Manager discovers this exact type name and its public fields.
// No runtime dependency on ConfigurationManager.dll is needed; plain BepInEx
// still exposes the original settings if that optional UI is absent.
internal sealed class ConfigurationManagerAttributes
{
    public string Category = "";
    public string DispName = "";
    public int Order;
    public bool IsAdvanced;
    public bool HideDefaultButton;
    public Action<ConfigEntryBase>? CustomDrawer;
}

internal static class McmSettings
{
    internal const string General = "General";
    internal const string Spawning = "Spawn test items";
    internal const string Preview = "Animation preview — no items spent";
    internal const string Advanced = "Advanced tests and diagnostics";

    internal static ConfigDescription Option(string category, string label, int position,
        string description, AcceptableValueBase? range = null, bool advanced = false) =>
        new(description, range, new ConfigurationManagerAttributes
        {
            Category = category, DispName = label, Order = -position, IsAdvanced = advanced
        });

    internal static ConfigEntry<bool> BindAction(ConfigFile config, string section, string key,
        string category, string label, int position, string description,
        Func<string?>? blockedReason = null, bool advanced = false)
    {
        var metadata = new ConfigurationManagerAttributes
        {
            Category = category, DispName = label, Order = -position, IsAdvanced = advanced,
            HideDefaultButton = true,
            CustomDrawer = entry => DrawAction(entry, label, description, blockedReason)
        };
        var action = config.Bind(section, key, false, new ConfigDescription(description, null, metadata));
        // A persisted one-shot request is not consent to run it on the next launch.
        action.Value = false;
        return action;
    }

    private static void DrawAction(ConfigEntryBase entry, string label, string description, Func<string?>? blockedReason)
    {
        var reason = blockedReason?.Invoke();
        var wasEnabled = GUI.enabled;
        try
        {
            GUI.enabled = wasEnabled && reason is null && entry.BoxedValue is false;
            var text = reason ?? (entry.BoxedValue is true ? "Requested…" : label);
            if (GUILayout.Button(new GUIContent(text, reason ?? description), GUILayout.MinHeight(40f)))
                entry.BoxedValue = true;
        }
        finally { GUI.enabled = wasEnabled; }
    }
}
