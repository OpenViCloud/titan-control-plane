namespace Titan.Application.Options;

public class IdempotencyCleanupOptions
{
    public const string SectionName = "IdempotencyCleanup";

    /// <summary>Default 24h: at least one apply/retry cycle.</summary>
    public int TtlHours { get; set; } = 24;

}
