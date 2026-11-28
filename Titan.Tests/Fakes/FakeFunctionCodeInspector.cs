using Titan.Application.Abstractions;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Fakes;

// Stands for the zip inspector in unit tests: answers a fixed layer, or refuses every zip with the reason given.
public class FakeFunctionCodeInspector : IFunctionCodeInspector
{
    public string? Refusal { get; set; }
    public List<(string ZipPath, FunctionCodeLanguage Language, string? Handler, string WorkDir)> Packed { get; } = [];

    public FunctionCodeLayer Layer { get; set; } = new(
        "sha256:" + new string('5', 64), 2048, 8192, 4, "sha256:" + new string('d', 64), "sha256:" + new string('e', 64), 1024,
        "/tmp/layer.tar.gz");

    public Task<FunctionCodeLayer> PackAsync(string zipPath, FunctionCodeLanguage language, string? handler, string workDir, CancellationToken ct)
    {
        Packed.Add((zipPath, language, handler, workDir));
        if (Refusal != null)
        {
            throw new BusinessException("FUNCTION_CODE_INVALID", Refusal);
        }
        return Task.FromResult(Layer);
    }
}
