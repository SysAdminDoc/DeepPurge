using System.Reflection;
using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

public class SecureDeleteTests
{
    [Fact]
    public void SecureDelete_does_not_expose_free_space_wipe_api()
    {
        var method = typeof(SecureDelete).GetMethod(
            "WipeFreeSpaceAsync",
            BindingFlags.Public | BindingFlags.Static);

        Assert.Null(method);
    }
}
