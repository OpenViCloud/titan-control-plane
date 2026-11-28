using FastEndpoints;
using FluentValidation;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Infrastructure.Endpoints.Function;

// The limits are the Domain's (titan-ops features/ovc-function/design.md#function-limits). The upload endpoint binds no
// request, so it runs this on the config part itself.
public class UploadFunctionVersionConfigValidator : Validator<UploadFunctionVersionConfig>
{
    public const int HandlerMaxLength = 128;

    // "module.function": the path of the file (dots for directories), then the exported function; the same shape the zip
    // inspector holds the zip to, so a handler that passes here is not refused there.
    private static readonly System.Text.RegularExpressions.Regex HandlerShape =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public UploadFunctionVersionConfigValidator()
    {
        RuleFor(x => x.Runtime).NotEmpty().WithMessage("'runtime' is required.");
        RuleFor(x => x.Handler!)
            .MaximumLength(HandlerMaxLength)
            .Matches(HandlerShape).WithMessage("'handler' is \"module.function\".")
            .When(x => !string.IsNullOrEmpty(x.Handler));
        RuleFor(x => x.MemoryMiB!.Value)
            .Must(FunctionAggregate.IsAMemorySize)
            .WithMessage($"'memoryMiB' is {FunctionAggregate.MinMemoryMiB} to {FunctionAggregate.MaxMemoryMiB} in steps of {FunctionAggregate.MemoryStepMiB}.")
            .When(x => x.MemoryMiB != null);
        RuleFor(x => x.TimeoutSeconds!.Value)
            .Must(FunctionAggregate.IsATimeout)
            .WithMessage($"'timeoutSeconds' is {FunctionAggregate.MinTimeoutSeconds} to {FunctionAggregate.MaxTimeoutSeconds}.")
            .When(x => x.TimeoutSeconds != null);
    }
}
