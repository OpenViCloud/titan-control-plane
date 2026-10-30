using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

public class SshKeyTests
{
    [Fact]
    public void Create_KeepsTheTenantTheNameAndTheParsedKey()
    {
        var tenant = TestTenant.Active("acme", "kc-1", "a@acme.com");

        SshKey key = SshKey.Create(tenant, 0, "  laptop  ", SshPublicKeyTests.Ed25519);

        Assert.NotEqual(Guid.Empty, key.Id);
        Assert.Equal(tenant.Id, key.TenantId);
        Assert.Equal("laptop", key.Name);
        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Equal(SshPublicKeyTests.Ed25519Fingerprint, key.Fingerprint);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJQIuvIctzZ61SoksnjJlkCRERFjIowWaKhlaoSOsHMB", key.PublicKey);
    }

    [Fact]
    public void Create_MalformedKey_IsRefused()
    {
        var tenant = TestTenant.Active("acme", "kc-1", "a@acme.com");

        Assert.Equal("INVALID_SSH_KEY",
            Assert.Throws<BadRequestException>(() => SshKey.Create(tenant, 0, "laptop", "ssh-ed25519 nope")).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_IsRefused(string name)
    {
        var tenant = TestTenant.Active("acme", "kc-1", "a@acme.com");

        Assert.Throws<BadRequestException>(() => SshKey.Create(tenant, 0, name, SshPublicKeyTests.Ed25519));
    }

    [Fact]
    public void Create_NameLongerThan64_IsRefused()
    {
        var tenant = TestTenant.Active("acme", "kc-1", "a@acme.com");

        Assert.Throws<BadRequestException>(() => SshKey.Create(tenant, 0, new string('k', 65), SshPublicKeyTests.Ed25519));
        Assert.NotNull(SshKey.Create(tenant, 0, new string('k', 64), SshPublicKeyTests.Ed25519));
    }
}
