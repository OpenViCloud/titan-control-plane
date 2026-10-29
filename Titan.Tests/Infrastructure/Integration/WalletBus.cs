using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Abstractions;
using Titan.Infrastructure.Messaging;

namespace Titan.Tests.Infrastructure.Integration;

// The publisher Program.cs registers when NATS is off: the mutation stays durable and the sweeper drives it.
// Whether a handler notifies is asserted in unit tests (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
internal static class WalletBus
{
    public static IWalletMutationPublisher None() =>
        new NullWalletMutationPublisher(NullLogger<NullWalletMutationPublisher>.Instance);
}
