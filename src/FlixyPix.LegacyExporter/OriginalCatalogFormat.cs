namespace FlixyPix.LegacyExporter
{
    /// <summary>Fixed parameters of the original catalog format.</summary>
    internal static class OriginalCatalogFormat
    {
        /// <summary>The original program opens every catalog it creates with this one built-in password; users never chose it.
        /// It is publicly known and kept here only so catalogs can be imported. It protects nothing and is not a secret.</summary>
        internal const string DefaultPassword = "loboRiggs_traiscottDug8hughestripe";
    }
}
