using Titan.Application.Services.Compute;

namespace Titan.Tests.Services;

// The shape was measured on Incus 7.0.2 with the template's Ubuntu 22.04 image (dev, 2026-11-13): a boothook in
// vendor-data runs before cloud-init waits for the snap seed, so sshd has the keys as soon as it listens; the
// customer's user-data (its bootcmd, its own keys) still applies; a reboot leaves one line per key.
public class VmVendorDataTests
{
    private const string Ed = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJQIuvIctzZ61SoksnjJlkCRERFjIowWaKhlaoSOsHMB";
    private const string Rsa = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCzyjeB";

    [Fact]
    public void NoKeys_IsNoVendorData()
    {
        Assert.Null(VmVendorData.ForKeys([]));
    }

    // A boothook: cloud-init runs it in its first stage, long before the modules that wait for the snap seed.
    [Fact]
    public void Keys_AreABoothook()
    {
        string script = VmVendorData.ForKeys([Ed, Rsa])!;

        Assert.StartsWith("#cloud-boothook\n#!/bin/sh\n", script);
    }

    // The keys go to a file sshd reads for the ubuntu user, not into the home directory: the user may not exist yet
    // when a boothook runs.
    [Fact]
    public void Keys_AreWrittenToAFileSshdReadsForTheUbuntuUser()
    {
        string script = VmVendorData.ForKeys([Ed, Rsa])!;

        Assert.Contains("cat > /etc/ssh/openvicloud/ubuntu <<'KEYS'\n" + Ed + "\n" + Rsa + "\nKEYS\n", script);
        Assert.Contains("chmod 644 /etc/ssh/openvicloud/ubuntu", script);
        Assert.Contains(
            "echo 'AuthorizedKeysFile .ssh/authorized_keys .ssh/authorized_keys2 /etc/ssh/openvicloud/%u' > /etc/ssh/sshd_config.d/10-openvicloud.conf",
            script);
        Assert.DoesNotContain("$home", script);
    }

    // A boothook runs at every boot; the writes run once per instance, so a key the customer removed stays removed.
    [Fact]
    public void TheWrites_RunOncePerInstance()
    {
        string script = VmVendorData.ForKeys([Ed])!;

        Assert.Contains("cloud-init-per instance openvicloud-ssh-keys /bin/sh -s <<'ONCE'\n", script);
        Assert.EndsWith("ONCE\n", script);
        Assert.DoesNotContain(">>", script);
        Assert.DoesNotContain("set -e", script);
    }

    // Each line was rebuilt from a parsed key, so it can hold nothing but a type and base64; anything else means a
    // caller handed raw input in.
    [Theory]
    [InlineData("ssh-ed25519 AAAA\nKEYS\nrm -rf /")]
    [InlineData("ssh-ed25519 AAAA comment")]
    [InlineData("ssh-ed25519 AAAA'")]
    [InlineData("ssh-ed25519 AAAA\n")]
    [InlineData("ssh-ed25519 AAAA\nONCE\nrm -rf /")]
    public void ALineThatIsNotTypeAndBase64_IsRefused(string line)
    {
        Assert.Throws<ArgumentException>(() => VmVendorData.ForKeys([line]));
    }
}
