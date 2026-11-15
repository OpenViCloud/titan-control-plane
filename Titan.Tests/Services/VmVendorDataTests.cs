using Titan.Application.Services.Compute;

namespace Titan.Tests.Services;

// The shape was measured on Incus 7.0.2 with the template's Ubuntu 22.04 image (dev, 2026-11-13): a boothook in
// vendor-data runs before sshd starts and before cloud-init waits for the snap seed, so key login does not wait for
// the seed; the customer's user-data (its bootcmd, its own keys) still applies; a reboot leaves one line per key.
public class VmVendorDataTests
{
    private const string Ed = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJQIuvIctzZ61SoksnjJlkCRERFjIowWaKhlaoSOsHMB";
    private const string Rsa = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCzyjeB";
    private const string Hash = "$6$rgprobesalt$nSxi4dKg3obBTV33X0Wnws7kIi8jT50mu.DpARo.IQtM4NQ/SAJt4uuiOqBLf8QgnyKzhezr5SvghngykEqvO0";

    [Fact]
    public void NoKeysAndNoPassword_IsNoVendorData()
    {
        Assert.Null(VmVendorData.Build([], null));
    }

    // A boothook: cloud-init runs it in an early stage, before sshd starts and long before the modules that wait for
    // the snap seed.
    [Fact]
    public void Keys_AreABoothook()
    {
        string script = VmVendorData.Build([Ed, Rsa], null)!;

        Assert.StartsWith("#cloud-boothook\n#!/bin/sh\n", script);
    }

    // The keys go to a file sshd reads for the ubuntu user, not into the home directory: the user may not exist yet
    // when a boothook runs.
    [Fact]
    public void Keys_AreWrittenToAFileSshdReadsForTheUbuntuUser()
    {
        string script = VmVendorData.Build([Ed, Rsa], null)!;

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
        string script = VmVendorData.Build([Ed], null)!;

        Assert.Contains("cloud-init-per instance openvicloud-ssh-keys /bin/sh -s <<'ONCE'\n", script);
        Assert.EndsWith("ONCE\n", script);
    }

    // The key file is written whole, never appended to; no `set -e`, so one failed step does not stop the next.
    [Fact]
    public void TheKeyFile_IsWrittenWhole_AndNoStepStopsTheNext()
    {
        string script = VmVendorData.Build([Ed], null)!;

        Assert.DoesNotContain(">>", script);
        Assert.DoesNotContain("set -e", script);
    }

    // titan-ops features/vm/design.md#console-password: the `ubuntu` user does not exist yet when a boothook runs, so the
    // boothook leaves a per-instance script for cloud-init's final stage, which runs after the user is made and locked.
    [Fact]
    public void AConsolePassword_LeavesAScriptThatSetsItsHash_OncePerInstance()
    {
        string script = VmVendorData.Build([], Hash)!;

        Assert.StartsWith("#cloud-boothook\n#!/bin/sh\n", script);
        Assert.Contains("cloud-init-per instance openvicloud-console-password /bin/sh -s <<'ONCE'\n", script);
        Assert.Contains("cat > /var/lib/cloud/scripts/per-instance/openvicloud-console-password <<'SCRIPT'\n", script);
        Assert.Contains("echo 'ubuntu:" + Hash + "' | chpasswd -e\n", script);
        Assert.Contains("chmod 700 /var/lib/cloud/scripts/per-instance/openvicloud-console-password\n", script);
        Assert.DoesNotContain("openvicloud-ssh-keys", script);
    }

    [Fact]
    public void KeysAndAPassword_AreTwoOncePerInstanceParts_OfOneBoothook()
    {
        string script = VmVendorData.Build([Ed], Hash)!;

        Assert.Equal(1, script.Split("#cloud-boothook").Length - 1);
        Assert.Contains("cloud-init-per instance openvicloud-ssh-keys", script);
        Assert.Contains("cloud-init-per instance openvicloud-console-password", script);
        Assert.Contains(Ed, script);
        Assert.Contains(Hash, script);
    }

    // The hash sits between single quotes in a script: anything but a SHA-512 crypt string means a caller's bug.
    [Theory]
    [InlineData("plain-password-123")]
    [InlineData("$6$salt$abc'def")]
    [InlineData("$1$salt$abcdefghijklmnopqrstuv")]
    public void APasswordThatIsNotASha512CryptHash_IsRefused(string hash)
    {
        Assert.Throws<ArgumentException>(() => VmVendorData.Build([], hash));
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
        Assert.Throws<ArgumentException>(() => VmVendorData.Build([line], null));
    }
}
