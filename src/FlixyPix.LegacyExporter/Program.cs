using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace FlixyPix.LegacyExporter
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 128 };

        // Loads the SQL CE 4.0 provider on a private copy of the catalog. The preservation
        // project runs it only inside the offline Sandbox (--offline-sandbox); FlixyPix itself
        // starts it for a user's own catalog (--import). Both modes behave identically.
        // SQL CE is not referenced at compile time and not shipped (see SqlCeRuntime).
        private static int Main(string[] args)
        {
            var arguments = new List<string>(args); string runtimeFolder = null;
            var runtimeAt = arguments.IndexOf("--runtime");
            if (runtimeAt >= 0 && runtimeAt + 1 < arguments.Count) { runtimeFolder = arguments[runtimeAt + 1]; arguments.RemoveRange(runtimeAt, 2); }
            if (arguments.Count != 3 || (arguments[0] != "--offline-sandbox" && arguments[0] != "--import"))
            {
                Console.Error.WriteLine("Usage: FlixyPix.LegacyExporter.exe --import [--runtime folder] input.sdf output.jsonl");
                return 2;
            }
            var source = Path.GetFullPath(arguments[1]); var output = Path.GetFullPath(arguments[2]);
            if (!File.Exists(source)) { Console.Error.WriteLine("Database not found."); return 2; }
            if (File.Exists(output) || Directory.Exists(output) || Directory.Exists(output + ".blobs") || File.Exists(output + ".blobs"))
            { Console.Error.WriteLine("Output or sidecar destination already exists; refusing to replace it."); return 2; }
            SqlCeRuntime runtime;
            try { runtime = SqlCeRuntime.Load(runtimeFolder); }
            catch (Exception error) when (error is FileNotFoundException || error is FileLoadException || error is BadImageFormatException)
            {
                Console.Error.WriteLine(error.GetType().Name + ": SQL Server Compact 4.0 was not found; category=runtime-missing");
                return 3;
            }
            Console.WriteLine("runtime " + runtime.Provider.GetName().Version + " " + runtime.Location);
            var work = Path.Combine(Path.GetTempPath(), "flixypix-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var temporaryOutput = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var temporaryBlobs = temporaryOutput + ".blobs";
            try
            {
                var before = Hash(source); var copy = Path.Combine(work, "catalog.sdf");
                File.Copy(source, copy);
                if (before != Hash(copy)) throw new IOException("Copied source digest differs.");
                var blobs = new BlobSidecarWriter(temporaryBlobs);
                // The supplied preservation catalog exceeds SQLCE's default
                // 256 MiB connection limit. This changes the guest copy's open
                // limit only; neither source schema nor source bytes are edited.
                var settings = new DbConnectionStringBuilder { ["Data Source"] = copy, ["Persist Security Info"] = false, ["Max Database Size"] = 4091 };
                // The environment overrides the built-in password for preservation scenarios only.
                var password = ProductIdentity.ReadCatalogPassword(Environment.GetEnvironmentVariable) ?? OriginalCatalogFormat.DefaultPassword;
                using (var connection = OpenCatalog(runtime, settings, password))
                {
                    using (var writer = new StreamWriter(temporaryOutput, false, new UTF8Encoding(false)))
                    {
                        Write(writer, new { type = "header", format = "flixypix-legacy", version = 2, sourceSha256 = before, sourceName = Path.GetFileName(source), sourceLength = new FileInfo(source).Length });
                        var tables = new List<string>();
                        using (var command = Command(connection, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'TABLE' ORDER BY TABLE_NAME"))
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) tables.Add(reader.GetString(0));
                        var counts = new Dictionary<string, long>(); long total = 0;
                        foreach (var table in tables)
                        {
                            var columns = new List<object>();
                            using (var command = Command(connection, "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ORDINAL_POSITION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@table ORDER BY ORDINAL_POSITION"))
                            {
                                var parameter = command.CreateParameter(); parameter.ParameterName = "@table"; parameter.Value = table; command.Parameters.Add(parameter);
                                using (var reader = command.ExecuteReader())
                                    while (reader.Read()) columns.Add(new { name = reader.GetString(0), dataType = reader.GetString(1), nullable = reader.GetString(2) == "YES", ordinal = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) });
                            }
                            Write(writer, new { type = "schema", table, columns }); long count = 0;
                            using (var command = Command(connection, "SELECT * FROM [" + table.Replace("]", "]]") + "]"))
                            using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                            {
                                while (reader.Read())
                                {
                                    var values = new Dictionary<string, object>(StringComparer.Ordinal);
                                    for (var i = 0; i < reader.FieldCount; i++)
                                    {
                                        object value;
                                        if (reader.IsDBNull(i)) value = null;
                                        else if (reader.GetFieldType(i) == typeof(byte[]))
                                        {
                                            var column = i;
                                            var length = reader.GetBytes(column, 0, null, 0, 0);
                                            value = blobs.Write(length, (offset, buffer, start, count) => reader.GetBytes(column, offset, buffer, start, count));
                                        }
                                        else value = TypedValue(reader.GetValue(i));
                                        values.Add(reader.GetName(i), value);
                                    }
                                    Write(writer, new { type = "row", table, values }); count++;
                                }
                            }
                            counts.Add(table, count); total += count;
                            Console.WriteLine("progress " + counts.Count.ToString(CultureInfo.InvariantCulture) + "/" + tables.Count.ToString(CultureInfo.InvariantCulture) + " " + total.ToString(CultureInfo.InvariantCulture));
                        }
                        Write(writer, new { type = "footer", tables = tables.Count, rows = total, tableRows = counts, blobCount = blobs.Count, blobBytes = blobs.Bytes });
                    }
                }
                if (before != Hash(source)) throw new IOException("The source database changed during export; result was not published.");
                BlobSidecarWriter.Publish(temporaryOutput, temporaryBlobs, output);
                Console.WriteLine("Export complete. Original source unchanged: " + before);
                return 0;
            }
            // Provider messages can contain connection details. Report the failure class only.
            catch (Exception thrown)
            {
                var error = thrown is TargetInvocationException && thrown.InnerException != null ? thrown.InnerException : thrown;
                Console.Error.WriteLine(error.GetType().Name + ": Export did not complete; no complete JSONL was published. HResult=" + error.HResult.ToString(CultureInfo.InvariantCulture));
                // Native parts of the runtime (sqlceme40.dll and the others) missing or of another version.
                if (error is DllNotFoundException || error is BadImageFormatException) Console.Error.WriteLine("SQLCE category=runtime-missing");
                // SqlCeException.Errors, read by name: the provider is not referenced at compile time.
                if (error is DbException && error.GetType().GetProperty("Errors")?.GetValue(error) is IEnumerable details)
                    foreach (var detail in details)
                    {
                        var message = ((detail.GetType().GetProperty("Message")?.GetValue(detail) as string) ?? "").ToLowerInvariant();
                        var native = detail.GetType().GetProperty("NativeError")?.GetValue(detail);
                        var category = message.Contains("password") ? "password" : message.Contains("maximum") || message.Contains("size") ? "database-size" :
                            message.Contains("corrupt") ? "integrity" : message.Contains("version") ? "version" : message.Contains("permission") || message.Contains("access") ? "access" : "unclassified";
                        Console.Error.WriteLine("SQLCE NativeError=" + Convert.ToString(native, CultureInfo.InvariantCulture) + "; category=" + category);
                    }
                return 1;
            }
            finally
            {
                if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
                if (Directory.Exists(temporaryBlobs)) Directory.Delete(temporaryBlobs, true);
                // Only the exporter-owned, freshly created directory is removed.
                if (Directory.Exists(work)) Directory.Delete(work, true);
            }
        }

        // Like the original program: try the program password first, then an unprotected catalog.
        private static DbConnection OpenCatalog(SqlCeRuntime runtime, DbConnectionStringBuilder settings, string password)
        {
            if (!string.IsNullOrEmpty(password))
            {
                settings["Password"] = password;
                try { return runtime.Open(settings); }
                catch (DbException) { settings.Remove("Password"); }
            }
            return runtime.Open(settings);
        }

        private static DbCommand Command(DbConnection connection, string text)
        {
            var command = connection.CreateCommand();
            command.CommandText = text;
            return command;
        }

        private static object TypedValue(object value)
        {
            if (value == DBNull.Value) return null;
            if (value is byte[]) throw new InvalidOperationException("Binary values require the bounded sidecar writer.");
            if (value is decimal) return new Dictionary<string, object> { ["$decimal"] = ((decimal)value).ToString(CultureInfo.InvariantCulture) };
            if (value is DateTime) return new Dictionary<string, object> { ["$datetime"] = ((DateTime)value).ToString("O", CultureInfo.InvariantCulture) };
            if (value is Guid) return new Dictionary<string, object> { ["$guid"] = value.ToString() };
            return value;
        }
        private static void Write(TextWriter writer, object record) { writer.WriteLine(Json.Serialize(record)); }
        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(stream).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }
}
