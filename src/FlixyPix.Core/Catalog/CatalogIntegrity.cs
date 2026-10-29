using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;
public sealed record CatalogIntegrityResult(bool IsValid,IReadOnlyList<string> Issues);
public sealed partial class CatalogRepository
{
 public CatalogIntegrityResult CheckIntegrity(CancellationToken token=default)
 {lock(gate){ThrowIfDisposed();return CheckIntegrityCore(null,token);}}
 private CatalogIntegrityResult CheckIntegrityCore(SqliteTransaction? tx,CancellationToken token)
 {
  token.ThrowIfCancellationRequested();var issues=new List<string>();
  using(var command=Command("PRAGMA integrity_check",tx))
  {using var cancel=token.Register(command.Cancel);using var reader=command.ExecuteReader();while(reader.Read()){token.ThrowIfCancellationRequested();var value=reader.GetString(0);if(value!="ok")issues.Add(value);}}
  using(var command=Command("PRAGMA foreign_key_check",tx))
  {using var cancel=token.Register(command.Cancel);using var reader=command.ExecuteReader();while(reader.Read()){token.ThrowIfCancellationRequested();issues.Add($"Foreign key: {reader.GetString(0)} row {reader.GetValue(1)} references {reader.GetString(2)}.");}}
  return new(issues.Count==0,issues);
 }
}
