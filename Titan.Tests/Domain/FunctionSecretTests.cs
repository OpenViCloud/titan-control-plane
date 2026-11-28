using Titan.Domain.Aggregates.FunctionSecret;

namespace Titan.Tests.Domain;

// A secret's versions (titan-ops features/ovc-function/design.md#function-secrets): numbered from 1 in the order the
// values came, the KeptVersions newest kept plus every number a kept function version points at, and the name rule of
// an environment variable.
public class FunctionSecretTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static FunctionSecretValue ValueNumbered(int number, int valueBytes = 5) =>
        new(number, "k1", new byte[FunctionSecret.NonceBytes], new byte[valueBytes + FunctionSecret.TagBytes], valueBytes);

    private static FunctionSecret ASecret(string name = "DB_PASSWORD") =>
        FunctionSecret.Create(Tenant, name, ValueNumbered(1), "kc-user");

    private static void AddUpTo(FunctionSecret secret, int last, IReadOnlyCollection<int>? pinned = null)
    {
        while (secret.NextVersionNumber <= last)
        {
            secret.AddVersion(ValueNumbered(secret.NextVersionNumber), "kc-user", pinned ?? []);
        }
    }

    [Fact]
    public void Create_MakesVersionOne_WithTheCiphertextAndWhoGaveIt()
    {
        FunctionSecret secret = ASecret();

        FunctionSecretVersion version = Assert.Single(secret.Versions);
        Assert.Equal((secret.Id, 1, "k1", 5, "kc-user"), (version.SecretId, version.Number, version.KeyId, version.ValueBytes, version.CreatedBy));
        Assert.Equal(FunctionSecret.NonceBytes, version.Nonce.Length);
        Assert.Equal(1, secret.LatestVersionNumber);
        Assert.Equal(2, secret.NextVersionNumber);
        Assert.Equal((Tenant, "DB_PASSWORD"), (secret.TenantId, secret.Name));
    }

    [Fact]
    public void AddVersion_NumbersAfterTheNewest()
    {
        FunctionSecret secret = ASecret();

        FunctionSecretVersion second = secret.AddVersion(ValueNumbered(2), "kc-other", []);
        FunctionSecretVersion third = secret.AddVersion(ValueNumbered(3), "kc-other", []);

        Assert.Equal([1, 2, 3], secret.Versions.Select(v => v.Number).Order());
        Assert.Equal((2, 3), (second.Number, third.Number));
        Assert.Equal(3, secret.LatestVersionNumber);
        Assert.Equal("kc-other", third.CreatedBy);
    }

    // The number is part of the associated data the value was encrypted under: a value encrypted for another number
    // would never decrypt as this one.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AddVersion_OfAValueEncryptedForAnotherNumber_IsRefused(int number)
    {
        FunctionSecret secret = ASecret();

        Assert.Throws<ArgumentException>(() => secret.AddVersion(ValueNumbered(number), "kc-user", []));
        Assert.Single(secret.Versions);
    }

    [Fact]
    public void AddVersion_KeepsTheTenNewest()
    {
        FunctionSecret secret = ASecret();

        AddUpTo(secret, 12);

        Assert.Equal(Enumerable.Range(3, FunctionSecret.KeptVersions), secret.Versions.Select(v => v.Number).Order());
        Assert.Null(secret.FindVersion(2));
        Assert.Equal(13, secret.NextVersionNumber);
    }

    [Fact]
    public void AddVersion_KeepsAnOlderVersion_AKeptFunctionVersionPointsAt()
    {
        FunctionSecret secret = ASecret();

        AddUpTo(secret, 12, pinned: [1, 2]);

        Assert.Equal([1, 2, .. Enumerable.Range(3, FunctionSecret.KeptVersions)], secret.Versions.Select(v => v.Number).Order());

        // Unpinned later, it goes with the next value.
        secret.AddVersion(ValueNumbered(13), "kc-user", [2]);
        Assert.Equal([2, .. Enumerable.Range(4, FunctionSecret.KeptVersions)], secret.Versions.Select(v => v.Number).Order());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(FunctionSecret.MaxValueBytes + 1)]
    public void AValueOutsideItsSize_IsRefused(int valueBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FunctionSecret.Create(Tenant, "DB_PASSWORD", ValueNumbered(1, valueBytes), "kc-user"));
    }

    [Fact]
    public void ANonceThatIsNotTwelveBytes_OrACiphertextWithoutItsTag_IsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            FunctionSecret.Create(Tenant, "A", new FunctionSecretValue(1, "k1", new byte[8], new byte[20], 4), "kc-user"));
        Assert.Throws<ArgumentException>(() =>
            FunctionSecret.Create(Tenant, "A", new FunctionSecretValue(1, "k1", new byte[12], new byte[15], 4), "kc-user"));
    }

    [Theory]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("_token", true)]
    [InlineData("a1", true)]
    [InlineData("1abc", false)]
    [InlineData("db-password", false)]
    [InlineData("db password", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheName_IsAnEnvironmentVariablesName(string? name, bool valid)
    {
        Assert.Equal(valid, FunctionSecret.IsAName(name));
    }

    [Fact]
    public void TheName_IsAtMost128Characters()
    {
        Assert.True(FunctionSecret.IsAName(new string('A', 128)));
        Assert.False(FunctionSecret.IsAName(new string('A', 129)));
    }

    [Fact]
    public void TheLimits_AreTheDesigns()
    {
        Assert.Equal((50, 4096, 10), (FunctionSecret.MaxPerTenant, FunctionSecret.MaxValueBytes, FunctionSecret.KeptVersions));
    }
}
