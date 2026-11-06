using System.IO;

namespace Titan.Tests.Domain;

// A source scan that keys a type by its file reads <Type>.<Part>.cs as part of <Type>.cs in the same folder
// (titan-ops architecture/code-structure.md#domain-tree): a rule that needs a property of the root and a method of a
// part in one text (IsDeleted and MarkObserved of Vm) would otherwise skip the type and stay green.
internal static class PartialTypeSources
{
    public static List<(string File, string Source)> Merge(List<(string File, string Source)> sources)
    {
        var present = new HashSet<string>();
        foreach (var (file, _) in sources)
            present.Add(file);

        var merged = new List<(string File, string Source)>();
        foreach (var (file, source) in sources)
        {
            string? rootFile = RootFileOf(file);
            if (rootFile != null && present.Contains(rootFile))
                continue;

            var text = source;
            foreach (var (other, otherSource) in sources)
            {
                if (RootFileOf(other) == file)
                    text += "\n" + otherSource;
            }
            merged.Add((file, text));
        }
        return merged;
    }

    // Vm.Start.cs ⇒ Vm.cs in the same folder; null for a file without a part in its name.
    private static string? RootFileOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        int dot = name.IndexOf('.');
        if (dot < 0)
            return null;
        return Path.Combine(Path.GetDirectoryName(file)!, name[..dot] + ".cs");
    }
}
