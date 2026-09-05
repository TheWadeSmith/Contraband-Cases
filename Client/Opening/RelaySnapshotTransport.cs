using System.Reflection;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Relay;
using Newtonsoft.Json;

namespace ContrabandCases.Client.Opening;

internal sealed class RelaySnapshotTransport
{
    private static readonly Lazy<MethodInfo> PostJsonAsyncMethod = new(ResolvePostJsonAsync, true);

    public Task<RelayPendingDiscovery> DiscoverPendingAsync(RelayInteractionOrigin origin)
    {
        if (!RelayInteractionPolicy.CanFetchSnapshot(origin))
        {
            throw new InvalidOperationException("Cosmetic previews cannot contact the Relay recovery route.");
        }

        return ParsePendingAsync(
            PostJson(ModConstants.RelayPendingRoute, "{}", "Relay recovery discovery"));
    }

    public Task<RelaySnapshot> FetchAsync(string stakeRootId, RelayInteractionOrigin origin)
    {
        if (!RelayInteractionPolicy.CanFetchSnapshot(origin))
        {
            throw new InvalidOperationException("Cosmetic previews cannot contact the Relay snapshot route.");
        }
        if (!RelaySnapshotEnvelope.IsMongoId(stakeRootId))
        {
            throw new RelaySnapshotException("A valid Relay stake root ID is required.");
        }

        var requestJson = JsonConvert.SerializeObject(new Dictionary<string, string>
        {
            ["stakeRootId"] = stakeRootId
        });
        return ParseAsync(
            PostJson(ModConstants.RelaySnapshotRoute, requestJson, "Relay snapshot"),
            stakeRootId);
    }

    private static Task<string> PostJson(string route, string requestJson, string operation)
    {
        try
        {
            return PostJsonAsyncMethod.Value.Invoke(
                    null,
                    new object[] { route, requestJson }) as Task<string>
                ?? throw new RelaySnapshotException($"SPT's {operation} request did not return a string task.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new RelaySnapshotException($"SPT could not start the {operation} request.", exception.InnerException);
        }
        catch (RelaySnapshotException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RelaySnapshotException($"SPT's {operation} transport is unavailable.", exception);
        }
    }

    private static async Task<RelaySnapshot> ParseAsync(Task<string> responseTask, string stakeRootId)
    {
        string response;
        try
        {
            response = await responseTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new RelaySnapshotException("The Relay snapshot request failed.", exception);
        }

        return RelaySnapshotEnvelope.Parse(response, stakeRootId);
    }

    private static async Task<RelayPendingDiscovery> ParsePendingAsync(Task<string> responseTask)
    {
        string response;
        try
        {
            response = await responseTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new RelaySnapshotException("The Relay recovery discovery request failed.", exception);
        }

        return RelayPendingDiscoveryEnvelope.Parse(response);
    }

    private static MethodInfo ResolvePostJsonAsync()
    {
        var requestHandler = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => string.Equals(
                assembly.GetName().Name,
                "spt-common",
                StringComparison.OrdinalIgnoreCase))
            .Select(assembly => assembly.GetType("SPT.Common.Http.RequestHandler", throwOnError: false))
            .SingleOrDefault(type => type is not null)
            ?? Type.GetType(
                "SPT.Common.Http.RequestHandler, spt-common",
                throwOnError: false)
            ?? throw new RelaySnapshotException("SPT's authenticated HTTP request handler is not loaded.");
        var methods = requestHandler.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method =>
                string.Equals(method.Name, "PostJsonAsync", StringComparison.Ordinal) &&
                method.ReturnType == typeof(Task<string>) &&
                method.GetParameters() is var parameters &&
                parameters.Length == 2 &&
                parameters.All(parameter => parameter.ParameterType == typeof(string)))
            .ToArray();
        if (methods.Length != 1)
        {
            throw new RelaySnapshotException("SPT exposes no unique supported PostJsonAsync(string, string) method.");
        }

        return methods[0];
    }
}
