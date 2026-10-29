using FlixyPix.Core;
using System.Text.Json;

if (args.Length != 6 || args[0] != "--export" || args[2] != "--output" || args[4] != "--source-id")
{
    Console.Error.WriteLine("FlixyPix.CatalogAudit --export catalog.jsonl --output NEW_DIRECTORY --source-id SOURCE_ID");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    FileAccessPolicy.ConfigureCurrent([Path.GetFullPath(args[1]), Path.GetFullPath(args[1]) + ".blobs"]);
    var report = await VerifiedMigration.RunAsync(args[1], args[3], args[5], cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { report.Passed, report.MediaCount, report.SourceUnchanged, report.RepeatIsNoOp, report.PortableRoundTripVerified, report.BackupRestoreVerified, ProjectionErrors = report.ProjectionErrors.Count }));
    return report.Passed ? 0 : 1;
}
catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": migration audit did not pass. Private staging data remains available for diagnosis."); return 1; }
