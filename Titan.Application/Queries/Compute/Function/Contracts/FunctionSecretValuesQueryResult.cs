namespace Titan.Application.Queries.Compute.Function.Contracts;

// Each secret's value under its variable's name; only the secrets, not the version's plain variables, which the deploy
// command carries.
public record FunctionSecretValuesQueryResult(Dictionary<string, string> Values)
{
    // A record prints every property: the values must never reach a log through ToString.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Values = [{(Values == null ? "" : string.Join(", ", Values.Keys))}]");
        return true;
    }
}
