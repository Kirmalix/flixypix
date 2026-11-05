using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record ImageSetProposal(string SourcePath, bool IsArchive, string Name,
 IReadOnlyList<Guid> MediaIds, string Snapshot, IReadOnlyList<Guid> ExistingSetIds)
{
    public IReadOnlyList<Guid> OverlappingSetIds { get; init; } = [];
}
public sealed record ImageSetProposalChoice(ImageSetProposal Proposal, string Name);

public sealed partial class CatalogRepository
{
    public IReadOnlyList<ImageSetProposal> GetImageSetProposals(CancellationToken token = default)
    {
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
            var proposals = ReadImageSetProposals(transaction, token); transaction.Commit(); return proposals;
        }
    }
    public IReadOnlyList<ImageSet> ApplyImageSetProposals(IReadOnlyList<ImageSetProposalChoice> choices, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(choices);
        lock (gate)
        {
            ThrowIfDisposed(); token.ThrowIfCancellationRequested();
            if (choices.Count == 0) return [];
            using var transaction = connection.BeginTransaction();
            var current = ReadImageSetProposals(transaction, token).ToDictionary(p => (p.IsArchive, PathKey(p.SourcePath)));
            var seen = new HashSet<(bool, string)>(); var sets = new List<ImageSet>();
            foreach (var choice in choices)
            {
                token.ThrowIfCancellationRequested(); var proposal = choice.Proposal;
                if (proposal == null || !seen.Add((proposal.IsArchive, PathKey(proposal.SourcePath))) ||
                   !current.TryGetValue((proposal.IsArchive, PathKey(proposal.SourcePath)), out var actual) ||
                   actual.Snapshot != proposal.Snapshot || !actual.MediaIds.SequenceEqual(proposal.MediaIds) ||
                   !actual.ExistingSetIds.Order().SequenceEqual(proposal.ExistingSetIds.Order()))
                    throw new InvalidOperationException(T("The image-set proposals changed. Refresh the preview."));
                if (string.IsNullOrWhiteSpace(choice.Name) || choice.Name.Length > 256) throw new ArgumentException(T("Invalid image-set name."));
                var set = new ImageSet { Name = choice.Name.Trim(), MediaIds = actual.MediaIds.ToList(), CoverId = actual.MediaIds.FirstOrDefault() }; ValidateImageSet(set); sets.Add(set);
            }
            foreach (var set in sets) { token.ThrowIfCancellationRequested(); SaveImageSet(set, transaction); }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return sets;
        }
    }
    private IReadOnlyList<ImageSetProposal> ReadImageSetProposals(SqliteTransaction transaction, CancellationToken token)
     => database.Run<IReadOnlyList<ImageSetProposal>>(token, () =>
    {
        var sources = new List<(MediaItem Media, string Source, bool Archive, string Entry)>();
        using (var command = Command("SELECT m.document FROM media_query q JOIN media m ON m.id=q.id WHERE q.kind='Image'", transaction))

        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); var media = JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), Json)!;
            var archive = media.Extra.TryGetValue("archivePath", out var path) && !string.IsNullOrWhiteSpace(path);
            var source = archive ? path! : Path.GetDirectoryName(Path.GetFullPath(media.Path))!;
            var entry = archive ? media.Extra.GetValueOrDefault("entryName") ?? media.Path : Path.GetFileName(media.Path);
            sources.Add((media, source, archive, entry));
        }
        var sets = ReadDocuments<ImageSet>("image_sets", transaction); var result = new List<ImageSetProposal>();
        foreach (var group in sources.GroupBy(s => (s.Archive, Key: PathKey(s.Source))).OrderBy(g => g.Key.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var ordered = group.OrderBy(s => s.Entry, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Entry, StringComparer.Ordinal).ThenBy(s => s.Media.Id).ToArray();
            var ids = ordered.Select(s => s.Media.Id).ToArray(); var first = ordered[0];
            // Only source identity affects staleness: unrelated rating/tag edits do not invalidate a proposal.
            var snapshot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ordered.Select(s => new { s.Media.Id, s.Media.Path, s.Entry, ArchiveHash = s.Media.Extra.GetValueOrDefault("sha256") }), Json))));
            var existing = sets.Where(s => s.MediaIds.Count == ids.Length && s.MediaIds.ToHashSet().SetEquals(ids)).Select(s => s.Id).Order().ToArray();
            var memberIds = ids.ToHashSet(); var overlaps = sets.Where(s => !existing.Contains(s.Id) && s.MediaIds.Any(memberIds.Contains)).Select(s => s.Id).Order().ToArray();
            var name = first.Archive ? Path.GetFileNameWithoutExtension(first.Source) : Path.GetFileName(Path.TrimEndingDirectorySeparator(first.Source));
            result.Add(new(first.Source, first.Archive, string.IsNullOrWhiteSpace(name) ? first.Source : name, ids, snapshot, existing) { OverlappingSetIds = overlaps });
        }
        return result;
    });
}
