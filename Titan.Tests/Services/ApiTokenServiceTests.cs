using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.ApiToken;
using Titan.Application.Commands.Platform.ApiToken.Contracts;
using Titan.Application.Queries.Platform.ApiToken;
using Titan.Application.Queries.Platform.ApiToken.Contracts;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Exceptions;
using Titan.Tests.Domain;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class ApiTokenServiceTests
{
    private const string OtherSecret = "ovc_Zz39Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2pQwA";

    private readonly FakeApiTokenRepository _tokens = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FixedClock _clock = new(ApiTokenTests.Now);
    private readonly Guid _tenantId = Guid.NewGuid();

    public ApiTokenServiceTests()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns(_tenantId);
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns("kc-1");
    }

    private Task<CreateApiTokenCommandResult> CreateAsync(string name = "terraform", string secret = ApiTokenTests.Secret, int? days = 90) =>
        new CreateApiTokenCommandHandler(_tokens, _currentUser.Object, _activity, _clock)
            .HandleAsync(new CreateApiTokenCommand(name, secret, days));

    private Task DeleteAsync(Guid id) =>
        new DeleteApiTokenCommandHandler(_tokens, _currentUser.Object, _activity).HandleAsync(new DeleteApiTokenCommand(id));

    private Task<List<ApiTokenQueryResult>> ListAsync() =>
        new ListApiTokensQueryHandler(_tokens, _currentUser.Object).HandleAsync(new ListApiTokensQuery());

    [Fact]
    public async Task Create_ThenList_ShowsTheTokenWithoutItsSecret()
    {
        CreateApiTokenCommandResult created = await CreateAsync(days: 30);

        ApiTokenQueryResult listed = Assert.Single(await ListAsync());
        Assert.Equal(created.ApiTokenId, listed.Id);
        Assert.Equal("terraform", listed.Name);
        Assert.Equal("ovc_q3v9", listed.Prefix);
        Assert.Equal(ApiTokenTests.Now, listed.CreatedAt);
        Assert.Equal(ApiTokenTests.Now.AddDays(30), listed.ExpiresAt);
    }

    [Fact]
    public async Task Create_ActsAsTheCallerInTheCallersTenant()
    {
        await CreateAsync();

        ApiToken saved = Assert.Single(_tokens.Saved);
        Assert.Equal(_tenantId, saved.TenantId);
        Assert.Equal("kc-1", saved.KeycloakUserId);
    }

    [Fact]
    public async Task Create_RecordsTheRequestAndTheOutcome_InTheSameSave()
    {
        CreateApiTokenCommandResult created = await CreateAsync();

        Assert.Equal(["api_token.create_requested", "api_token.created"], _activity.Events.Select(e => e.Action));
        Assert.All(_activity.Events, e => Assert.Equal(created.ApiTokenId, e.ResourceId));
        Assert.All(_activity.Events, e => Assert.DoesNotContain(ApiTokenTests.Secret, e.ResourceName ?? ""));
        Assert.Equal(1, _tokens.SaveCount);
    }

    [Fact]
    public async Task Create_SameName_IsApiTokenNameTaken_AndSavesNothing()
    {
        await CreateAsync("terraform");

        ConflictException refused = await Assert.ThrowsAsync<ConflictException>(() => CreateAsync(" terraform ", OtherSecret));
        Assert.Equal("API_TOKEN_NAME_TAKEN", refused.Code);
        Assert.Single(_tokens.Saved);
    }

    [Fact]
    public async Task Create_SameSecretAgain_IsApiTokenExists()
    {
        await CreateAsync("terraform");

        ConflictException refused = await Assert.ThrowsAsync<ConflictException>(() => CreateAsync("ci"));
        Assert.Equal("API_TOKEN_EXISTS", refused.Code);
        Assert.Single(_tokens.Saved);
    }

    [Fact]
    public async Task Create_TheTwentyFirst_IsRefused()
    {
        for (int i = 0; i < ApiToken.MaxPerTenant; i++)
        {
            _tokens.Saved.Add(ApiToken.Create(_tenantId, "kc-1", 0, $"t{i}", SecretNumber(i), null, ApiTokenTests.Now));
        }

        BusinessException refused = await Assert.ThrowsAsync<BusinessException>(() => CreateAsync());
        Assert.Equal("API_TOKEN_QUOTA_EXCEEDED", refused.Code);
    }

    [Fact]
    public async Task Create_TokensOfAnotherTenant_DoNotCountAgainstTheLimit()
    {
        for (int i = 0; i < ApiToken.MaxPerTenant; i++)
        {
            _tokens.Saved.Add(ApiToken.Create(Guid.NewGuid(), "kc-9", 0, $"t{i}", SecretNumber(i), null, ApiTokenTests.Now));
        }

        await CreateAsync();

        Assert.Single(_tokens.Saved, t => t.TenantId == _tenantId);
    }

    [Fact]
    public async Task Create_WithoutATenant_IsNotAMember()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns((Guid?)null);

        await Assert.ThrowsAsync<ForbiddenException>(() => CreateAsync());
        Assert.Empty(_tokens.Saved);
    }

    [Fact]
    public async Task Delete_RemovesTheToken_AndRecordsBothEvents()
    {
        CreateApiTokenCommandResult created = await CreateAsync();
        _activity.Events.Clear();

        await DeleteAsync(created.ApiTokenId);

        Assert.Empty(_tokens.Saved);
        Assert.Empty(await ListAsync());
        Assert.Equal(["api_token.delete_requested", "api_token.deleted"], _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Delete_Again_IsApiTokenNotFound()
    {
        CreateApiTokenCommandResult created = await CreateAsync();
        await DeleteAsync(created.ApiTokenId);

        NotFoundException refused = await Assert.ThrowsAsync<NotFoundException>(() => DeleteAsync(created.ApiTokenId));
        Assert.Equal("API_TOKEN_NOT_FOUND", refused.Code);
    }

    [Fact]
    public async Task Delete_ATokenOfAnotherTenant_IsApiTokenNotFound_AndKeepsIt()
    {
        ApiToken foreign = ApiToken.Create(Guid.NewGuid(), "kc-9", 0, "theirs", OtherSecret, null, ApiTokenTests.Now);
        _tokens.Saved.Add(foreign);

        NotFoundException refused = await Assert.ThrowsAsync<NotFoundException>(() => DeleteAsync(foreign.Id));
        Assert.Equal("API_TOKEN_NOT_FOUND", refused.Code);
        Assert.Single(_tokens.Saved);
    }

    [Fact]
    public async Task List_ShowsOnlyTheCallersTenant()
    {
        _tokens.Saved.Add(ApiToken.Create(Guid.NewGuid(), "kc-9", 0, "theirs", OtherSecret, null, ApiTokenTests.Now));
        await CreateAsync();

        ApiTokenQueryResult listed = Assert.Single(await ListAsync());
        Assert.Equal("terraform", listed.Name);
    }

    [Fact]
    public void NoContractCarriesASecretOrItsHash()
    {
        Type[] contracts = [typeof(ApiTokenQueryResult), typeof(CreateApiTokenCommandResult)];
        foreach (Type contract in contracts)
        {
            Assert.DoesNotContain(contract.GetProperties(), p => p.Name.Contains("Secret") || p.Name.Contains("Hash"));
        }
    }

    // A distinct well-formed secret per index.
    private static string SecretNumber(int i) => "ovc_" + i.ToString("D2") + ApiTokenTests.Secret[6..];
}
