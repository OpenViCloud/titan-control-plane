using System.Reflection;
using Titan.Domain.Aggregates.Acceptance;

namespace Titan.Tests.Domain;

// Acceptance is append-only: created via a factory with no public setter, so no code path can rewrite a
// recorded consent (a new version = a new row). Acceptance_HasNoPublicSetters turns red on a public setter.
public class AcceptanceTests
{
    [Fact]
    public void Record_SetsAllFields()
    {
        var before = DateTimeOffset.UtcNow;

        var a = Acceptance.Record("kc-user-1", "terms", "v0-placeholder", "203.0.113.7");

        Assert.NotEqual(Guid.Empty, a.Id);
        Assert.Equal("kc-user-1", a.SubjectId);
        Assert.Equal("terms", a.DocumentKey);
        Assert.Equal("v0-placeholder", a.DocumentVersion);
        Assert.Equal("203.0.113.7", a.SourceIp);
        Assert.True(a.AcceptedAt >= before);
    }

    [Fact]
    public void Record_AllowsNullSourceIp()
    {
        var a = Acceptance.Record("kc-user-1", "terms", "v0-placeholder", sourceIp: null);
        Assert.Null(a.SourceIp);
    }

    [Theory]
    [InlineData("", "terms", "v1")]
    [InlineData("kc-user-1", "", "v1")]
    [InlineData("kc-user-1", "terms", "")]
    public void Record_RejectsBlankRequiredFields(string subject, string key, string version)
    {
        Assert.Throws<ArgumentException>(() => Acceptance.Record(subject, key, version, null));
    }

    [Fact]
    public void Acceptance_HasNoPublicSetters()
    {
        var writable = typeof(Acceptance)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(writable);
    }
}
