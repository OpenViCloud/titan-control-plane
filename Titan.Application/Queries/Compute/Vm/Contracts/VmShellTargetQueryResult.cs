namespace Titan.Application.Queries.Compute.Vm.Contracts;

// Where an SSH session execs: the VM's cluster, then the Incus project and instance.
public record VmShellTargetQueryResult(string ClusterId, string ProjectName, string InstanceName);
