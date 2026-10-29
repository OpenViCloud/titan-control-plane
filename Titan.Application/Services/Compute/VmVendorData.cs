using System.Text;
using System.Text.RegularExpressions;

namespace Titan.Application.Services.Compute;

/// Titan's cloud-init vendor-data for a new VM: a shell script that appends the picked SSH keys to the `ubuntu`
/// user's authorized_keys (titan-ops features/vm/design.md#ssh-key). Vendor-data, not user-data: cloud-config keys in
/// vendor-data are dropped when the customer's user-data sets keys too, and a script appends instead.
public static class VmVendorData
{
    // A rebuilt key line: type, one space, base64. Nothing that could end the heredoc or reach the shell.
    private static readonly Regex KeyLine = new(@"\A[a-z0-9-]+ [A-Za-z0-9+/]+={0,2}\z", RegexOptions.CultureInvariant);

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

        var script = new StringBuilder();
        script.Append("#!/bin/sh\n");
        script.Append("# OpenViCloud: SSH keys picked at VM create. Appends; the customer's own keys stay.\n");
        script.Append("# Each step reports its own failure; the next step still runs.\n");
        script.Append("user=ubuntu\n");
        script.Append("home=$(getent passwd \"$user\" | cut -d: -f6)\n");
        script.Append("if [ -z \"$home\" ]; then echo \"openvicloud: user $user not found\" >&2; exit 1; fi\n");
        script.Append("install -d -m 700 -o \"$user\" -g \"$user\" \"$home/.ssh\" || echo \"openvicloud: cannot create $home/.ssh\" >&2\n");
        script.Append("cat >> \"$home/.ssh/authorized_keys\" <<'KEYS'\n");
        foreach (string line in keyLines)
        {
            script.Append(line).Append('\n');
        }

        script.Append("KEYS\n");
        script.Append("chown \"$user:$user\" \"$home/.ssh/authorized_keys\" && chmod 600 \"$home/.ssh/authorized_keys\" || echo \"openvicloud: cannot fix authorized_keys owner/mode\" >&2\n");
        return script.ToString();
    }
}
