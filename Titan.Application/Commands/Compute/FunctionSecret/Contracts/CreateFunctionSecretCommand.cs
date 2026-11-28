namespace Titan.Application.Commands.Compute.FunctionSecret.Contracts;

// The wire contract and the command in one type (titan-ops architecture/code-structure.md#command-is-wire-contract).
// Value is the secret itself: only its ciphertext is kept (titan-ops features/ovc-function/design.md#function-secrets).
public record CreateFunctionSecretCommand(string Name, string Value)
{
    // A record prints every property: the value must never reach a log through ToString.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Value = ");
        builder.Append(Value == null ? "null" : "***");
        return true;
    }
}
