using System.IO;

namespace Titan.Tests.Domain;

public class PartialTypeSourcesTests
{
    private static readonly string Vm = Path.Combine("Aggregates", "Vm");

    [Fact]
    public void Merge_ReadsThePartsOfAType_AsOneSourceUnderItsRootFile()
    {
        var sources = new List<(string File, string Source)>
        {
            (Path.Combine(Vm, "Vm.Observation.cs"), "MarkObserved"),
            (Path.Combine(Vm, "Vm.cs"), "IsDeleted"),
            (Path.Combine(Vm, "Entities", "VmSnapshot.cs"), "VmSnapshot"),
        };

        var merged = PartialTypeSources.Merge(sources);

        Assert.Equal(2, merged.Count);
        Assert.Equal((Path.Combine(Vm, "Vm.cs"), "IsDeleted\nMarkObserved"), merged[0]);
        Assert.Equal((Path.Combine(Vm, "Entities", "VmSnapshot.cs"), "VmSnapshot"), merged[1]);
    }

    [Fact]
    public void Merge_KeepsADottedFileWithoutARootFile_AsItsOwnSource()
    {
        var sources = new List<(string File, string Source)> { (Path.Combine(Vm, "Vm.Start.cs"), "Start") };

        Assert.Equal(sources, PartialTypeSources.Merge(sources));
    }
}
