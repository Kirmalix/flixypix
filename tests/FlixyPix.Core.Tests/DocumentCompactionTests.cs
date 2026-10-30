using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Schema 16: documents without indentation, media without null properties and without the copy of the
/// original's row; the original's dates still reach the query table.</summary>
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

 [Fact]
 public void Version15CatalogDropsTheCopiedRowAndKeepsTheOriginalsDates()
 {
  Guid id;
  using (var catalog = new CatalogRepository(Database))
  {
   id = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video, LegacyId = "movies:7",
    Extra = new() { ["legacy.table"] = "movies", ["legacy.sourceSha256"] = new string('a', 64),
     ["legacy.values"] = """{"ID":7,"addedDate":{"$datetime":"2019-05-04T10:00:00"},"filedate":{"$datetime":"2018-01-02T03:04:05"},"categoryid":3}""" } }).Id;
  }
  Scalar("UPDATE media SET document=json_remove(document,'$.originalAddedAt','$.originalFileDate'); PRAGMA user_version=15;");
  using (var catalog = new CatalogRepository(Database))
  {
   var item = catalog.GetMedia(id)!;
   Assert.Equal("2019-05-04T10:00:00", item.OriginalAddedAt); Assert.Equal("2018-01-02T03:04:05", item.OriginalFileDate);
   Assert.False(item.Extra.ContainsKey("legacy.values")); Assert.False(item.Extra.ContainsKey("legacy.sourceSha256"));
   Assert.Equal("movies", item.Extra["legacy.table"]);
   Assert.Contains("before-v17-v15", catalog.MigrationBackupPath!);
  }
  Assert.Equal(Scalar("SELECT julianday('2018-01-02T03:04:05')"), Scalar("SELECT file_date FROM media_query WHERE id=$id", id.ToString()));
  Assert.Equal(Scalar("SELECT julianday('2019-05-04T10:00:00')"), Scalar("SELECT imported FROM media_query WHERE id=$id", id.ToString()));
  Assert.Equal(17L, Scalar("PRAGMA user_version"));
 }

 public void Dispose()
 {
  try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
 }
}
