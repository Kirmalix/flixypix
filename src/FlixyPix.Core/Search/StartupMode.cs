namespace FlixyPix.Core;

/// <summary>A single startup choice backed by the existing library preferences.</summary>
public enum StartupMode { Home, Overview, View, Everything }

public static class StartupModes
{
    public static StartupMode From(CatalogPreferences preferences) => preferences.Home.StartOnHome ? StartupMode.Home
        : !preferences.General.RestoreLastView ? StartupMode.Overview
        : preferences.General.RestoreScope == RestoreScope.View ? StartupMode.View : StartupMode.Everything;
}
