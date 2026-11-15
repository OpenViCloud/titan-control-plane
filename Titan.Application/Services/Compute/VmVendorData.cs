using System.Text;
using System.Text.RegularExpressions;
using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Compute;

/// Titan's cloud-init vendor-data for a new VM: a boothook that writes the picked SSH keys to a file sshd reads for the
/// `ubuntu` user (titan-ops features/vm/design.md#ssh-key), and leaves the console password's hash for cloud-init's
/// final stage (titan-ops features/vm/design.md#console-password). Cloud-init runs a boothook in an early stage, before
/// sshd starts and before the stages that wait for the image's snap seed, so key login does not wait for the seed.
/// Vendor-data, not user-data: cloud-config keys in vendor-data are dropped when the customer's user-data sets keys
/// too; a boothook is a part of its own and stays.
public static class VmVendorData
{
    // A rebuilt key line: type, one space, base64. Nothing that could end either heredoc or reach the shell.
    private static readonly Regex KeyLine = new(@"\A[a-z0-9-]+ [A-Za-z0-9+/]+={0,2}\z", RegexOptions.CultureInvariant);

    // A SHA-512 crypt string: nothing that could end the single quotes or a heredoc.
    private static readonly Regex PasswordHash = new(@"\A\$6\$[./0-9A-Za-z]{1,16}\$[./0-9A-Za-z]{86}\z", RegexOptions.CultureInvariant);

    private const string PasswordScript = "/var/lib/cloud/scripts/per-instance/openvicloud-console-password";

    /// The vendor-data for the keys a customer picked and the console password they set: every id must be a key of the
    /// caller's tenant, since a foreign id is indistinguishable from an unknown one
    /// (titan-ops features/vm/design.md#ssh-key); a repeated id goes in once. The password leaves as its hash only
    /// (titan-ops features/vm/design.md#console-password).
    public static async Task<string?> ForAsync(ISshKeyRepository keys, Guid tenantId, List<Guid>? sshKeyIds,
        string? consolePassword, CancellationToken ct)
    {
        var lines = new List<string>();
        if (sshKeyIds != null && sshKeyIds.Count > 0)
        {
            List<Guid> distinctIds = sshKeyIds.Distinct().ToList();
            Dictionary<Guid, SshKey> found = (await keys.ListByIdsAsync(tenantId, distinctIds, ct)).ToDictionary(k => k.Id);
            foreach (Guid id in distinctIds)
            {
                if (!found.TryGetValue(id, out SshKey? key))
                {
                    throw new NotFoundException("SSH_KEY_NOT_FOUND", $"SSH key '{id}' not found.");
                }
                lines.Add(key.PublicKey);
            }
        }
        string? passwordHash = null;
        if (consolePassword != null)
        {
            passwordHash = Sha512Crypt.Hash(consolePassword);
        }
        return Build(lines, passwordHash);
    }

    /// Null when there is neither a key nor a password: the VM is created exactly as without these features.
    public static string? Build(IReadOnlyList<string> keyLines, string? consolePasswordHash)
    {
        if (keyLines.Count == 0 && consolePasswordHash == null)
        {
            return null;
        }

        foreach (string line in keyLines)
        {
            if (!KeyLine.IsMatch(line))
            {
                throw new ArgumentException("An SSH key line must be '<type> <base64>', rebuilt from a parsed key.", nameof(keyLines));
            }
        }
        if (consolePasswordHash != null && !PasswordHash.IsMatch(consolePasswordHash))
        {
            throw new ArgumentException("A console password reaches vendor-data as a SHA-512 crypt hash only.", nameof(consolePasswordHash));
        }

        var script = new StringBuilder();
        script.Append("#cloud-boothook\n");
        script.Append("#!/bin/sh\n");
        if (keyLines.Count > 0)
        {
            AppendKeys(script, keyLines);
        }
        if (consolePasswordHash != null)
        {
            AppendPassword(script, consolePasswordHash);
        }
        return script.ToString();
    }

    // The user may not exist yet when a boothook runs, so the keys live outside its home; the customer's own
    // authorized_keys still counts. A boothook runs at every boot: the writes run once per instance, so a key the
    // customer removes stays removed.
    private static void AppendKeys(StringBuilder script, IReadOnlyList<string> keyLines)
    {
        script.Append("# OpenViCloud: SSH keys picked at VM create, for sshd before the rest of cloud-init runs.\n");
        script.Append("cloud-init-per instance openvicloud-ssh-keys /bin/sh -s <<'ONCE'\n");
        script.Append("mkdir -p /etc/ssh/openvicloud /etc/ssh/sshd_config.d\n");
        script.Append("cat > /etc/ssh/openvicloud/ubuntu <<'KEYS'\n");
        foreach (string line in keyLines)
        {
            script.Append(line).Append('\n');
        }
        script.Append("KEYS\n");
        script.Append("chmod 644 /etc/ssh/openvicloud/ubuntu\n");
        script.Append("echo 'AuthorizedKeysFile .ssh/authorized_keys .ssh/authorized_keys2 /etc/ssh/openvicloud/%u' > /etc/ssh/sshd_config.d/10-openvicloud.conf\n");
        script.Append("ONCE\n");
    }

    // The user does not exist yet when a boothook runs, and cloud-init locks its password when it makes it: the boothook
    // leaves a script that cloud-init's final stage runs once, after that.
    private static void AppendPassword(StringBuilder script, string hash)
    {
        script.Append("# OpenViCloud: the console password set at VM create, as its hash, once the ubuntu user exists.\n");
        script.Append("cloud-init-per instance openvicloud-console-password /bin/sh -s <<'ONCE'\n");
        script.Append("mkdir -p /var/lib/cloud/scripts/per-instance\n");
        script.Append("cat > ").Append(PasswordScript).Append(" <<'SCRIPT'\n");
        script.Append("#!/bin/sh\n");
        script.Append("echo 'ubuntu:").Append(hash).Append("' | chpasswd -e\n");
        script.Append("SCRIPT\n");
        script.Append("chmod 700 ").Append(PasswordScript).Append('\n');
        script.Append("ONCE\n");
    }
}
