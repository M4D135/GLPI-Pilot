using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public static class TeamWorkspaceCalendarProtocol
{
    private static bool Id(string? id) => id is not null && Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    public static void Validate(TeamWorkspaceBatchMutation batch)
    {
        if (batch is null || !Id(batch.MutationId) || batch.Mutations is null || batch.Mutations.Count is < 1 or > 200
            || batch.Mutations.Any(value => value is null || !Id(value.MutationId) || value.BaseRevision < 0 || value.Item is null || !CalendarWorkspaceDocuments.IsKind(value.Item.Kind))
            || batch.Mutations.Select(value => value.Item.Id).Distinct(StringComparer.Ordinal).Count() != batch.Mutations.Count
            || batch.Mutations.Select(value => value.MutationId).Distinct(StringComparer.Ordinal).Count() != batch.Mutations.Count
            || batch.ResolvesGroups is null || batch.ResolvesGroups.Count > 200 || batch.ResolvesGroups.Any(value => !Id(value)) || batch.ResolvesGroups.Distinct(StringComparer.Ordinal).Count() != batch.ResolvesGroups.Count)
            throw new InvalidDataException("Lot partagé du calendrier invalide.");
        long bytes = 0;
        foreach (var value in batch.Mutations) { bytes += Encoding.UTF8.GetByteCount(TeamWorkspaceCodec.Write(value.Item)); if (bytes > TeamWorkspaceCodec.MaximumBatchBytes) throw new InvalidDataException("Lot du calendrier supérieur à 32 Mio."); }
    }
    public static string RequestHash(TeamWorkspaceBatchMutation batch)
    {
        Validate(batch); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", batch.Mutations.OrderBy(value => value.Item.Id, StringComparer.Ordinal)
            .Select(value => value.MutationId + ":" + value.BaseRevision + ":" + TeamWorkspaceCodec.ContentHash(value.Item))) + "\nresolve:" + string.Join(",", batch.ResolvesGroups.Order(StringComparer.Ordinal))))).ToLowerInvariant();
    }
    public static void ValidateReceipt(TeamWorkspaceBatchMutation batch, TeamWorkspaceBatchReceipt receipt)
    {
        Validate(batch);
        if (receipt is null || receipt.Outcome is not ("accepted" or "unchanged" or "conflict") || receipt.Receipts is null || receipt.Receipts.Count != batch.Mutations.Count
            || receipt.Receipts.Any(value => value is null || value.Current is null || value.Current.CentralRevision <= 0 || value.Outcome != receipt.Outcome)
            || !receipt.Receipts.Select(value => value.Current.Id).ToHashSet(StringComparer.Ordinal).SetEquals(batch.Mutations.Select(value => value.Item.Id))
            || receipt.ResolvedGroups is null || !(receipt.Outcome == "conflict" ? receipt.ResolvedGroups.Count == 0 : receipt.ResolvedGroups.ToHashSet(StringComparer.Ordinal).SetEquals(batch.ResolvesGroups)))
            throw new InvalidDataException("Accusé de lot du calendrier incomplet ou incohérent.");
        foreach (var member in receipt.Receipts) TeamWorkspaceCodec.Validate(member.Current);
    }
    public static void ValidateChanges(IReadOnlyList<TeamWorkspaceChange> changes)
    {
        if (changes.Count is < 1 or > 200 || !Id(changes[0].BatchId) || changes.Any(change => change.BatchId != changes[0].BatchId || change.BatchSize != changes.Count)
            || changes.Select(change => change.Item.Id).Distinct(StringComparer.Ordinal).Count() != changes.Count)
            throw new InvalidDataException("Le lot reçu du calendrier est incomplet ; le curseur reste inchangé.");
        long last = 0, bytes = 0;
        foreach (var change in changes)
        {
            TeamWorkspaceCodec.Validate(change.Item);
            if (!CalendarWorkspaceDocuments.IsKind(change.Item.Kind) || change.Sequence <= last || change.Sequence != change.Item.CentralRevision || change.Incoming is { } incoming && incoming.Id != change.Item.Id
                || change.ResolvedGroups is null || change.ResolvedGroups.Count > 200 || change.ResolvedGroups.Any(value => !Id(value)) || !change.ResolvedGroups.SequenceEqual(changes[0].ResolvedGroups))
                throw new InvalidDataException("Ordre ou contenu du lot du calendrier incohérent.");
            bytes += Encoding.UTF8.GetByteCount(TeamWorkspaceCodec.Write(change.Item));
            if (change.Incoming is { } candidate) bytes += Encoding.UTF8.GetByteCount(TeamWorkspaceCodec.Write(candidate));
            if (bytes > 2L * TeamWorkspaceCodec.MaximumBatchBytes) throw new InvalidDataException("Lot reçu du calendrier supérieur à 64 Mio.");
            last = change.Sequence;
        }
    }
}
