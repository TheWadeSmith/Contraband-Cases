using System.Reflection;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using Newtonsoft.Json;

namespace ContrabandCases.Client.Opening;

internal sealed class ManifestSnapshotTransport
{
    private static readonly Lazy<MethodInfo> PostJsonAsyncMethod = new(ResolvePostJsonAsync, true);

    // Explicit read-only catalog/history browsing. This does not relax the
    // authority policy on recovery or economic routes used by cosmetic tests.
    public async Task<ManifestLibrarySnapshot> FetchLibraryAsync() =>
        ManifestSnapshotParser.ParseLibrary(await PostJson(ModConstants.ManifestLibraryRoute, "{}", "catalog and dossier").ConfigureAwait(false));

    public Task<ManifestCurrentState> FetchCurrentAsync(RelayInteractionOrigin origin,
        string caseTemplateId = ModConstants.CaseTemplateId)
    {
        RequireTransportAuthority(origin);
        return ParseCurrentAsync(PostJson(
            ModConstants.ManifestCurrentRoute,
            JsonConvert.SerializeObject(new { caseTemplateId = CaseContracts.Require(caseTemplateId) }),
            "current Manifest discovery"));
    }

    public Task<ManifestSnapshot> FetchAsync(
        string manifestId,
        RelayInteractionOrigin origin)
    {
        RequireTransportAuthority(origin);
        var requestJson = CreateSnapshotRequestJson(manifestId);
        return ParseRequiredAsync(
            PostJson(
                ModConstants.ManifestSnapshotRoute,
                requestJson,
                "Manifest snapshot"),
            manifestId);
    }

    internal static string CreateSnapshotRequestJson(string manifestId)
    {
        manifestId = ManifestProtocolValidation.RequireIdentifier(
            manifestId,
            nameof(manifestId));
        return JsonConvert.SerializeObject(new Dictionary<string, string>
        {
            ["manifestId"] = manifestId
        });
    }

    private static void RequireTransportAuthority(RelayInteractionOrigin origin)
    {
        if (!RelayInteractionPolicy.CanFetchSnapshot(origin))
        {
            throw new InvalidOperationException(
                "Cosmetic previews cannot contact the Manifest recovery routes.");
        }
    }

    private static Task<string> PostJson(string route, string requestJson, string operation)
    {
        try
        {
            return PostJsonAsyncMethod.Value.Invoke(
                    null,
                    new object[] { route, requestJson }) as Task<string>
                ?? throw new ManifestSnapshotException(
                    $"SPT's {operation} request did not return a string task.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new ManifestSnapshotException(
                $"SPT could not start the {operation} request.",
                exception.InnerException);
        }
        catch (ManifestSnapshotException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ManifestSnapshotException(
                $"SPT's {operation} transport is unavailable.",
                exception);
        }
    }

    private static async Task<ManifestCurrentState> ParseCurrentAsync(Task<string> responseTask)
    {
        string response;
        try
        {
            response = await responseTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ManifestSnapshotException(
                "The current Manifest discovery request failed.",
                exception);
        }

        return ManifestSnapshotEnvelope.ParseCurrent(response);
    }

    private static async Task<ManifestSnapshot> ParseRequiredAsync(
        Task<string> responseTask,
        string expectedManifestId)
    {
        string response;
        try
        {
            response = await responseTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ManifestSnapshotException(
                "The Manifest snapshot request failed.",
                exception);
        }

        return ManifestSnapshotEnvelope.Parse(response, expectedManifestId);
    }

    private static MethodInfo ResolvePostJsonAsync()
    {
        var requestHandler = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => string.Equals(
                assembly.GetName().Name,
                "spt-common",
                StringComparison.OrdinalIgnoreCase))
            .Select(assembly => assembly.GetType(
                "SPT.Common.Http.RequestHandler",
                throwOnError: false))
            .SingleOrDefault(type => type is not null)
            ?? Type.GetType(
                "SPT.Common.Http.RequestHandler, spt-common",
                throwOnError: false)
            ?? throw new ManifestSnapshotException(
                "SPT's authenticated HTTP request handler is not loaded.");
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
            throw new ManifestSnapshotException(
                "SPT exposes no unique supported PostJsonAsync(string, string) method.");
        }

        return methods[0];
    }
}
