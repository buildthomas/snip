using System.Text;
using Xunit;

namespace Snip.Tests;

public class SourceEncodingTests
{
    // UTF-8 text decoded as Windows-1252 and re-saved turns "—" into "â€”", "×" into "Ã—", etc.
    private static readonly string[] Mojibake = { "â€", "Ã", "Â" };

    [Fact]
    public void SourceFilesHaveNoDoubleEncodedCharacters()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Snip.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var bad = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Mojibake.Any(m => File.ReadAllText(f, Encoding.UTF8).Contains(m)))
            .ToList();

        Assert.Empty(bad);
    }
}
