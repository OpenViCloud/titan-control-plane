using VmTemplateAggregate = Titan.Domain.Aggregates.Catalog.VmTemplate.VmTemplate;

namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// Customer-facing — the internal sync state and ExternalTemplateId are NOT exposed.
//
// No sizing and no VmOfferingId (titan-ops features/catalog/design.md#template-borrows-sizing): the template's tier is
// only the provider-side base, and the offering the customer picks overrides it at
// instantiate, so publishing it would describe no customer's VM.
public record TemplateQueryResult(
    Guid Id,
    string Name,
    string? Description,
    string Os,          // = OsImageName
    int? DiskGb)        // = RootDiskSizeGb (null = image native size)
{
    public static TemplateQueryResult From(VmTemplateAggregate t) =>
        new(t.Id, t.Name, t.Description, t.OsImageName, t.RootDiskSizeGb);
}
