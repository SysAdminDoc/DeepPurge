using DeepPurge.Core.Models;
using DeepPurge.Core.Export;
using Xunit;

namespace DeepPurge.Tests;

public class SnapshotTests
{
    [Fact]
    public Task ProgramExporter_csv_format()
    {
        var programs = new List<InstalledProgram>
        {
            new() { DisplayName = "Test App", DisplayVersion = "1.0", Publisher = "Test Corp", InstallLocation = @"C:\Apps\Test" },
            new() { DisplayName = "Another App", DisplayVersion = "2.3.1", Publisher = "Another Inc", InstallLocation = @"D:\Programs\Another" },
        };
        var tmpFile = Path.GetTempFileName() + ".csv";
        try
        {
            ProgramExporter.ExportToCsv(programs, tmpFile);
            var content = File.ReadAllText(tmpFile);
            return VerifyXunit.Verifier.Verify(content);
        }
        finally { try { File.Delete(tmpFile); } catch { } }
    }

    [Fact]
    public Task ProgramExporter_json_format()
    {
        var programs = new List<InstalledProgram>
        {
            new() { DisplayName = "Test App", DisplayVersion = "1.0", Publisher = "Test Corp" },
        };
        var tmpFile = Path.GetTempFileName() + ".json";
        try
        {
            ProgramExporter.ExportToJson(programs, tmpFile);
            var content = File.ReadAllText(tmpFile);
            return VerifyXunit.Verifier.Verify(content);
        }
        finally { try { File.Delete(tmpFile); } catch { } }
    }
}
