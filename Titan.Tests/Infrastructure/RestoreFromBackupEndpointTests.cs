using System.Text.Json;
using FastEndpoints;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Http;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.Auth;
using Titan.Infrastructure.Endpoints.Admin;
using Titan.Infrastructure.Persistence;
using Titan.Tests.Services;

namespace Titan.Tests.Infrastructure;

// The two routes an admin restores a backup archive through (titan-ops features/backup/design.md#restore-as-new-resource).
// They write a tenant's resources across tenants, so each carries its own gate: the role lives in Configure() (the
// global default only asks for a login), and a create is replayed by its Idempotency-Key. The route id is the SOURCE,
// the answer is the NEW resource's id and nothing else (titan-ops architecture/http-contract.md#create-returns-id).
public class RestoreFromBackupEndpointTests
{
    private readonly RestoreFromBackupWorld _world = new();

    private string Key => RestoreFromBackupWorld.KeyOf(_world.VmArchiveTakenAt);

    private RestoreVmFromBackupEndpoint VmEndpoint(TitanDbContext db, Guid vmId) =>
        Factory.Create<RestoreVmFromBackupEndpoint>(ctx => ctx.Request.RouteValues["vmId"] = vmId.ToString(), _world.VmHandler(db));

    private RestoreVolumeFromBackupEndpoint VolumeEndpoint(TitanDbContext db, Guid volumeId) =>
        Factory.Create<RestoreVolumeFromBackupEndpoint>(
            ctx => ctx.Request.RouteValues["volumeId"] = volumeId.ToString(), _world.VolumeHandler(db));

    // The host writes its answers with the web defaults; an endpoint built outside it does not, so the field names are
    // read from the answer's contract, as BackupsQueryResultJsonTests does.
    private static string[] FieldsOf(object answer)
    {
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return json.RootElement.EnumerateObject().Select(f => f.Name).ToArray();
    }

    // ---- Who may call them, and how.

    [Fact]
    public void RestoreVm_IsAPost_ForPlatformAdminOnly_ThatRequiresAnIdempotencyKey()
    {
        using TitanDbContext db = _world.NewDb();
        RestoreVmFromBackupEndpoint ep = VmEndpoint(db, Guid.NewGuid());

        Assert.Equal(["v1/admin/backups/vms/{vmId:guid}/restore"], ep.Definition.Routes);
        Assert.Equal(["POST"], ep.Definition.Verbs);
        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
        Assert.IsAssignableFrom<IRequiresIdempotencyKey>(ep);
    }

    [Fact]
    public void RestoreVolume_IsAPost_ForPlatformAdminOnly_ThatRequiresAnIdempotencyKey()
    {
        using TitanDbContext db = _world.NewDb();
        RestoreVolumeFromBackupEndpoint ep = VolumeEndpoint(db, Guid.NewGuid());

        Assert.Equal(["v1/admin/backups/volumes/{volumeId:guid}/restore"], ep.Definition.Routes);
        Assert.Equal(["POST"], ep.Definition.Verbs);
        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
        Assert.IsAssignableFrom<IRequiresIdempotencyKey>(ep);
    }

    // ---- What they answer.

    [Fact]
    public async Task RestoreVm_RestoresTheVmOfTheRoute_AndAnswers202_WithOnlyTheNewVmsId()
    {
        Vm source = await _world.ARunningVmAsync("web-1");
        await _world.AVmArchiveAsync(source);
        await using TitanDbContext db = _world.NewDb();
        RestoreVmFromBackupEndpoint ep = VmEndpoint(db, source.Id);

        await ep.HandleAsync(new RestoreVmFromBackupBody(Key, "web-1-restored"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, ep.HttpContext.Response.StatusCode);
        Vm restored = Assert.Single(await _world.VmsAsync(), v => v.RestoredFromBackup);
        Assert.Equal("web-1-restored", restored.Name);
        Assert.Equal(restored.Id, ep.Response.NewVmId);
        Assert.NotEqual(source.Id, ep.Response.NewVmId);
        Assert.Equal(["newVmId"], FieldsOf(ep.Response));
    }

    [Fact]
    public async Task RestoreVolume_RestoresTheVolumeOfTheRoute_AndAnswers202_WithOnlyTheNewVolumesId()
    {
        Volume source = await _world.AnAvailableVolumeAsync("data-1");
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, source.Id, _world.VmArchiveTakenAt);
        await using TitanDbContext db = _world.NewDb();
        RestoreVolumeFromBackupEndpoint ep = VolumeEndpoint(db, source.Id);

        await ep.HandleAsync(new RestoreVolumeFromBackupBody(Key, "data-1-restored"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, ep.HttpContext.Response.StatusCode);
        Volume restored = Assert.Single(await _world.VolumesAsync(), v => v.RestoredFromBackup);
        Assert.Equal("data-1-restored", restored.Name);
        Assert.Equal(restored.Id, ep.Response.NewVolumeId);
        Assert.NotEqual(source.Id, ep.Response.NewVolumeId);
        Assert.Equal(["newVolumeId"], FieldsOf(ep.Response));
    }

    // ---- What the body may hold: 400 before the handler runs.

    private readonly RestoreVmFromBackupValidator _vmValidator = new();
    private readonly RestoreVolumeFromBackupValidator _volumeValidator = new();

    // The key is the time in the archive's object key, digit for digit: the worker writes it back into an object key, so
    // nothing else reaches it.
    public static TheoryData<string?> KeysThatAreNotAnArchives() => new()
    {
        null, "", " ", "20261120T010000", "20261120010000Z", "2026-11-20T01:00:00Z", "20261120T010000Z ", " 20261120T010000Z",
        "20261120T010000Z\n", "20261120t010000z", "2026112T010000Z", "202611200T010000Z", "20261120T0100000Z",
        "../20261120T010000Z", "20261120T010000Z/..", "20261120T010000Z.tar.gz", "٢٠٢٦١١٢٠T٠١٠٠٠٠Z",
    };

    [Theory]
    [MemberData(nameof(KeysThatAreNotAnArchives))]
    public void ABackupKey_ThatIsNotAnArchiveKey_IsRejected_ForAVmAndForAVolume(string? backupKey)
    {
        _vmValidator.TestValidate(new RestoreVmFromBackupBody(backupKey!, "web-1")).ShouldHaveValidationErrorFor(r => r.BackupKey);
        _volumeValidator.TestValidate(new RestoreVolumeFromBackupBody(backupKey!, "data-1")).ShouldHaveValidationErrorFor(r => r.BackupKey);
    }

    [Fact]
    public void AnArchiveKey_IsAccepted_ForAVmAndForAVolume()
    {
        _vmValidator.TestValidate(new RestoreVmFromBackupBody("20261120T010000Z", "web-1")).ShouldNotHaveAnyValidationErrors();
        _volumeValidator.TestValidate(new RestoreVolumeFromBackupBody("20261120T010000Z", "data-1")).ShouldNotHaveAnyValidationErrors();
    }

    // The new VM's name follows the rule of a VM's create (Vm.NameMaxLength, Vm.NamePattern).
    [Fact]
    public void ANewVmsName_FollowsTheVmNameRule()
    {
        RestoreVmFromBackupBody Named(string? name) => new("20261120T010000Z", name!);
        _vmValidator.TestValidate(Named(new string('a', Vm.NameMaxLength))).ShouldNotHaveValidationErrorFor(r => r.Name);
        _vmValidator.TestValidate(Named(new string('a', Vm.NameMaxLength + 1))).ShouldHaveValidationErrorFor(r => r.Name);
        _vmValidator.TestValidate(Named("a_b")).ShouldHaveValidationErrorFor(r => r.Name);
        _vmValidator.TestValidate(Named("-web")).ShouldHaveValidationErrorFor(r => r.Name);
        _vmValidator.TestValidate(Named("")).ShouldHaveValidationErrorFor(r => r.Name);
        _vmValidator.TestValidate(Named(null)).ShouldHaveValidationErrorFor(r => r.Name);
    }

    // The new volume's name follows the rule of a volume's create (Volume.NameMaxLength, any characters).
    [Fact]
    public void ANewVolumesName_FollowsTheVolumeNameRule()
    {
        RestoreVolumeFromBackupBody Named(string? name) => new("20261120T010000Z", name!);
        _volumeValidator.TestValidate(Named(new string('a', Volume.NameMaxLength))).ShouldNotHaveValidationErrorFor(r => r.Name);
        _volumeValidator.TestValidate(Named("Data of web_1 (copy)")).ShouldNotHaveValidationErrorFor(r => r.Name);
        _volumeValidator.TestValidate(Named(new string('a', Volume.NameMaxLength + 1))).ShouldHaveValidationErrorFor(r => r.Name);
        _volumeValidator.TestValidate(Named("")).ShouldHaveValidationErrorFor(r => r.Name);
        _volumeValidator.TestValidate(Named("   ")).ShouldHaveValidationErrorFor(r => r.Name);
        _volumeValidator.TestValidate(Named(null)).ShouldHaveValidationErrorFor(r => r.Name);
    }
}
