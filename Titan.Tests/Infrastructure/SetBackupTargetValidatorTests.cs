using FluentValidation.TestHelper;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using Titan.Infrastructure.Endpoints.Admin;

namespace Titan.Tests.Infrastructure;

// The validator applies the aggregate's own rules, so a request it lets through is never refused later as a 500.
// It cannot see the database, so "the first PUT needs a secret" is not here: the handler and the domain own it.
public class SetBackupTargetValidatorTests
{
    private readonly SetBackupTargetValidator _validator = new();

    private static SetBackupTargetCommand Command(
        string endpoint = "https://acct.r2.cloudflarestorage.com", string region = "auto",
        string bucket = "titan-backup-dev", string accessKeyId = "key-id", string? secret = "s") =>
        new(endpoint, region, bucket, accessKeyId, secret);

    [Fact]
    public void AValidCommand_IsAccepted_WithOrWithoutASecret()
    {
        _validator.TestValidate(Command()).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(Command(secret: null)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AnHttpEndpoint_IsRefused_OnTheEndpointOnly()
    {
        var result = _validator.TestValidate(Command(endpoint: "http://acct.r2.cloudflarestorage.com"));

        result.ShouldHaveValidationErrorFor(c => c.Endpoint);
        result.ShouldNotHaveValidationErrorFor(c => c.Bucket);
    }

    [Fact]
    public void AnEmptyBucket_IsRefused_OnTheBucketOnly()
    {
        var result = _validator.TestValidate(Command(bucket: ""));

        result.ShouldHaveValidationErrorFor(c => c.Bucket);
        result.ShouldNotHaveValidationErrorFor(c => c.Endpoint);
    }

    [Fact]
    public void ABucketOf64Characters_IsRefused()
    {
        _validator.TestValidate(Command(bucket: new string('a', 64))).ShouldHaveValidationErrorFor(c => c.Bucket);
    }

    [Fact]
    public void AnEmptyAccessKeyId_IsRefused()
    {
        _validator.TestValidate(Command(accessKeyId: "")).ShouldHaveValidationErrorFor(c => c.AccessKeyId);
    }

    [Fact]
    public void AnEmptyRegion_IsRefused()
    {
        _validator.TestValidate(Command(region: "")).ShouldHaveValidationErrorFor(c => c.Region);
    }
}
