using System.Text;
using System.Text.Json;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.FunctionSecret;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Application.Exceptions;
using Titan.Application.Queries.Compute.Function;
using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Application.Queries.Compute.FunctionSecret;
using Titan.Application.Queries.Compute.FunctionSecret.Contracts;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.FunctionSecrets;
using Titan.Tests.Fakes;
using Titan.Tests.Infrastructure.FunctionSecrets;

namespace Titan.Tests.Services;

// Function secrets at the handler (titan-ops features/ovc-function/design.md#function-secrets): create with its quota and
// unique name, a new value as the next version under the row lock, delete refused while a running or deploying version
// reads it, the list that never carries a value, the activity entries, and the worker's read of a version's values. The
// cipher is the real AES-GCM one under a test key, so a test can tell what was stored from what was given.
public class FunctionSecretServiceTests
{
    private const string Value = "p@ss-wörd-0123456789";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FakeFunctionSecretRepository _repo = new();
    private readonly FakeFunctionRepository _functions = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly AesGcmFunctionSecretCipher _cipher = AesGcmFunctionSecretCipherTests.Cipher();

    public FunctionSecretServiceTests()
    {
        _currentUser.Setup(u => u.TenantId).Returns(_tenantId);
        _currentUser.Setup(u => u.KeycloakUserId).Returns("kc-member");
    }

    private CreateFunctionSecretCommandHandler Create() => new(_repo, _cipher, _currentUser.Object, _activity);

    private AddFunctionSecretVersionCommandHandler AddVersion() => new(_repo, _functions, _cipher, _currentUser.Object, _activity);

    private DeleteFunctionSecretCommandHandler Delete() => new(_repo, _functions, _currentUser.Object, _activity);

    private ListFunctionSecretsQueryHandler List() => new(_repo, _functions, _currentUser.Object);

    private GetFunctionSecretValuesQueryHandler Values() => new(_functions, _repo, _cipher);

    private async Task<FunctionSecret> SeedAsync(string name = "DB_PASSWORD", string value = Value)
    {
        await Create().HandleAsync(new CreateFunctionSecretCommand(name, value));
        return _repo.Saved.Single(s => s.Name == name);
    }

    private string Decrypted(FunctionSecret secret, int number)
    {
        FunctionSecretVersion version = secret.FindVersion(number)!;
        return _cipher.Decrypt(secret.TenantId, secret.Name, number, version.KeyId, version.Nonce, version.Ciphertext);
    }

    private Function AFunctionOfTheTenant(string name = "hello")
    {
        Function function = TestFunctions.AFunction(_tenantId, name: name);
        _functions.Rows.Add(function);
        return function;
    }

    // ── create ──

    [Fact]
    public async Task Create_StoresVersionOne_EncryptedForTheTenantNameAndNumber()
    {
        CreateFunctionSecretCommandResult result = await Create().HandleAsync(new CreateFunctionSecretCommand("DB_PASSWORD", Value));

        FunctionSecret secret = Assert.Single(_repo.Saved);
        Assert.Equal(1, result.Version);
        Assert.Equal((_tenantId, "DB_PASSWORD"), (secret.TenantId, secret.Name));
        FunctionSecretVersion version = Assert.Single(secret.Versions);
        Assert.Equal(("k1", Encoding.UTF8.GetByteCount(Value), "kc-member"), (version.KeyId, version.ValueBytes, version.CreatedBy));
        Assert.DoesNotContain(Value, Encoding.UTF8.GetString(version.Ciphertext));
        Assert.Equal(Value, Decrypted(secret, 1));
        Assert.Equal(1, _repo.Saves);
    }

    [Fact]
    public async Task Create_ANameTaken_Is409_AndAddsNothing()
    {
        await SeedAsync();

        var e = await Assert.ThrowsAsync<ConflictException>(() => Create().HandleAsync(new CreateFunctionSecretCommand("DB_PASSWORD", "other")));

        Assert.Equal("FUNCTION_SECRET_EXISTS", e.Code);
        Assert.Single(_repo.Saved);
    }

    [Fact]
    public async Task Create_TheFiftyFirst_IsRefused()
    {
        for (int i = 0; i < FunctionSecret.MaxPerTenant; i++)
        {
            await SeedAsync($"S{i}");
        }

        var e = await Assert.ThrowsAsync<BusinessException>(() => Create().HandleAsync(new CreateFunctionSecretCommand("ONE_MORE", Value)));

        Assert.Equal("FUNCTION_SECRET_QUOTA_EXCEEDED", e.Code);
        Assert.Equal(FunctionSecret.MaxPerTenant, _repo.Saved.Count);
    }

    [Theory]
    [InlineData("db-password", Value)]
    [InlineData("1DB", Value)]
    [InlineData("DB", "")]
    public async Task Create_ANameOrValueOutsideTheRule_Is400(string name, string value)
    {
        var e = await Assert.ThrowsAsync<BadRequestException>(() => Create().HandleAsync(new CreateFunctionSecretCommand(name, value)));

        Assert.Equal("VALIDATION_ERROR", e.Code);
        Assert.Empty(_repo.Saved);
    }

    [Fact]
    public async Task Create_AValueOverFourKiB_Is400_CountedInUtf8()
    {
        string over = new('é', FunctionSecret.MaxValueBytes / 2 + 1);

        var e = await Assert.ThrowsAsync<BadRequestException>(() => Create().HandleAsync(new CreateFunctionSecretCommand("DB", over)));

        Assert.Equal("VALIDATION_ERROR", e.Code);
        await Create().HandleAsync(new CreateFunctionSecretCommand("DB", new string('é', FunctionSecret.MaxValueBytes / 2)));
    }

    [Fact]
    public async Task Create_RecordsTheRequestAndTheOutcome_WithoutTheValue()
    {
        FunctionSecret secret = await SeedAsync();

        Assert.Equal(["function_secret.create_requested", "function_secret.created"], _activity.Events.Select(e => e.Action));
        Assert.All(_activity.Events, e => Assert.Equal(("function_secret", secret.Id, "DB_PASSWORD"), (e.ResourceType, e.ResourceId, e.ResourceName)));
        Assert.DoesNotContain(_activity.Events, e => e.ToString().Contains(Value));
    }

    [Fact]
    public void TheCommands_NeverPrintTheValue()
    {
        Assert.DoesNotContain(Value, new CreateFunctionSecretCommand("DB", Value).ToString());
        Assert.DoesNotContain(Value, new AddFunctionSecretVersionCommand("DB", Value).ToString());
        Assert.Contains("DB", new CreateFunctionSecretCommand("DB", Value).ToString());
    }

    // ── a new value ──

    [Fact]
    public async Task AddVersion_IsTheNextNumber_UnderTheRowLock()
    {
        FunctionSecret secret = await SeedAsync();

        AddFunctionSecretVersionCommandResult result = await AddVersion().HandleAsync(new AddFunctionSecretVersionCommand("DB_PASSWORD", "v2"));

        Assert.Equal(2, result.Version);
        Assert.Equal(["DB_PASSWORD"], _repo.Locked);
        Assert.Equal("v2", Decrypted(secret, 2));
        Assert.Equal(Value, Decrypted(secret, 1));
        Assert.Equal(["function_secret.version_add_requested", "function_secret.version_added"],
            _activity.Events.Skip(2).Select(e => e.Action));
    }

    [Fact]
    public async Task AddVersion_OfAnUnknownName_OrAnotherTenantsSecret_Is404()
    {
        await SeedAsync();
        _currentUser.Setup(u => u.TenantId).Returns(Guid.NewGuid());

        var e = await Assert.ThrowsAsync<NotFoundException>(() => AddVersion().HandleAsync(new AddFunctionSecretVersionCommand("DB_PASSWORD", "v2")));
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => AddVersion().HandleAsync(new AddFunctionSecretVersionCommand("NOPE", "v2")));

        Assert.Equal(("FUNCTION_SECRET_NOT_FOUND", "FUNCTION_SECRET_NOT_FOUND"), (e.Code, unknown.Code));
    }

    [Fact]
    public async Task AddVersion_KeepsAnOldNumber_AKeptFunctionVersionPoints_Beyond_TheTenNewest()
    {
        FunctionSecret secret = await SeedAsync();
        Function function = AFunctionOfTheTenant();
        function.WithSecrets(FunctionSecretRef.Of("DB", secret.Id, secret.Name, 1));

        for (int i = 2; i <= 12; i++)
        {
            await AddVersion().HandleAsync(new AddFunctionSecretVersionCommand("DB_PASSWORD", $"v{i}"));
        }

        Assert.Equal([1, .. Enumerable.Range(3, FunctionSecret.KeptVersions)], secret.Versions.Select(v => v.Number).Order());
        Assert.Equal(Value, Decrypted(secret, 1));
    }

    // ── delete ──

    [Fact]
    public async Task Delete_RemovesTheSecret_AndRecordsIt()
    {
        FunctionSecret secret = await SeedAsync();

        await Delete().HandleAsync(new DeleteFunctionSecretCommand("DB_PASSWORD"));

        Assert.Empty(_repo.Saved);
        Assert.Equal(["function_secret.delete_requested", "function_secret.deleted"], _activity.Events.Skip(2).Select(e => e.Action));
        Assert.Equal(secret.Id, _activity.Events.Last().ResourceId);
        var again = await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(new DeleteFunctionSecretCommand("DB_PASSWORD")));
        Assert.Equal("FUNCTION_SECRET_NOT_FOUND", again.Code);
    }

    [Fact]
    public async Task Delete_WhileARunningOrDeployingVersionReadsIt_Is409_NamingTheFunctions()
    {
        FunctionSecret secret = await SeedAsync();
        Function running = AFunctionOfTheTenant("api");
        running.Running(running.WithSecrets(FunctionSecretRef.Of("DB", secret.Id, secret.Name, 1)));
        Function deploying = AFunctionOfTheTenant("worker");
        deploying.Deploy(deploying.WithSecrets(FunctionSecretRef.Of("DB", secret.Id, secret.Name, 1)).Id);

        var e = await Assert.ThrowsAsync<ConflictException>(() => Delete().HandleAsync(new DeleteFunctionSecretCommand("DB_PASSWORD")));

        Assert.Equal("FUNCTION_SECRET_IN_USE", e.Code);
        Assert.Contains("api", e.Message);
        Assert.Contains("worker", e.Message);
        Assert.Single(_repo.Saved);
        Assert.Equal(2, _activity.Events.Count);
    }

    [Fact]
    public async Task Delete_WhenOnlyAnOlderVersion_OrADeletedFunction_ReadsIt_Goes()
    {
        FunctionSecret secret = await SeedAsync();
        Function function = AFunctionOfTheTenant("api");
        function.WithSecrets(FunctionSecretRef.Of("DB", secret.Id, secret.Name, 1));
        function.Running();
        Function gone = AFunctionOfTheTenant("gone");
        gone.WithSecrets(FunctionSecretRef.Of("DB", secret.Id, secret.Name, 1));
        gone.Delete();

        await Delete().HandleAsync(new DeleteFunctionSecretCommand("DB_PASSWORD"));

        Assert.Empty(_repo.Saved);
    }

    // ── list ──

    [Fact]
    public async Task List_ByName_WithVersionsAndUses_NeverAValue()
    {
        FunctionSecret db = await SeedAsync("DB_PASSWORD");
        await SeedAsync("API_KEY");
        await AddVersion().HandleAsync(new AddFunctionSecretVersionCommand("DB_PASSWORD", "v2"));
        Function function = AFunctionOfTheTenant("api");
        FunctionVersion first = function.WithSecrets(FunctionSecretRef.Of("DB", db.Id, db.Name, 1));
        function.Running(first);
        FunctionVersion second = function.WithSecrets(FunctionSecretRef.Of("DB", db.Id, db.Name, 2));

        List<FunctionSecretQueryResult> secrets = await List().HandleAsync(new ListFunctionSecretsQuery());

        Assert.Equal(["API_KEY", "DB_PASSWORD"], secrets.Select(s => s.Name));
        FunctionSecretQueryResult listed = secrets[1];
        Assert.Equal(2, listed.LatestVersion);
        Assert.Equal([2, 1], listed.Versions.Select(v => v.Version));
        Assert.Equal(
            [
                new FunctionSecretUseQueryResult(function.Id, "api", second.Id, second.Number, 2, false),
                new FunctionSecretUseQueryResult(function.Id, "api", first.Id, first.Number, 1, true),
            ],
            listed.UsedBy);
        Assert.Empty(secrets[0].UsedBy);
        string json = JsonSerializer.Serialize(secrets);
        Assert.DoesNotContain(Value, json);
        Assert.DoesNotContain("Ciphertext", json);
        Assert.DoesNotContain("Nonce", json);
    }

    [Fact]
    public async Task List_IsTheTenantsOwn()
    {
        await SeedAsync();
        _currentUser.Setup(u => u.TenantId).Returns(Guid.NewGuid());

        Assert.Empty(await List().HandleAsync(new ListFunctionSecretsQuery()));
    }

    // ── the worker's read of a version's values ──

    [Fact]
    public async Task Values_AreThoseTheVersionPins_UnderTheirVariables()
    {
        FunctionSecret db = await SeedAsync("DB_PASSWORD");
        await AddVersion().HandleAsync(new AddFunctionSecretVersionCommand("DB_PASSWORD", "v2"));
        FunctionSecret api = await SeedAsync("API_KEY", "key-1");
        Function function = AFunctionOfTheTenant();
        FunctionVersion version = function.WithSecrets(
            FunctionSecretRef.Of("DB", db.Id, db.Name, 1), FunctionSecretRef.Of("KEY", api.Id, api.Name, 1));

        FunctionSecretValuesQueryResult result = await Values().HandleAsync(new GetFunctionSecretValuesQuery(function.Id, version.Id));

        Assert.Equal(new Dictionary<string, string> { ["DB"] = Value, ["KEY"] = "key-1" }, result.Values);
        Assert.DoesNotContain(Value, result.ToString());
    }

    [Fact]
    public async Task Values_OfAVersionWithoutSecrets_AreEmpty()
    {
        Function function = AFunctionOfTheTenant();
        FunctionVersion version = function.WithVersion();

        Assert.Empty((await Values().HandleAsync(new GetFunctionSecretValuesQuery(function.Id, version.Id))).Values);
    }

    [Fact]
    public async Task Values_OfAnUnknownFunction_OrVersion_Are404()
    {
        Function function = AFunctionOfTheTenant();
        FunctionVersion version = function.WithVersion();
        Function other = AFunctionOfTheTenant("other");

        var noFunction = await Assert.ThrowsAsync<NotFoundException>(() => Values().HandleAsync(new GetFunctionSecretValuesQuery(Guid.NewGuid(), version.Id)));
        var notItsVersion = await Assert.ThrowsAsync<NotFoundException>(() => Values().HandleAsync(new GetFunctionSecretValuesQuery(other.Id, version.Id)));
        other.Delete();
        var deleted = await Assert.ThrowsAsync<NotFoundException>(() => Values().HandleAsync(new GetFunctionSecretValuesQuery(other.Id, version.Id)));

        Assert.Equal(("FUNCTION_NOT_FOUND", "FUNCTION_VERSION_NOT_FOUND", "FUNCTION_NOT_FOUND"), (noFunction.Code, notItsVersion.Code, deleted.Code));
    }

    [Fact]
    public async Task Values_OfASecretGone_OrAVersionNoLongerKept_Are422()
    {
        FunctionSecret db = await SeedAsync("DB_PASSWORD");
        Function function = AFunctionOfTheTenant();
        FunctionVersion missingNumber = function.WithSecrets(FunctionSecretRef.Of("DB", db.Id, db.Name, 7));
        FunctionVersion goneSecret = function.WithSecrets(FunctionSecretRef.Of("DB", Guid.NewGuid(), "GONE", 1));

        var e1 = await Assert.ThrowsAsync<BusinessException>(() => Values().HandleAsync(new GetFunctionSecretValuesQuery(function.Id, missingNumber.Id)));
        var e2 = await Assert.ThrowsAsync<BusinessException>(() => Values().HandleAsync(new GetFunctionSecretValuesQuery(function.Id, goneSecret.Id)));

        Assert.Equal(("FUNCTION_SECRET_NOT_FOUND", "FUNCTION_SECRET_NOT_FOUND"), (e1.Code, e2.Code));
    }

    // Another tenant's secret is as gone: a version only ever reads its own tenant's.
    [Fact]
    public async Task Values_NeverReadAnotherTenantsSecret()
    {
        FunctionSecret theirs = await SeedAsync("DB_PASSWORD");
        Function mine = TestFunctions.AFunction(Guid.NewGuid());
        _functions.Rows.Add(mine);
        FunctionVersion version = mine.WithSecrets(FunctionSecretRef.Of("DB", theirs.Id, theirs.Name, 1));

        var e = await Assert.ThrowsAsync<BusinessException>(() => Values().HandleAsync(new GetFunctionSecretValuesQuery(mine.Id, version.Id)));

        Assert.Equal("FUNCTION_SECRET_NOT_FOUND", e.Code);
    }
}
