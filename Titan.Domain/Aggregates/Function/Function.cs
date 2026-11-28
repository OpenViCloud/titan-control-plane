using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Function;

/// A customer's function on a function cluster (titan-ops features/ovc-function/design.md#function-lifecycle): its
/// uploaded versions, the one that runs and the one being deployed, and one status with one operation waiting at a time.
/// Nothing of it is on the cluster until its first deploy; the Knative Service the worker names then is its ExternalId.
public partial class Function : IAggregateRoot
{
    /// A function's name: 1 to 100 letters, digits, hyphens or underscores, starting with a letter or digit, as a load
    /// balancer's. The validators read these two.
    public const int NameMaxLength = 100;
    public const string NamePattern = "^[a-zA-Z0-9][a-zA-Z0-9_-]*$";

    // The limits of titan-ops features/ovc-function/design.md#function-limits. Constants, not TenantQuota caps: no admin
    // sets them per tenant yet.
    public const int MaxPerTenant = 20;
    /// The newest versions kept, plus the running and the deploying one when older
    /// (titan-ops features/ovc-function/design.md#function-versions).
    public const int KeptVersions = 5;
    public const int MaxInstances = 10;
    /// Knative's containerConcurrency: one call at a time per instance.
    public const int ConcurrencyPerInstance = 1;
    public const int MaxInstancesPerTenant = 20;
    public const int MinMemoryMiB = 128;
    public const int MaxMemoryMiB = 2048;
    public const int MemoryStepMiB = 128;
    public const int DefaultMemoryMiB = 128;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;
    public const int DefaultTimeoutSeconds = 30;

    // The tenant namespace's ResourceQuota and LimitRange the deploy command carries. Memory: every instance at the
    // largest size, plus the container beside it in the pod, which the namespace's default sizes.
    public const int TenantQuotaMemoryMiB = MaxInstancesPerTenant * (MaxMemoryMiB + DefaultMemoryMiB);
    public const int TenantQuotaPods = MaxInstancesPerTenant;
    public const int TenantQuotaServices = MaxPerTenant;
    public const int TenantQuotaCronJobs = MaxPerTenant;
    public const int DefaultCpuMillicores = DefaultMemoryMiB * 1000 / 1024;
    public const int MaxCpuMillicores = MaxMemoryMiB * 1000 / 1024;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// A function cluster of FunctionClusters, never an Incus cluster.
    public string ClusterId { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    /// The Knative Service, "fn<26>", the worker names on the first deploy that reaches the cluster.
    public string? ExternalId { get; private set; }
    /// The one status the API shows. Only an operation, a report ending it, or an observation at rest writes it, each
    /// through the moves of Function.Deployment.cs, Function.Deletion.cs and Function.Observation.cs.
    public FunctionStatus Status { get; private set; }
    public FunctionOperation Operation { get; private set; } = FunctionOperation.None;
    /// The version the Knative Service serves; null until a deploy ends Done.
    public Guid? RunningVersionId { get; private set; }
    /// The version the waiting deploy asks for; null when no deploy waits.
    public Guid? DeployingVersionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<FunctionVersion> _versions = [];
    public IReadOnlyCollection<FunctionVersion> Versions => _versions;

    private Function() { }

    /// Nothing reaches the cluster here: the first deploy makes the Knative Service. The name is the handler's to check
    /// (titan-ops architecture/domain-methods.md#names-and-sizes).
    public static Function Create(Guid tenantId, string clusterId, string name)
    {
        return new Function
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = clusterId,
            Name = name,
            Status = FunctionStatus.Inactive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsDeleted => Status == FunctionStatus.Deleted;

    /// A delete waits or is done: a later delete is a repeat, answered like the first.
    public bool IsDeletedOrDeleting => Status == FunctionStatus.Deleting || IsDeleted;

    /// Counts toward MaxPerTenant until the delete is done: a function being deleted still holds its Knative Service in
    /// the tenant's namespace, whose quota counts it. FunctionRepository.CountActiveByTenantAsync writes the same test in
    /// SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool OccupiesQuota => Status != FunctionStatus.Deleted;

    /// Whether the function cluster holds the Knative Service as far as Titan last knew: what the waiting operation last
    /// saw, or Active at rest. What the reconciler is told Titan recorded.
    public bool IsSeenOnTheCluster
    {
        get
        {
            if (IsWaiting)
            {
                return Operation.LastSeenStatus == FunctionStatus.Active;
            }
            return Status == FunctionStatus.Active;
        }
    }

    /// The reconciler's set: something may be on the cluster, and the function is not gone.
    public bool IsReconcilable => ExternalId != null && Status != FunctionStatus.Deleted;

    /// The versions whose zip and image the cleanup job removes: the discarded ones, or every one of a deleted function
    /// (titan-ops features/ovc-function/design.md#code-storage).
    public IEnumerable<FunctionVersion> VersionsToClean
    {
        get
        {
            if (IsDeleted)
            {
                return _versions;
            }
            return _versions.Where(v => v.IsDiscarded);
        }
    }

    private bool IsWaiting => Status == FunctionStatus.Deploying || Status == FunctionStatus.Deleting;

    private static readonly System.Text.RegularExpressions.Regex NameShape =
        new(NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    /// The shape rule of a function name (titan-ops architecture/domain-methods.md#names-and-sizes).
    public static bool IsAName(string? name) =>
        name != null && name.Length > 0 && name.Length <= NameMaxLength && NameShape.IsMatch(name);

    /// 128 to 2048 MiB in steps of 128 (titan-ops features/ovc-function/design.md#function-limits).
    public static bool IsAMemorySize(int memoryMiB) =>
        memoryMiB >= MinMemoryMiB && memoryMiB <= MaxMemoryMiB && memoryMiB % MemoryStepMiB == 0;

    /// 1 to 300 seconds for an HTTP function (titan-ops features/ovc-function/design.md#function-limits).
    public static bool IsATimeout(int timeoutSeconds) =>
        timeoutSeconds >= MinTimeoutSeconds && timeoutSeconds <= MaxTimeoutSeconds;

    /// The zip's key in the function bucket (titan-ops features/ovc-function/design.md#code-storage). Known before the
    /// version exists: the upload writes the zip first.
    public static string CodeKeyFor(Guid tenantId, Guid functionId, Guid versionId) =>
        $"functions/{tenantId}/{functionId}/{versionId}.zip";

    /// The report names the operation the function waits on, which its status says.
    private bool IsWaitingOn(Guid operationId, FunctionStatus waiting) =>
        Status == waiting && Operation.PendingId == operationId;

    /// Where a give-up leaves the function: Inactive while no version ever ran, else as the cluster was last seen
    /// holding it while the operation waited.
    private FunctionStatus RestingStatus()
    {
        if (RunningVersionId == null)
        {
            return FunctionStatus.Inactive;
        }
        return Operation.LastSeenStatus ?? FunctionStatus.NotFound;
    }

    /// What the cluster holds at rest, as the operation about to be asked remembers it.
    private FunctionStatus? SeenAtRest()
    {
        if (Status == FunctionStatus.Active || Status == FunctionStatus.NotFound)
        {
            return Status;
        }
        return null;
    }

    /// The Knative Service the worker names, once, whatever the outcome: a deploy that gave up after the Service was
    /// made leaves one that a delete must still reach.
    private void RecordTheService(string? externalId)
    {
        if (ExternalId == null && !string.IsNullOrEmpty(externalId))
        {
            ExternalId = externalId;
        }
    }

    private FunctionVersion? FindKeptVersion(Guid versionId) =>
        _versions.FirstOrDefault(v => v.Id == versionId && !v.IsDiscarded);

    private void EnsureNotDeletedOrDeleting()
    {
        if (Status == FunctionStatus.Deleting || Status == FunctionStatus.Deleted)
        {
            throw new BusinessException("INVALID_STATUS", "The function is being or has been deleted.");
        }
    }
}
