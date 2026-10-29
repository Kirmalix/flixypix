using System;
#nullable enable

namespace FlixyPix
{
    /// <summary>Current identity and the deliberately retained read-compatibility identifiers.</summary>
    public static class ProductIdentity
    {
        public const string Name = "FlixyPix";
        public const string ProjectUrl = "https://github.com/Kirmalix/flixypix";
        public const string PreviousApplicationDirectory = "EazyFlixPix.Reborn";
        public const string PreviousFormatPrefix = "eazyflixpix";
        public const string PasswordVariable = "FLIXYPIX_SQLCE_PASSWORD";
        public const string PreviousPasswordVariable = "EZFP_SQLCE_PASSWORD";
        public const string BackupExtension = ".flixypixbackup";
        public const string PreviousBackupExtension = ".ezfpbackup";
        public const string OrdinalCollation = "EFP_ORDINAL";

        public static string? ReadCatalogPassword(Func<string,string?> reader)
        {
            return reader(PasswordVariable) ?? reader(PreviousPasswordVariable);
        }

        public static bool IsFormat(string? value, string current)
        {
            if (string.Equals(value, current, StringComparison.Ordinal)) return true;
            return current.StartsWith("flixypix-", StringComparison.Ordinal) &&
                string.Equals(value, PreviousFormatPrefix + current.Substring("flixypix".Length), StringComparison.Ordinal);
        }
    }
}
