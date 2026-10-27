namespace Titan.Application.Abstractions;

public interface IComputeCatalogProvider
{
    Task<List<OsImageInfo>> ListOsImagesAsync(CancellationToken ct = default);
}

public record ClusterInfo(string Id, string Name);
public record OsImageInfo(string Id, string Name, int SizeGb);
