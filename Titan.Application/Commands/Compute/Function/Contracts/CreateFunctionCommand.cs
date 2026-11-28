namespace Titan.Application.Commands.Compute.Function.Contracts;

// The command IS the wire contract (titan-ops architecture/code-structure.md#command-is-wire-contract). ClusterId names
// a function cluster.
public record CreateFunctionCommand(string Name, string ClusterId);
