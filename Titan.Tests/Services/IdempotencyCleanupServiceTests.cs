using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Services;

public class IdempotencyCleanupServiceTests
{
    [Fact]
    public async Task PurgeOnce_PurgesRecordsOlderThanConfiguredTtl()
    {
        var store = new Mock<IIdempotencyStore>();
        DateTimeOffset captured = default;
        store.Setup(s => s.PurgeExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
             .Callback<DateTimeOffset, CancellationToken>((c, _) => captured = c)
             .ReturnsAsync(0);

        var opts = Options.Create(new IdempotencyCleanupOptions { TtlHours = 24 });
        var svc = new IdempotencyCleanupService(store.Object, opts,
            NullLogger<IdempotencyCleanupService>.Instance);

        var lower = DateTimeOffset.UtcNow.AddHours(-24).AddSeconds(-5);
        await svc.PurgeOnceAsync(store.Object, default);
        var upper = DateTimeOffset.UtcNow.AddHours(-24).AddSeconds(5);

        store.Verify(s => s.PurgeExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.InRange(captured, lower, upper); // cutoff == now - TtlHours
    }
}
