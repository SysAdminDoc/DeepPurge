using DeepPurge.Core.InstallMonitor;
using Xunit;

namespace DeepPurge.Tests;

public class UsnJournalReaderTests
{
    [Fact]
    public void ResolvePath_reconstructs_parent_chain_from_file_reference_numbers()
    {
        var root = new UsnFileId(5);
        var directory = new UsnFileId(42);
        var nodes = new Dictionary<UsnFileId, UsnPathNode>
        {
            [directory] = new UsnPathNode(directory, root, "DeepPurge"),
        };

        var path = UsnJournalReader.ResolvePath(
            @"C:\",
            directory,
            "setup.exe",
            nodes,
            out var resolved);

        Assert.True(resolved);
        Assert.Equal(Path.Combine(@"C:\", "DeepPurge", "setup.exe"), path);
    }

    [Fact]
    public void ResolvePath_does_not_fabricate_root_relative_path_when_parent_is_unknown()
    {
        var path = UsnJournalReader.ResolvePath(
            @"C:\",
            new UsnFileId(99),
            "setup.exe",
            new Dictionary<UsnFileId, UsnPathNode>(),
            out var resolved);

        Assert.False(resolved);
        Assert.StartsWith("<unresolved:", path, StringComparison.Ordinal);
    }
}
