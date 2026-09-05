using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace ContrabandCases.Client.Compat;

internal abstract class ModulePatch
{
    private readonly Harmony _harmony;
    private readonly ManualLogSource _log;

    protected ModulePatch(Harmony harmony, ManualLogSource log)
    {
        _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected abstract MethodBase GetTargetMethod();

    public MethodBase Enable()
    {
        var target = GetTargetMethod();
        var prefixMethod = GetType()
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.IsDefined(typeof(PatchPrefixAttribute), inherit: false))
            ?? throw new InvalidOperationException($"{GetType().Name} has no unique prefix method.");

        _harmony.Patch(target, prefix: new HarmonyMethod(prefixMethod));
        var patchInfo = Harmony.GetPatchInfo(target);
        if (patchInfo?.Prefixes.Any(patch =>
                string.Equals(patch.owner, _harmony.Id, StringComparison.Ordinal) &&
                patch.PatchMethod == prefixMethod) != true)
        {
            throw new InvalidOperationException($"Harmony did not register {_harmony.Id} on {target.DeclaringType?.FullName}.{target.Name}.");
        }

        _log.LogInfo($"Verified patch owner {_harmony.Id}: {target.DeclaringType?.FullName}.{target.Name}");
        return target;
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PatchPrefixAttribute : Attribute
{
}
