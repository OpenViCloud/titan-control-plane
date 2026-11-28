namespace Titan.Domain.Aggregates.ProviderCommand;

/// The worker routes on these; a kind no worker claims stays Pending (titan-ops architecture/provider-and-worker.md#provider-command-table).
public static class ProviderCommandKinds
{
    public const string VmCreate = "vm.create";
    public const string VmDelete = "vm.delete";
    public const string VmStop = "vm.stop";
    public const string VmStart = "vm.start";
    public const string VmSnapshotCreate = "vm-snapshot.create";
    public const string VmSnapshotRevert = "vm-snapshot.revert";
    public const string VmSnapshotDelete = "vm-snapshot.delete";
    public const string VmKeyAdditionCreate = "vm-key-addition.create";
    public const string NetworkCreate = "network.create";
    public const string NetworkDelete = "network.delete";
    public const string FirewallRuleCreate = "firewall-rule.create";
    public const string FirewallRuleDelete = "firewall-rule.delete";
    public const string VolumeCreate = "volume.create";
    public const string VolumeAttach = "volume.attach";
    public const string VolumeDetach = "volume.detach";
    public const string VolumeDelete = "volume.delete";
    public const string VolumeSnapshotCreate = "volume-snapshot.create";
    public const string VolumeSnapshotDelete = "volume-snapshot.delete";
    public const string VolumeSnapshotRestore = "volume-snapshot.restore";
    public const string LoadBalancerCreate = "load-balancer.create";
    public const string LoadBalancerUpdate = "load-balancer.update";
    public const string LoadBalancerDelete = "load-balancer.delete";
    public const string TemplateSync = "template.sync";
    /// Pulls a function runtime's image onto every Ready node of a function cluster. Leased by the function worker, which
    /// runs inside that cluster, never by the Incus worker (titan-ops architecture/provider-commands.md#provider-command-table).
    public const string FunctionRuntimeSync = "function-runtime.sync";
    public const string VmBackup = "vm.backup";
    public const string VolumeBackup = "volume.backup";
    /// The day's config of one cluster, of no tenant and no resource; the worker's backup loop leases it with the two
    /// above, and its row of no tenant is a lane of its own whatever side it is on (titan-ops features/backup/design.md#cluster-backup).
    public const string ClusterBackup = "cluster.backup";

    /// One restore into a temporary Incus project of the cluster, of no tenant and no resource: the worker leases it on a
    /// loop apart from the command and backup loops (with the two restores below), so a drill never holds back a tenant's
    /// command or a backup (titan-ops features/backup/design.md#restore-drill).
    public const string RestoreDrill = "restore.drill";

    /// An admin's restore of one backup archive as a new VM or volume of the same tenant
    /// (titan-ops features/backup/design.md#restore-as-new-resource). The worker leases the two on the drill's loop, so
    /// their rows carry no tenant either: a row with a tenant sits in that tenant's command lane, where the command loop
    /// would keep picking that tenant's next command and losing it to the lane's index. Of no tenant, one of the three
    /// runs at a time: a restore waits for a drill, and a drill for it.
    public const string VmRestore = "vm.restore";
    public const string VolumeRestore = "volume.restore";

    /// The kinds the worker leases on a loop of their own; a tenant's backup and its other commands are two lanes
    /// (titan-ops features/provider/design.md#tenant-lane). The two restores are not among them: the lane is the
    /// tenant's, and their rows have none.
    public static readonly IReadOnlyList<string> Backup = [VmBackup, VolumeBackup];
}
