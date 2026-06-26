using DeepPurge.Core.Diagnostics;
using Xunit;

namespace DeepPurge.Tests;

public class SizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-1, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "2 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.00 GB")]
    [InlineData(5368709120, "5.00 GB")]
    public void Format_produces_expected_output(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }
}
