namespace Titan.Domain.Aggregates.FunctionSecret;

public partial class FunctionSecret
{
    /// A new value: the next version, encrypted under NextVersionNumber. Every version beyond the KeptVersions newest
    /// goes, except those in pinned: the numbers a kept version of a function not Deleted points at, which a deploy of
    /// that version still reads (titan-ops features/ovc-function/design.md#function-secrets). A function running an older
    /// version keeps it: it receives the new value only with a new function version.
    public FunctionSecretVersion AddVersion(FunctionSecretValue value, string createdBy, IReadOnlyCollection<int> pinned)
    {
        FunctionSecretVersion added = Append(value, createdBy);
        List<FunctionSecretVersion> newestFirst = _versions.OrderByDescending(v => v.Number).ToList();
        for (int i = KeptVersions; i < newestFirst.Count; i++)
        {
            if (!pinned.Contains(newestFirst[i].Number))
            {
                _versions.Remove(newestFirst[i]);
            }
        }
        return added;
    }
}
