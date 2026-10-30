using System.Security.Cryptography;
using System.Text;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

public class ApiTokenTests
{
    // ovc_ + 43 base64url characters = 32 random bytes, the shape the portal makes.
    public const string Secret = "ovc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2pQwA";
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static ApiToken Create(int tokenCount = 0, string name = "terraform", string secret = Secret, int? days = 90) =>
        ApiToken.Create(Guid.NewGuid(), "kc-1", tokenCount, name, secret, days, Now);

    [Fact]
    public void Create_KeepsTheTenantTheCreatorAndTheName_AndOnlyAHashOfTheSecret()
    {
        var tenantId = Guid.NewGuid();

        ApiToken token = ApiToken.Create(tenantId, "kc-1", 0, "  terraform  ", Secret, 90, Now);

        Assert.NotEqual(Guid.Empty, token.Id);
        Assert.Equal(tenantId, token.TenantId);
        Assert.Equal("kc-1", token.KeycloakUserId);
        Assert.Equal("terraform", token.Name);
        Assert.Equal(Now, token.CreatedAt);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Secret))), token.SecretHash);
        Assert.Equal("ovc_q3v9", token.Prefix);
    }

    [Fact]
    public void NoPropertyHoldsTheSecret()
    {
        ApiToken token = Create();

        foreach (var property in typeof(ApiToken).GetProperties())
        {
            Assert.NotEqual(Secret, property.GetValue(token) as string);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public void Create_WithALifetime_ExpiresThatManyDaysLater(int days)
    {
        Assert.Equal(Now.AddDays(days), Create(days: days).ExpiresAt);
    }

    [Fact]
    public void Create_WithoutALifetime_NeverExpires()
    {
        Assert.Null(Create(days: null).ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-30)]
    [InlineData(366)]
    public void Create_AnyOtherLifetime_IsRefused(int days)
    {
        Assert.Equal("INVALID_API_TOKEN",
            Assert.Throws<BadRequestException>(() => Create(days: days)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ovc_")]
    [InlineData("ovc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2pQw")]   // 42 characters
    [InlineData("ovc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2pQwAB")] // 44 characters
    [InlineData("abc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2pQwA")]
    [InlineData("ovc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2p+wA")] // not base64url
    [InlineData("ovc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2p wA")]
    public void Create_MalformedSecret_IsRefused(string secret)
    {
        Assert.Equal("INVALID_API_TOKEN",
            Assert.Throws<BadRequestException>(() => Create(secret: secret)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_IsRefused(string name)
    {
        Assert.Equal("INVALID_API_TOKEN_NAME",
            Assert.Throws<BadRequestException>(() => Create(name: name)).Code);
    }

    [Fact]
    public void Create_NameLongerThan64_IsRefused()
    {
        Assert.Throws<BadRequestException>(() => Create(name: new string('t', 65)));
        Assert.NotNull(Create(name: new string('t', 64)));
    }

    [Fact]
    public void Create_TheTwentyFirstToken_IsRefused_TheTwentiethIsNot()
    {
        Assert.NotNull(Create(tokenCount: ApiToken.MaxPerTenant - 1));
        Assert.Equal("API_TOKEN_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => Create(tokenCount: ApiToken.MaxPerTenant)).Code);
    }

    [Fact]
    public void ATokenWithALifetime_IsUsableUntilItsExpiry_NotAtIt()
    {
        ApiToken token = Create(days: 30);
        DateTimeOffset expiry = token.ExpiresAt!.Value;

        Assert.True(token.IsUsableAt(expiry.AddSeconds(-1)));
        Assert.False(token.IsUsableAt(expiry));
        Assert.False(token.IsUsableAt(expiry.AddDays(1)));
    }

    [Fact]
    public void ATokenWithoutALifetime_IsAlwaysUsable()
    {
        Assert.True(Create(days: null).IsUsableAt(Now.AddYears(50)));
    }

    [Fact]
    public void HashOf_IsTheHashCreateStores()
    {
        Assert.Equal(Create().SecretHash, ApiToken.HashOf(Secret));
        Assert.NotEqual(ApiToken.HashOf(Secret), ApiToken.HashOf(Secret.Replace('A', 'B')));
    }
}
