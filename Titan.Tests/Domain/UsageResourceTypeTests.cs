using Titan.Domain.Aggregates.UsageRecord;

namespace Titan.Tests.Domain;

// The enum's DB tokens are the ONE thing that must never drift: they key the idempotency index
// and ON CONFLICT, and every existing usage_records/invoice_items row already holds them. ToDbString /
// FromDbString are the single source of truth reused by both EF converters and the raw-SQL insert, so
// they are pinned here directly rather than only through the DB round-trip.
public class UsageResourceTypeTests
{
    [Theory]
    [InlineData(UsageResourceType.Compute, "COMPUTE")]
    [InlineData(UsageResourceType.Disk, "DISK")]
    [InlineData(UsageResourceType.Snapshot, "SNAPSHOT")]
    public void ToDbString_ProducesExactUppercaseToken(UsageResourceType type, string expected)
    {
        Assert.Equal(expected, type.ToDbString());
    }

    [Theory]
    [InlineData("COMPUTE", UsageResourceType.Compute)]
    [InlineData("DISK", UsageResourceType.Disk)]
    [InlineData("SNAPSHOT", UsageResourceType.Snapshot)]
    public void FromDbString_ParsesExactUppercaseToken(string token, UsageResourceType expected)
    {
        Assert.Equal(expected, UsageResourceTypeExtensions.FromDbString(token));
    }

    // Exhaustiveness: every enum member has a token AND round-trips. A member added without a mapping
    // arm throws in ToDbString (the `_ =>` guard), so this fails rather than silently persisting a
    // wrong token.
    [Fact]
    public void EveryMember_RoundTrips()
    {
        foreach (var type in Enum.GetValues<UsageResourceType>())
        {
            Assert.Equal(type, UsageResourceTypeExtensions.FromDbString(type.ToDbString()));
        }
    }

    // The stored token is uppercase, NOT the PascalCase member name — this is exactly what a bare
    // HasConversion<string>() would get wrong (it would store "Compute").
    [Fact]
    public void ToDbString_IsNotTheMemberName()
    {
        Assert.NotEqual(UsageResourceType.Compute.ToString(), UsageResourceType.Compute.ToDbString());
        Assert.Equal("COMPUTE", UsageResourceType.Compute.ToDbString());
    }

    [Fact]
    public void FromDbString_RejectsUnknownToken()
    {
        Assert.Throws<ArgumentException>(() => UsageResourceTypeExtensions.FromDbString("compute"));
        Assert.Throws<ArgumentException>(() => UsageResourceTypeExtensions.FromDbString("NETWORK"));
    }
}
