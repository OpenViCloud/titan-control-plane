using Titan.Application.Abstractions;

namespace Titan.Application.Services.Compute.Function;

/// The language of a catalog runtime, from its name ("python3.15", "nodejs26", "go"): the three languages of OVC Function
/// (titan-ops features/ovc-function/design.md#runtimes). The inspector checks the zip against it.
public static class FunctionRuntimeLanguage
{
    /// Null for a name of no known language.
    public static FunctionCodeLanguage? Of(string runtimeName)
    {
        if (runtimeName.StartsWith("python", StringComparison.Ordinal))
        {
            return FunctionCodeLanguage.Python;
        }
        if (runtimeName.StartsWith("nodejs", StringComparison.Ordinal))
        {
            return FunctionCodeLanguage.NodeJs;
        }
        if (runtimeName == "go")
        {
            return FunctionCodeLanguage.Go;
        }
        return null;
    }
}
