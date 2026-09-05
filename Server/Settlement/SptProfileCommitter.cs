using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;

namespace ContrabandCases.Server.Settlement;

[Injectable(InjectionType.Singleton)]
public sealed class SptProfileCommitter(SaveServer saveServer) : IProfileCommitter
{
    public async Task CommitAsync(MongoId profileId, CancellationToken cancellationToken)
    {
        await saveServer.SaveProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
    }
}
