using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;

namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// Admin-facing — DELIBERATELY exposes the internal sync state of every cluster that TemplateQueryResult hides.
public record AdminTemplateQueryResult(
    Guid Id,
    string Name,
    string? Description,
    string Os,           // = OsImageName
    string VmOfferingId, // titan-ops features/catalog/design.md#template-borrows-sizing — the tier this template borrows its base sizing from
    int? DiskGb,         // = RootDiskSizeGb
    bool Listed,         // titan-ops features/catalog/design.md#template-listed — false ⇒ customers neither see nor pick it
    List<AdminTemplateClusterQueryResult> Clusters) // ordered by cluster id
{
    public static AdminTemplateQueryResult From(VmTemplateAggregate t) =>
        new(t.Id, t.Name, t.Description, t.OsImageName, t.VmOfferingId, t.RootDiskSizeGb, t.IsListed,
            t.Clusters.OrderBy(z => z.ClusterId, StringComparer.Ordinal).Select(AdminTemplateClusterQueryResult.From).ToList());
}
