using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Logging;
using ContrabandCases.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;

namespace ContrabandCases.Tests.Client;

// Executes a private, in-memory copy of the real client assembly. Only the native
// Unity/UI/HTTP boundaries are substituted; coordinator guards, callbacks,
// coroutine bodies, snapshot parsing, and the item-operation dispatcher stay real.
internal sealed class ManifestCoordinatorHarness : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly AssemblyLoadContext _context;
    private readonly Func<AssemblyLoadContext, AssemblyName, Assembly?> _resolve;
    private readonly Assembly _client;
    private readonly object _coordinator;
    private readonly object _run;
    private readonly List<(object Handle, IEnumerator Body)> _coroutines = [];
    private readonly Array _inventoryItems;
    private float _frameDelta = 0.001f;
    public Queue<Func<Task<string>>> Responses { get; } = new();
    public List<(string Route, string Json)> Reads { get; } = [];
    public List<object> Operations { get; } = [];
    public List<Delegate> OperationCallbacks { get; } = [];
    public Action? Retry { get; private set; }
    public Action? Close { get; private set; }
    public int ClosedWindows { get; private set; }
    public int Confirmations { get; private set; }
    public bool Busy => (bool)_coordinator.GetType().GetProperty("IsBusy")!.GetValue(_coordinator)!;
    public bool Detached => (bool)Get(_run, "PresentationDetached")!;
    public string Stage => Get(_run, "Stage")!.ToString()!;
    public int Observers => _coroutines.Count;

    public ManifestCoordinatorHarness()
    {
        var gamePath = typeof(ManifestCoordinatorHarness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "GameManagedPath").Value!;
        _context = new AssemblyLoadContext("Manifest coordinator " + _id, isCollectible: true);
        _resolve = (_, name) =>
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default &&
                assembly.GetName().Name == name.Name);
            if (loaded is not null) return loaded;
            var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (!File.Exists(path)) path = Path.Combine(gamePath, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        _context.Resolving += _resolve;
        AssemblyLoadContext.Default.Resolving += _resolve;
        try
        {
        ManifestNativeBoundary.Register(_id, Boundary);
        using var definition = AssemblyDefinition.ReadAssembly(Path.Combine(AppContext.BaseDirectory, "ContrabandCases.Client.dll"));
        Instrument(definition.MainModule);
        using var image = new MemoryStream();
        definition.Write(image);
        image.Position = 0;
        _client = _context.LoadFromStream(image);

        var native = _context.LoadFromAssemblyName(new AssemblyName("Assembly-CSharp"));
        var profile = RuntimeHelpers.GetUninitializedObject(native.GetType("EFT.Profile", true)!);
        profile.GetType().GetField("Id")!.SetValue(profile, "111111111111111111111111");
        var inventoryType = native.GetType("EFT.InventoryLogic.Inventory", true)!;
        profile.GetType().GetField("Inventory")!.SetValue(profile, RuntimeHelpers.GetUninitializedObject(inventoryType));
        var itemType = native.GetType("EFT.InventoryLogic.Item", true)!;
        var item = RuntimeHelpers.GetUninitializedObject(itemType);
        var templateType = native.GetType("EFT.InventoryLogic.ItemTemplate", true)!;
        var template = RuntimeHelpers.GetUninitializedObject(templateType);
        var idField = templateType.GetField("<_id>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        idField.SetValue(template, Activator.CreateInstance(idField.FieldType, ModConstants.KeyTemplateId));
        itemType.GetField("<Template>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(item, template);
        _inventoryItems = Array.CreateInstance(itemType, 1);
        _inventoryItems.SetValue(item, 0);
        var session = DispatchProxy.Create(native.GetType("EFT.IClientSession", true)!, typeof(ManifestSessionProxy));
        ((ManifestSessionProxy)session).InvokeMember = (method, args) => method.Name switch
        {
            "get_Profile" => profile,
            "SendOperationRightNow" => RecordOperation(args!),
            _ => throw new NotSupportedException("Unexpected session boundary: " + method.Name)
        };
        var coordinatorType = _client.GetType("ContrabandCases.Client.Opening.ManifestPresentationCoordinator", true)!;
        var parameters = coordinatorType.GetConstructors().Single().GetParameters();
        object[] dependencies = parameters.Select(parameter => parameter.ParameterType == typeof(ManualLogSource)
            ? (object)new ManualLogSource("Coordinator regression")
            : RuntimeHelpers.GetUninitializedObject(parameter.ParameterType)).ToArray();
        _coordinator = Activator.CreateInstance(coordinatorType, dependencies)!;
        var runType = coordinatorType.GetNestedType("ManifestRun", BindingFlags.NonPublic)!;
        _run = Activator.CreateInstance(runType, 1L, session, profile,
            "222222222222222222222222", ModConstants.CaseTemplateId)!;
        coordinatorType.GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_coordinator, _run);
        coordinatorType.GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_coordinator, 1L);
        Set(_run, "Stage", Enum.Parse(Get(_run, "Stage")!.GetType(), "Confirming"));
        Set(_run, "OpeningOdds", Get(ParseCurrent(CurrentResponse()), "OpeningOdds"));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Confirm() => Invoke("RevalidateNewTicket", _run);
    public void SceneTeardown() => Invoke("HandleSceneTeardown");
    public void CompleteOperation()
    {
        var resultType = _context.LoadFromAssemblyName(new AssemblyName("Comfort"))
            .GetType("Comfort.Common.SuccessfulResult", true)!;
        OperationCallbacks[^1].DynamicInvoke(Activator.CreateInstance(resultType));
    }
    public void ShowPrepared()
    {
        var snapshot = Get(ParseCurrent(CurrentResponse("TicketPreparedSnapshot")), "Snapshot")!;
        Invoke("ShowPreparedRetry", _run, snapshot);
    }

    public void Tick()
    {
        foreach (var entry in _coroutines.ToArray())
            if (_coroutines.Contains(entry) && !entry.Body.MoveNext()) _coroutines.Remove(entry);
    }

    public void TickUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            Tick();
            Thread.Sleep(1); // Let the real async JSON parser's continuation run.
        }
        Xunit.Assert.True(condition(), $"Coordinator stalled: stage={Stage}, reads={Reads.Count}, observers={Observers}, operations={Operations.Count}.");
    }

    public void AdvancePastReadTimeout()
    {
        _frameDelta = 1f;
        for (var frame = 0; frame < 20; frame++) Tick();
        _frameDelta = 0.001f;
    }

    public static string CurrentResponse(string? snapshotFixture = null, string? catalogId = null)
    {
        // Existing canonical protocol fixtures include the complete wire schema.
        var fixtures = typeof(ManifestClientProtocolTests);
        var odds = (JObject)fixtures.GetMethod("OpeningOdds", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [catalogId ?? new string('c', 64)])!;
        var snapshot = snapshotFixture is null ? null : fixtures.GetMethod(snapshotFixture,
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        return (string)fixtures.GetMethod("CurrentEnvelope", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [snapshot, snapshot is null ? odds : null])!;
    }

    private object ParseCurrent(string json) => _client.GetType("ContrabandCases.Client.Opening.ManifestSnapshotEnvelope")!
        .GetMethod("ParseCurrent")!.Invoke(null, [json])!;

    private object? RecordOperation(object?[] args)
    {
        Operations.Add(args[0]!);
        OperationCallbacks.Add((Delegate)args[1]!);
        return null;
    }

    private object? Boundary(string name, object?[] args)
    {
        switch (name)
        {
            case "PostJson":
                Reads.Add(((string)args[0]!, (string)args[1]!));
                return Responses.Dequeue()();
            case "ShowManifestPending": Retry = null; Close = null; return true;
            case "ShowManifestVerificationError":
                Retry = (Action)args[3]!;
                Close = args.Length > 4 ? args[4] as Action : null;
                return true;
            case "ShowManifestConfirmation":
                Confirmations++;
                Retry = (Action)args[3]!;
                return true;
            case "EndRun": ClosedWindows++; return null;
            case "CompleteSpriteLoading": return null;
            case "StartCoroutine":
                var handle = RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.Coroutine));
                GC.SuppressFinalize(handle);
                _coroutines.Add((handle, (IEnumerator)args[1]!));
                return handle;
            case "StopCoroutine":
                _coroutines.RemoveAll(entry => ReferenceEquals(entry.Handle, args[1]));
                return null;
            case "get_unscaledDeltaTime": return _frameDelta;
            case "get_frameCount": return 1;
            case "get_AllRealPlayerItems": return _inventoryItems;
            default: throw new NotSupportedException("Unexpected native boundary: " + name);
        }
    }

    private void Instrument(ModuleDefinition module)
    {
        var bridge = module.ImportReference(typeof(ManifestNativeBoundary).GetMethod(nameof(ManifestNativeBoundary.Invoke))!);
        var shims = new TypeDefinition("ContrabandCases.Tests", "NativeShims",
            Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(shims);
        foreach (var type in AllTypes(module.Types).ToArray())
        {
            foreach (var method in type.Methods.ToArray())
            {
                if (!method.HasBody) continue;
                if ((type.Name == "RouletteOverlay" && method.Name is "ShowManifestPending" or
                    "ShowManifestVerificationError" or "ShowManifestConfirmation" or "EndRun" or "CompleteSpriteLoading") ||
                    (type.Name == "ManifestSnapshotTransport" && method.Name == "PostJson"))
                {
                    Forward(method, method.Name, bridge);
                    continue;
                }
                foreach (var instruction in method.Body.Instructions.ToArray())
                {
                    if (instruction.Operand is not MethodReference call) continue;
                    var native = (call.DeclaringType.FullName == "UnityEngine.MonoBehaviour" &&
                        call.Name is "StartCoroutine" or "StopCoroutine") ||
                        (call.DeclaringType.FullName == "UnityEngine.Time" && call.Name is "get_unscaledDeltaTime" or "get_frameCount") ||
                        (call.DeclaringType.FullName == "EFT.InventoryLogic.Inventory" && call.Name == "get_AllRealPlayerItems");
                    if (!native) continue;
                    var shim = new MethodDefinition("NativeBoundary" + shims.Methods.Count,
                        Mono.Cecil.MethodAttributes.Assembly | Mono.Cecil.MethodAttributes.Static, call.ReturnType);
                    if (call.HasThis) shim.Parameters.Add(new ParameterDefinition(call.DeclaringType));
                    foreach (var parameter in call.Parameters) shim.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                    shims.Methods.Add(shim);
                    Forward(shim, call.Name, bridge);
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = shim;
                }
            }
        }
    }

    private void Forward(MethodDefinition method, string name, MethodReference bridge)
    {
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, _id);
        il.Emit(OpCodes.Ldstr, name);
        var count = method.Parameters.Count + (method.HasThis ? 1 : 0);
        il.Emit(OpCodes.Ldc_I4, count);
        il.Emit(OpCodes.Newarr, method.Module.TypeSystem.Object);
        for (var index = 0; index < count; index++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldarg, index);
            var argumentType = method.HasThis && index == 0 ? method.DeclaringType :
                method.Parameters[index - (method.HasThis ? 1 : 0)].ParameterType;
            if (argumentType.IsValueType) il.Emit(OpCodes.Box, argumentType);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Call, bridge);
        if (method.ReturnType.MetadataType == MetadataType.Void) il.Emit(OpCodes.Pop);
        else if (method.ReturnType.IsValueType) il.Emit(OpCodes.Unbox_Any, method.ReturnType);
        else il.Emit(OpCodes.Castclass, method.ReturnType);
        il.Emit(OpCodes.Ret);
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(type => new[] { type }.Concat(AllTypes(type.NestedTypes)));
    private static object? Get(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target);
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private void Invoke(string name, params object?[] args) => _coordinator.GetType()
        .GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_coordinator, args);

    public void Dispose()
    {
        _coroutines.Clear();
        AssemblyLoadContext.Default.Resolving -= _resolve;
        ManifestNativeBoundary.Remove(_id);
        _context.Unload();
    }
}

// Public because the instrumented copy is a separate assembly; no runtime code
// or game assembly is patched, and each fixture has an isolated dispatch key.
public static class ManifestNativeBoundary
{
    private static readonly ConcurrentDictionary<string, Func<string, object?[], object?>> Boundaries = new();
    internal static void Register(string id, Func<string, object?[], object?> invoke) => Boundaries[id] = invoke;
    internal static void Remove(string id) => Boundaries.TryRemove(id, out _);
    public static object? Invoke(string id, string name, object?[] args) => Boundaries[id](name, args);
}

public class ManifestSessionProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> InvokeMember { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMember(targetMethod!, args);
}
