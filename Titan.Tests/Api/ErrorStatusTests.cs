using System.Reflection;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Endpoints;

namespace Titan.Tests.Api;

// The exception → HTTP table behind every error body. Silent about the host wiring itself: that
// Program.cs routes UseExceptionHandler through ErrorStatus.For is read, not run, here.
public class ErrorStatusTests
{
    public static TheoryData<Exception, int> Mapped => new()
    {
        { new NotFoundException("X", "m"), 404 },
        { new ConflictException("X", "m"), 409 },
        { new BusinessException("X", "m"), 422 },
        { new ForbiddenException("X", "m"), 403 },
        { new BadRequestException("X", "m"), 400 },
        { new ServiceUnavailableException("X", "m"), 503 },
    };

    [Theory]
    [MemberData(nameof(Mapped))]
    public void ADomainException_KeepsItsCode_AndGetsItsStatus(Exception ex, int expectedStatus)
    {
        ErrorStatus error = ErrorStatus.For(ex);

        Assert.Equal(expectedStatus, error.Status);
        Assert.Equal("X", error.Code);
    }

    [Fact]
    public void AnythingElse_Is500_WithoutLeakingItsType()
    {
        ErrorStatus error = ErrorStatus.For(new InvalidOperationException("boom"));

        Assert.Equal(500, error.Status);
        Assert.Equal("INTERNAL_ERROR", error.Code);
    }

    // Every HTTP caller catches it today; one that forgets must still answer a conflict with its code,
    // not a 500 that reads like a crash.
    [Fact]
    public void ALostConcurrencyRace_ThatEscapesItsCaller_Is409_WithItsCode()
    {
        ErrorStatus error = ErrorStatus.For(new ConcurrencyConflictException("X", "m"));

        Assert.Equal(409, error.Status);
        Assert.Equal("X", error.Code);
    }

    // Cloudflare answers an origin 502 or 504 with its own page, so the body and its code never reach a client
    // (titan-ops architecture/http-contract.md#error-envelope).
    [Fact]
    public void NoDomainException_MapsToAStatusTheEdgeReplaces()
    {
        int[] replacedByTheEdge = [502, 504];
        List<Type> exceptionTypes = typeof(NotFoundException).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(NotFoundException).Namespace)
            .Where(t => typeof(Exception).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.True(exceptionTypes.Count >= 7, $"the scan found only {exceptionTypes.Count} exception types");

        foreach (Type type in exceptionTypes)
        {
            ConstructorInfo constructor = type.GetConstructors()
                .Single(c => c.GetParameters().Length >= 2);
            object?[] arguments = constructor.GetParameters()
                .Select((parameter, index) => index < 2 ? (object?)"X" : null)
                .ToArray();
            Exception instance = (Exception)constructor.Invoke(arguments);

            ErrorStatus error = ErrorStatus.For(instance);

            Assert.False(replacedByTheEdge.Contains(error.Status), $"{type.Name} maps to {error.Status}");
        }
    }
}
