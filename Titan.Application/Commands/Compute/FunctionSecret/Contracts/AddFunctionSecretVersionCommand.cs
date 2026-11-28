namespace Titan.Application.Commands.Compute.FunctionSecret.Contracts;

// The secret's name from the route, the new value from the body (AddFunctionSecretVersionBody).
public record AddFunctionSecretVersionCommand(string Name, string Value)
{
    // A record prints every property: the value must never reach a log through ToString.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Value = ");
        builder.Append(Value == null ? "null" : "***");
        return true;
    }
}
