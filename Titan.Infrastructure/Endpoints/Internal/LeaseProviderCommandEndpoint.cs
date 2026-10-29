using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Platform.ProviderCommand;
using Titan.Application.Commands.Platform.ProviderCommand.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker takes work under a lease id it minted, then GETs the same route to read what it got
// (titan-ops architecture/provider-and-worker.md#provider-command-lease). PUT: a repeat with the same lease id is a no-op.
public class LeaseProviderCommandEndpoint(LeaseProviderCommandCommandHandler handler, IConfiguration config)
    : Endpoint<LeaseProviderCommandBody>
{
    public override void Configure()
    {
        Put("internal/provider-commands/leases/{leaseId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(LeaseProviderCommandBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        bool callerIsTheWorker = InternalTokenAuth.Matches(token, config["Internal:Token"]);
        if (!callerIsTheWorker)
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid leaseId = Route<Guid>("leaseId");
        await handler.HandleAsync(new LeaseProviderCommandCommand(leaseId, body.Kinds), ct);
        await SendNoContentAsync(ct);
    }
}

// The kinds this worker can run; LeaseProviderCommandCommand also carries the route's lease id.
public record LeaseProviderCommandBody(List<string> Kinds);

public class LeaseProviderCommandValidator : Validator<LeaseProviderCommandBody>
{
    public LeaseProviderCommandValidator()
    {
        RuleFor(b => b.Kinds).NotEmpty();
    }
}
