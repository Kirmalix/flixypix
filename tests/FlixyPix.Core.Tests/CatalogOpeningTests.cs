using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Opening with long work: the plan read beforehand, progress per phase, cancelling without a trace, and the
/// note in the maintenance log.</summary>
public sealed class CatalogOpeningTests : IDisposable
{
 private const string Password = "geheimes-passwort";
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-opening-" + Guid.NewGuid().ToString("N"));
 private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHucAAAAASUVORK5CYII=");
 public CatalogOpeningTests() => Directory.CreateDirectory(root);
 private string Database => Path.Combine(root, "catalog.db");

 private sealed class Steps(Action<CatalogOpenProgress> report) : IProgress<CatalogOpenProgress>
 {
  public void Report(CatalogOpenProgress value) => report(value);
 }

 private void SetVersion(long version)
 {
  using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
  connection.Open(); using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA user_version={version}"; command.ExecuteNonQuery();
 }

 private long Version()
 {
  using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
  connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version"; return (long)command.ExecuteScalar()!;
 }

 private void OlderLibrary(int media = 5)
 {
  using (var catalog = new CatalogRepository(Database))
   for (var index = 0; index < media; index++) catalog.Upsert(new() { Path = Path.Combine(root, $"m{index}.mp4"), Kind = MediaKind.Video });
  SetVersion(15);
 }

 [Fact]
 public void CurrentAndNewLibrariesNeedNoWork()
 {
  Assert.False(CatalogRepository.Inspect(Database).HasWork); // not created yet
  new CatalogRepository(Database).Dispose();
  var plan = CatalogRepository.Inspect(Database);
  Assert.False(plan.HasWork); Assert.False(plan.Upgrades); Assert.Equal(0, plan.RequiredBytes);
  using var catalog = new CatalogRepository(Database); Assert.False(catalog.UpgradedOnOpen);
 }

 [Fact]
 public void OlderLibraryIsPlannedThenConvertedPhaseByPhaseAndLogged()
 {
  OlderLibrary();
  var plan = CatalogRepository.Inspect(Database);
  Assert.True(plan.Upgrades); Assert.Equal(15, plan.FromVersion); Assert.Equal(5, plan.Media);
  Assert.Equal([CatalogOpenPhase.SafetyCopy, CatalogOpenPhase.Entries], plan.Phases);
  Assert.True(plan.RequiredBytes > plan.CatalogBytes); Assert.True(plan.HasRoom); Assert.True(plan.Estimate > TimeSpan.Zero);
  var steps = new List<CatalogOpenProgress>();
  using (var catalog = new CatalogRepository(Database, null, new Steps(steps.Add)))
  {
   Assert.True(catalog.UpgradedOnOpen);
   var log = catalog.GetMaintenanceLog();
   Assert.Equal(15, log.LastUpgradeFrom); Assert.Equal(16, log.LastUpgradeTo); Assert.NotNull(log.LastUpgradeAt); Assert.True(log.LastUpgradeSeconds >= 0);
  }
  Assert.Equal(plan.Phases, steps.Select(s => s.Phase).Distinct());
  var copy = steps.Last(s => s.Phase == CatalogOpenPhase.SafetyCopy); Assert.True(copy.Total > 0 && copy.Done == copy.Total);
  var entries = steps.Last(s => s.Phase == CatalogOpenPhase.Entries); Assert.Equal((5L, 5L), (entries.Done, entries.Total));
  Assert.False(CatalogRepository.Inspect(Database).HasWork);
 }

 [Fact]
 public void PicturesOfAVersion14LibraryAreAPhaseOfTheirOwn()
 {
  using (var catalog = new CatalogRepository(Database))
  {
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video });
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
  }
  SetVersion(14);
  var plan = CatalogRepository.Inspect(Database);
  Assert.Equal([CatalogOpenPhase.SafetyCopy, CatalogOpenPhase.Pictures, CatalogOpenPhase.Entries], plan.Phases);
  Assert.Equal(1, plan.Pictures);
 }

 [Fact]
 public void CancellingLeavesTheLibraryAsItWasWithoutASafetyCopy()
 {
  OlderLibrary(1200);
  using var cancel = new CancellationTokenSource();
  var progress = new Steps(step => { if (step.Phase == CatalogOpenPhase.Entries && step.Done > 0) cancel.Cancel(); });
  Assert.ThrowsAny<OperationCanceledException>(() => new CatalogRepository(Database, null, progress, cancel.Token));
  Assert.Equal(15, Version());
  Assert.Empty(Directory.GetFiles(root, "catalog.db.before-*"));
  Assert.True(CatalogRepository.Inspect(Database).Upgrades);
  using var again = new CatalogRepository(Database);
  Assert.True(again.UpgradedOnOpen); Assert.Equal(1200, again.Search().Count);
 }

 [Fact]
 public void TooLittleRoomIsSaidBeforehand()
 {
  OlderLibrary();
  var plan = CatalogRepository.Inspect(Database) with { FreeBytes = 1024 };
  Assert.False(plan.HasRoom);
 }

 [Fact]
 public void AnInterruptedPictureEncryptionIsPlannedForTheUnlockedLibrary()
 {
  using (var catalog = new CatalogRepository(Database))
  {
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video });
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
  }
  CatalogEncryption.EnableEncryption(Database, Password);
  // As if the switch back to plain files had stopped right at its start.
  var state = Path.Combine(root, "catalog-assets", "store.json");
  File.WriteAllText(state, File.ReadAllText(state).Replace("\"encryptFiles\": true", "\"encryptFiles\": false").Replace("\"filesEncrypted\": true", "\"filesEncrypted\": null"));
  using var key = CatalogEncryption.Unlock(Database, Password);
  var plan = CatalogRepository.Inspect(Database, key);
  Assert.False(plan.Upgrades); Assert.Equal([CatalogOpenPhase.PictureEncryption], plan.Phases);
  var steps = new List<CatalogOpenProgress>();
  using (var catalog = new CatalogRepository(Database, key, new Steps(steps.Add))) Assert.False(catalog.EncryptsPictures);
  Assert.Contains(steps, s => s.Phase == CatalogOpenPhase.PictureEncryption && s.Done == s.Total && s.Total == 1);
  Assert.False(CatalogRepository.Inspect(Database, key).HasWork);
 }

 public void Dispose()
 {
  try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
 }
}
