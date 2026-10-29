using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FlixyPix.LegacyExporter
{
    /// <summary>Finds SQL Server Compact 4.0 at run time. FlixyPix does not ship it: the exporter uses the private copy
    /// of an installation of the original (--runtime), a copy next to itself (offline Sandbox guest), or the system-wide
    /// Microsoft SQL Server Compact 4.0 SP1 in the global assembly cache, in that order.</summary>
    internal sealed class SqlCeRuntime
    {
        internal const string AssemblyFile = "System.Data.SqlServerCe.dll";
        private const string Installed = "System.Data.SqlServerCe, Version=4.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91";
        private static readonly byte[] PublicKeyToken = { 0x89, 0x84, 0x5d, 0xcd, 0x80, 0x80, 0xcc, 0x91 };
        private readonly Type connectionType;

        private SqlCeRuntime(Assembly provider)
        {
            var name = provider.GetName();
            if (name.Name != "System.Data.SqlServerCe" || name.Version.Major != 4 || !(name.GetPublicKeyToken() ?? new byte[0]).SequenceEqual(PublicKeyToken))
                throw new FileLoadException("Not a SQL Server Compact 4.0 provider.");
            Provider = provider;
            connectionType = provider.GetType("System.Data.SqlServerCe.SqlCeConnection", true);
        }

        internal Assembly Provider { get; }
        internal string Location => string.IsNullOrEmpty(Provider.Location) ? "global assembly cache" : Path.GetDirectoryName(Provider.Location);

        internal static SqlCeRuntime Load(string folder)
        {
            foreach (var candidate in new[] { folder, AppDomain.CurrentDomain.BaseDirectory })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                var path = Path.Combine(Path.GetFullPath(candidate), AssemblyFile);
                if (File.Exists(path)) return new SqlCeRuntime(Assembly.LoadFrom(path));
            }
            return new SqlCeRuntime(Assembly.Load(Installed));
        }

        internal DbConnection Open(DbConnectionStringBuilder settings)
        {
            var connection = (DbConnection)Activator.CreateInstance(connectionType, settings.ConnectionString);
            try { connection.Open(); return connection; }
            catch { connection.Dispose(); throw; }
        }
    }
}
