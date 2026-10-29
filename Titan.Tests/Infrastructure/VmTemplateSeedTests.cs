using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The catalog's templates exist only as seed rows (titan-ops features/catalog/design.md#template-seed-in-migration), so
// which image a template comes from and whether customers see it is decided here, and pinned here.
public class VmTemplateSeedTests
{
    private const string Ubuntu = "0a5e0000-0000-4000-8000-000000000002";
    private const string Alma = "0a5e0000-0000-4000-8000-000000000003";
    private const string Rocky = "0a5e0000-0000-4000-8000-000000000005";

    private static IEntityType TemplateType()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"seed-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        // Seed data lives only in the design-time model, the one migrations are built from.
        return db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(VmTemplate))!;
    }

    private static IDictionary<string, object?> SeedRow(string id) =>
        TemplateType().GetSeedData().Single(row => (Guid)row[nameof(VmTemplate.Id)]! == Guid.Parse(id));

    // The distro's own image: it ships sshd and cloud-init, which the image server's ubuntu/22.04 does not
    // (titan-ops features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public void Ubuntu_ComesFromTheDistrosOwnImage_PerArchitecture_AndIsListed()
    {
        var row = SeedRow(Ubuntu);

        Assert.Equal("https://cloud-images.ubuntu.com/releases/jammy/release/ubuntu-22.04-server-cloudimg-{arch}.img",
            row[nameof(VmTemplate.ImageUrl)]);
        Assert.Equal(true, row[nameof(VmTemplate.IsListed)]);
    }

    // Nobody can log in to them yet (titan-ops features/catalog/design.md#template-listed); the rows stay, VMs point at them.
    [Theory]
    [InlineData(Alma)]
    [InlineData(Rocky)]
    public void AlmaAndRocky_AreNotListed_AndKeepPullingFromTheImageServer(string id)
    {
        var row = SeedRow(id);

        Assert.Equal(false, row[nameof(VmTemplate.IsListed)]);
        Assert.Null(row[nameof(VmTemplate.ImageUrl)]);
    }

    [Fact]
    public void Columns_AreImageUrlNullable_AndIsListedRequired()
    {
        var type = TemplateType();
        var imageUrl = type.FindProperty(nameof(VmTemplate.ImageUrl))!;
        var isListed = type.FindProperty(nameof(VmTemplate.IsListed))!;

        Assert.Equal("image_url", imageUrl.GetColumnName());
        Assert.True(imageUrl.IsNullable);
        Assert.Equal("is_listed", isListed.GetColumnName());
        Assert.False(isListed.IsNullable);
        // A store default would make EF skip writing false (the CLR default) and the database put true instead.
        Assert.Null(isListed.FindAnnotation(RelationalAnnotationNames.DefaultValue));
        Assert.Null(isListed.FindAnnotation(RelationalAnnotationNames.DefaultValueSql));
    }
}
