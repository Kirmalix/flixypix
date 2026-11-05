using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StartupModeTests
{
    [Theory]
    [InlineData(true, false, RestoreScope.View, StartupMode.Home)]
    [InlineData(true, true, RestoreScope.All, StartupMode.Home)]
    [InlineData(false, false, RestoreScope.All, StartupMode.Overview)]
    [InlineData(false, true, RestoreScope.View, StartupMode.View)]
    [InlineData(false, true, RestoreScope.All, StartupMode.Everything)]
    public void LegacyOptionsResolveToOneStartTarget(bool home, bool restore, RestoreScope scope, StartupMode expected)
    {
        var preferences = new CatalogPreferences();
        Assert.Equal(expected, StartupModes.From(preferences with
        {
            Home = preferences.Home with { StartOnHome = home },
            General = preferences.General with { RestoreLastView = restore, RestoreScope = scope }
        }));
    }
}
