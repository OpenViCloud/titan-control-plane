using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The Domain says what went wrong in its own words: a rule refused (BusinessException), a thing missing
// (NotFoundException), a state that moved under the caller (ConflictException, ConcurrencyConflictException). A
// request that is malformed, forbidden or blocked by a system that is down or misconfigured is the Application's or
// the edge's to say, so those types live in Titan.Application.Exceptions (titan-ops
// architecture/http-contract.md#error-envelope).
public class DomainExceptionTests
{
    [Fact]
    public void TheDomain_DeclaresOnlyItsOwnExceptions()
    {
        string[] declared = typeof(BusinessException).Assembly.GetTypes()
            .Where(t => typeof(Exception).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[] { nameof(BusinessException), nameof(ConcurrencyConflictException), nameof(ConflictException), nameof(NotFoundException) },
            declared);
    }
}
