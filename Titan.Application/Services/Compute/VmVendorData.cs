using System.Text;
using System.Text.RegularExpressions;
using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Compute;

/// Titan's cloud-init vendor-data for a new VM: a boothook that writes the picked SSH keys to a file sshd reads for the
/// `ubuntu` user (titan-ops features/vm/design.md#ssh-key). A boothook runs in cloud-init's first stage, before the
/// stages that wait for the image's snap seed, so the keys work as soon as sshd listens. Vendor-data, not user-data:
/// cloud-config keys in vendor-data are dropped when the customer's user-data sets keys too; a boothook is a part of
/// its own and stays.
public static class VmVendorData
{
    // A rebuilt key line: type, one space, base64. Nothing that could end either heredoc or reach the shell.
    private static readonly Regex KeyLine = new(@"\A[a-z0-9-]+ [A-Za-z0-9+/]+={0,2}\z", RegexOptions.CultureInvariant);

    /// The vendor-data for the keys a customer picked: every id must be a key of the caller's tenant, since a foreign id
    /// is indistinguishable from an unknown one (titan-ops features/vm/design.md#ssh-key); a repeated id goes in once.
    public static async Task<string?> ForKeysAsync(ISshKeyRepository keys, Guid tenantId, List<Guid>? sshKeyIds, CancellationToken ct)
    {
        if (sshKeyIds == null || sshKeyIds.Count == 0)
        {
            return null;
        }

        List<Guid> distinctIds = sshKeyIds.Distinct().ToList();
        Dictionary<Guid, SshKey> found = (await keys.ListByIdsAsync(tenantId, distinctIds, ct)).ToDictionary(k => k.Id);
        var lines = new List<string>();
        foreach (Guid id in distinctIds)
        {
            if (!found.TryGetValue(id, out SshKey? key))
            {
                throw new NotFoundException("SSH_KEY_NOT_FOUND", $"SSH key '{id}' not found.");
            }
            lines.Add(key.PublicKey);
        }
        return ForKeys(lines);
    }

    /// Null when there is no key: the VM is created exactly as without this feature.
    public static string? ForKeys(IReadOnlyList<string> keyLines)
    {
        if (keyLines.Count == 0)
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

        // The user may not exist yet when a boothook runs, so the keys live outside its home; the customer's own
        // authorized_keys still counts. A boothook runs at every boot: the writes run once per instance, so a key the
        // customer removes stays removed.
        var script = new StringBuilder();
        script.Append("#cloud-boothook\n");
        script.Append("#!/bin/sh\n");
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
        return script.ToString();
    }
}
