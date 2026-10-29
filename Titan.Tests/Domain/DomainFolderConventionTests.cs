using System.IO;
using System.Text.RegularExpressions;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The two folder-layout invariants of Titan.Domain (titan-ops architecture/code-structure.md#domain-tree), checked
// against the source tree: a doc cannot fail, this can.
public class DomainFolderConventionTests
{
    [Fact] // titan-ops architecture/code-structure.md#folder-name-collision
    public void NoFolderName_CollidesWithATypeItDoesNotOwn()
    {
        // Compute/Vm/ holding class Vm is fine: every shape it breaks (e.g. a partially-qualified reference)
        // fails loudly at compile time. A folder named after a type declared ELSEWHERE is the CS0118 landmine.
        var domainDir = Path.Combine(FindRepoDir(), "Titan.Domain");

        // Oracle comes from the compiled assembly, not from parsing the same files we are checking.
        var typesByName = typeof(NotFoundException).Assembly
            .GetExportedTypes()
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Namespace ?? "").ToList(), StringComparer.Ordinal);
        Assert.NotEmpty(typesByName);

        var folders = SourceFolders(domainDir).ToList();
        Assert.NotEmpty(folders);

        var offenders = new List<string>();
        foreach (var dir in folders)
        {
            var name = Path.GetFileName(dir);
            if (!typesByName.TryGetValue(name, out var namespaces)) continue;

            var rel = Path.GetRelativePath(domainDir, dir);
            var folderNamespace = "Titan.Domain." + rel.Replace(Path.DirectorySeparatorChar, '.');

            // Owning the type means the type is declared in this folder's own namespace.
            if (!namespaces.Contains(folderNamespace, StringComparer.Ordinal))
                offenders.Add($"{rel} (type {name} lives in {string.Join("/", namespaces)})");
        }

        Assert.True(offenders.Count == 0,
            "Folder name(s) collide with a public Titan.Domain type declared ELSEWHERE — that is the "
            + "CS0118 landmine (C# resolves the namespace before the type). A folder may only share "
            + "its name with the type it contains: " + string.Join(", ", offenders.OrderBy(x => x, StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryNamespace_MatchesItsFolderPath()
    {
        var domainDir = Path.Combine(FindRepoDir(), "Titan.Domain");
        var files = SourceFiles(domainDir).ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var declared = DeclaredNamespace(file);
            if (declared is null)
            {
                offenders.Add($"{Path.GetRelativePath(domainDir, file)}: no file-scoped namespace");
                continue;
            }

            var folder = Path.GetDirectoryName(Path.GetRelativePath(domainDir, file))!;
            var expected = folder.Length == 0
                ? "Titan.Domain"
                : "Titan.Domain." + folder.Replace(Path.DirectorySeparatorChar, '.');

            if (declared != expected)
                offenders.Add($"{Path.GetRelativePath(domainDir, file)}: declares '{declared}', folder says '{expected}'");
        }

        Assert.True(offenders.Count == 0,
            "Namespace does not match folder path: " + string.Join(" | ", offenders));
    }

    private static IEnumerable<string> SourceFolders(string root) =>
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(NotBuildOutput);

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput);

    private static bool NotBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return !path.Contains($"{sep}obj{sep}") && !path.Contains($"{sep}bin{sep}")
            && !path.EndsWith($"{sep}obj") && !path.EndsWith($"{sep}bin");
    }

    private static string? DeclaredNamespace(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var m = Regex.Match(line, @"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]");
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    private static string FindRepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
