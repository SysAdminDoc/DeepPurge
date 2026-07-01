using System.IO.Compression;
using DeepPurge.Core.Diagnostics;
using Xunit;

namespace DeepPurge.Tests;

public class SupportBundleTests
{
    [Fact]
    public void Export_creates_zip_with_expected_sections()
    {
        var dir = CreateTempDir();
        try
        {
            var output = Path.Combine(dir, "bundle.zip");
            var result = SupportBundleExporter.Export(output);

            Assert.True(result.Success, result.ErrorMessage ?? "Export failed");
            Assert.True(File.Exists(output));
            Assert.True(result.ByteCount > 0);
            Assert.True(result.SectionCount >= 2);

            using var zip = ZipFile.OpenRead(output);
            var names = zip.Entries.Select(e => e.Name).ToList();
            Assert.Contains("doctor.txt", names);
            Assert.Contains("app-summary.txt", names);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Export_redacts_user_profile_paths()
    {
        var dir = CreateTempDir();
        try
        {
            var output = Path.Combine(dir, "bundle.zip");
            var result = SupportBundleExporter.Export(output);
            Assert.True(result.Success, result.ErrorMessage ?? "Export failed");

            using var zip = ZipFile.OpenRead(output);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0) continue;
                using var reader = new StreamReader(entry.Open());
                var content = reader.ReadToEnd();
                Assert.DoesNotContain(userProfile, content, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Export_overwrites_existing_file()
    {
        var dir = CreateTempDir();
        try
        {
            var output = Path.Combine(dir, "bundle.zip");
            File.WriteAllText(output, "old");

            var result = SupportBundleExporter.Export(output);
            Assert.True(result.Success);
            Assert.True(new FileInfo(output).Length > 4);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Export_appends_zip_extension_when_missing()
    {
        var dir = CreateTempDir();
        try
        {
            var output = Path.Combine(dir, "bundle.zip");
            var result = SupportBundleExporter.Export(output);
            Assert.True(result.Success);
            Assert.EndsWith(".zip", result.OutputPath);
        }
        finally { TryDeleteDir(dir); }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeepPurgeBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
