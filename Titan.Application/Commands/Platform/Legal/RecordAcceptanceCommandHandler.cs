using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.Legal.Contracts;
using Titan.Application.Services.Platform.Legal;
using Titan.Domain.Aggregates.Acceptance;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Platform.Legal;

/// Records the caller's acceptance of the current document version. Idempotent — re-accepting a
/// version already accepted is a no-op.
public class RecordAcceptanceCommandHandler(
    ILegalDocumentCatalog catalog,
    IAcceptanceRepository acceptances,
    ICurrentUser currentUser)
{
    public async Task HandleAsync(RecordAcceptanceCommand command, CancellationToken ct = default)
    {
        var sourceIp = command.SourceIp;

        var d = catalog.Current;
        var subjectId = currentUser.KeycloakUserId;

        // Idempotent: already accepted this version = nothing to record. The unique index is the true
        // concurrency guard (two racing clicks); this is the common-case fast path.
        if (await acceptances.ExistsAsync(subjectId, d.Key, d.Version, ct))
            return;

        await acceptances.AddAsync(Acceptance.Record(subjectId, d.Key, d.Version, sourceIp), ct);
    }
}
