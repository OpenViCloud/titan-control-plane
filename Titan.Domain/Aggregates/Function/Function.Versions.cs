using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Function;

public partial class Function
{
    /// Refused while a delete waits or once the function is gone: asked before an upload writes anything to the bucket or
    /// the registry, and again by AddVersion under the row lock.
    public void EnsureTakesVersions()
    {
        if (IsDeletedOrDeleting)
        {
            throw new BusinessException("INVALID_STATUS", "The function is being or has been deleted.");
        }
    }

    /// A kept version of this function: one discarded, or of another function, is as missing.
    public FunctionVersion GetVersion(Guid versionId)
    {
        FunctionVersion? version = FindKeptVersion(versionId);
        if (version == null)
        {
            throw new NotFoundException("FUNCTION_VERSION_NOT_FOUND", "Function version not found.");
        }
        return version;
    }

    /// The next version, numbered after every version the function still has. Every version beyond the KeptVersions
    /// newest that neither runs nor is being deployed is discarded: no longer listed nor deployable, its zip and image
    /// left to the cleanup job (titan-ops features/ovc-function/design.md#code-storage).
    public FunctionVersion AddVersion(FunctionVersionSpec spec)
    {
        EnsureNotDeletedOrDeleting();
        if (_versions.Any(v => v.Id == spec.VersionId))
        {
            throw new ArgumentException("a version id is used once", nameof(spec));
        }
        int number = 1;
        if (_versions.Count > 0)
        {
            number = _versions.Max(v => v.Number) + 1;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FunctionVersion version = FunctionVersion.Create(Id, number, spec, now);
        _versions.Add(version);
        DiscardBeyondTheKept(now);
        return version;
    }

    /// Removes a version whose zip and image the cleanup job has deleted. A kept version is refused: its code is still
    /// needed. A version this function does not have is already gone.
    public void ForgetCode(Guid versionId)
    {
        FunctionVersion? version = _versions.FirstOrDefault(v => v.Id == versionId);
        if (version == null)
        {
            return;
        }
        if (!IsDeleted && !version.IsDiscarded)
        {
            throw new BusinessException("INVALID_STATUS", "A kept version's code is still in use.");
        }
        _versions.Remove(version);
    }

    private void DiscardBeyondTheKept(DateTimeOffset now)
    {
        List<FunctionVersion> newestFirst = _versions.OrderByDescending(v => v.Number).ToList();
        for (int i = KeptVersions; i < newestFirst.Count; i++)
        {
            FunctionVersion version = newestFirst[i];
            bool inUse = version.Id == RunningVersionId || version.Id == DeployingVersionId;
            if (!inUse)
            {
                version.Discard(now);
            }
        }
    }
}
