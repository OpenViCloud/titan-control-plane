using Titan.Domain.Aggregates.VmTemplate;

namespace Titan.Tests.Fakes;

// VmTemplate.ImageUrl and an unlisted IsListed come only from the seed (HasData); no method sets them, so tests set
// them the way EF does — through the private setter.
public static class TemplateSeedFields
{
    public static VmTemplate Unlisted(VmTemplate template)
    {
        typeof(VmTemplate).GetProperty(nameof(VmTemplate.IsListed))!.SetValue(template, false);
        return template;
    }

    public static VmTemplate WithImageUrl(VmTemplate template, string imageUrl)
    {
        typeof(VmTemplate).GetProperty(nameof(VmTemplate.ImageUrl))!.SetValue(template, imageUrl);
        return template;
    }
}
