using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Documents are stored without indentation, media documents also without null properties.</summary>
public sealed class DocumentCompactionTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-compact-" + Guid.NewGuid().ToString("N"));
 public DocumentCompactionTests() => Directory.CreateDirectory(root);
 private string Database => Path.Combine(root, "catalog.db");

 private object? Scalar(string sql, string? id = null)
 {
  using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
  connection.Open(); connection.CreateCollation("EFP_ORDINAL", string.CompareOrdinal);
  using var command = connection.CreateCommand(); command.CommandText = sql;
  if (id != null) command.Parameters.AddWithValue("$id", id);
  return command.ExecuteScalar();
 }

 [Fact]
 public void DocumentsAreStoredCompactAndMediaWithoutNulls()
 {
  using (var catalog = new CatalogRepository(Database))
  {
   var item = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video, Title = "Abend" });
   catalog.UpsertScene(new() { MediaId = item.Id, StartSeconds = 1, EndSeconds = 2, Title = "Szene" });
   var stored = (string)Scalar("SELECT document FROM media")!;
   Assert.DoesNotContain("\n", stored); Assert.DoesNotContain(":null", stored); Assert.DoesNotContain("description", stored);
   Assert.DoesNotContain("\n", (string)Scalar("SELECT document FROM scenes")!);
   var read = catalog.GetMedia(item.Id)!; Assert.Null(read.Description); Assert.Equal("Abend", read.Title); Assert.Equal(MediaKind.Video, read.Kind);
   // Clearing a value removes the property.
   catalog.Upsert(read with { Description = "Text" }); catalog.Upsert(catalog.GetMedia(item.Id)! with { Description = null });
   Assert.Null(catalog.GetMedia(item.Id)!.Description); Assert.DoesNotContain("description", (string)Scalar("SELECT document FROM media")!);
  }
 }

 public void Dispose()
 {
  try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
 }
}
