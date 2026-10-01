using System.Text.Json;
using PaperlessLlm.Intent;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Sync;

/// <summary>Durable intent, minimal PATCH, read-back verification; never treats a response as proof of commit.</summary>
public sealed class IntentSynchronizer(IPaperlessClient reader, IPaperlessWriter writer, string journalDirectory,
    string reviewTag = "needs review", bool dryRun = false) : IIntentSynchronizer
{
    private sealed record Operation(string JobId, PaperlessDocument Before, JsonElement Intent, int PageCount, JsonElement Patch, string Status,
        DateTimeOffset CreatedAt, PaperlessDocument? After = null);

    public async Task<SyncResult> ApplyAsync(string jobId, PaperlessDocument source, JsonElement intent, int pageCount, CancellationToken ct)
    {
        if (jobId.Length is < 1 or > 160 || jobId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid job identity.");
        AuditWriter.PrivateDirectory(journalDirectory);
        var path = Path.Combine(journalDirectory, jobId + ".json");
        using var operationLock = new FileStream(Path.Combine(journalDirectory, jobId + ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Operation operation;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidOperationException("sync_journal_too_large");
            operation = JsonSerializer.Deserialize<Operation>(await File.ReadAllTextAsync(path, ct))
                ?? throw new InvalidOperationException("invalid_sync_journal");
            if (operation.JobId != jobId || operation.Before.Id != source.Id || operation.Before.RevisionHash != source.RevisionHash)
                throw new InvalidOperationException("sync_job_identity_mismatch");
            if (!JsonElement.DeepEquals(operation.Intent, intent) || operation.PageCount != pageCount)
                throw new InvalidOperationException("sync_intent_identity_mismatch");
            if (operation.Status == "verified")
                return new("applied", await reader.GetDocumentAsync(source.Id, ct));
        }
        else
        {
            var taxonomy = await reader.GetTaxonomyAsync(ct);
            var validated = IntentValidator.Validate(intent.GetRawText(), source, taxonomy, pageCount);
            var current = await reader.GetDocumentAsync(source.Id, ct);
            if (current.RevisionHash != source.RevisionHash) throw new SyncConflictException("stale_source");
            var patch = BuildPatch(validated, current);
            if (patch.Count == 0) return new("no_change", current);
            var matches = taxonomy.Tags.Where(t => t.Name.Equals(reviewTag, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new ProposalValidationException("review_tag_missing_or_ambiguous");
            var tags = patch.TryGetValue("tags", out var proposedTags) ? (int[])proposedTags! : current.Tags.ToArray();
            patch["tags"] = tags.Append(matches[0].Id).Distinct().Order().ToArray();
            if (dryRun) return new("dry_run", current);
            operation = new(jobId, current, validated, pageCount, JsonSerializer.SerializeToElement(patch), "pending", DateTimeOffset.UtcNow);
            await SaveAsync(path, operation, ct);
        }

        var latest = await reader.GetDocumentAsync(source.Id, ct);
        if (!Matches(latest, operation.Patch))
        {
            // A pending intent may already have committed despite an HTTP timeout. Only replay
            // against the exact before-state; never overwrite a subsequent human correction.
            if (latest.RevisionHash != operation.Before.RevisionHash) throw new SyncConflictException("sync_conflict");
            if (dryRun) return new("dry_run", latest);
            // IDs and tag semantics may have changed while a pending write was paused.
            var taxonomy = await reader.GetTaxonomyAsync(ct);
            try { IntentValidator.Validate(operation.Intent.GetRawText(), operation.Before, taxonomy, operation.PageCount); }
            catch (ProposalValidationException) { throw new SyncConflictException("sync_taxonomy_changed"); }
            var marker = taxonomy.Tags.Where(t => t.Name.Equals(reviewTag, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (marker.Length != 1 || !operation.Patch.GetProperty("tags").EnumerateArray().Any(t => t.GetInt32() == marker[0].Id))
                throw new SyncConflictException("sync_review_tag_changed");
            latest = await reader.GetDocumentAsync(source.Id, ct);
            if (latest.RevisionHash != operation.Before.RevisionHash) throw new SyncConflictException("sync_conflict");
            var patch = operation.Patch.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            await writer.PatchAsync(source.Id, patch, ct);
            latest = await reader.GetDocumentAsync(source.Id, ct);
            if (!Matches(latest, operation.Patch)) throw new SyncConflictException("sync_readback_mismatch");
        }
        await SaveAsync(path, operation with { Status = "verified", After = latest }, ct);
        return new("applied", latest);
    }

    internal static Dictionary<string, object?> BuildPatch(JsonElement intent, PaperlessDocument current)
    {
        Dictionary<string, object?> patch = [];
        void Text(string field, string target, string? before)
        {
            var entry = intent.GetProperty(field);
            if (entry.GetProperty("action").GetString() != "set") return;
            var value = entry.GetProperty("value").GetString();
            var comparison = field == "date" && before is { Length: >= 10 } ? before[..10] : before;
            if (value != comparison) patch[target] = value;
        }
        void Identity(string field, int? before)
        {
            var entry = intent.GetProperty(field);
            if (entry.GetProperty("action").GetString() != "set") return;
            var value = entry.GetProperty("value").GetInt32();
            if (value != before) patch[field] = value;
        }
        Text("title", "title", current.Title);
        Text("date", "created", current.Created);
        Identity("correspondent", current.CorrespondentId);
        Identity("document_type", current.DocumentTypeId);
        var tags = current.Tags.Concat(intent.GetProperty("add_tags").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()))
            .Distinct().Order().ToArray();
        if (!tags.SequenceEqual(current.Tags.Order())) patch["tags"] = tags;
        var ocr = intent.GetProperty("ocr");
        if (ocr.GetProperty("action").GetString() == "set")
        {
            var content = string.Join("\n\n", ocr.GetProperty("pages").EnumerateArray().OrderBy(p => p.GetProperty("page").GetInt32())
                .Select(p => p.GetProperty("text").GetString()));
            if (content != current.Content) patch["content"] = content;
        }
        return patch;
    }

    private static bool Matches(PaperlessDocument current, JsonElement patch)
    {
        foreach (var field in patch.EnumerateObject())
        {
            bool equal = field.Name switch
            {
                "title" => current.Title == field.Value.GetString(),
                "content" => current.Content == field.Value.GetString(),
                "created" => current.Created is { Length: >= 10 } && current.Created[..10] == field.Value.GetString(),
                "correspondent" => current.CorrespondentId == field.Value.GetInt32(),
                "document_type" => current.DocumentTypeId == field.Value.GetInt32(),
                "tags" => current.Tags.Order().SequenceEqual(field.Value.EnumerateArray().Select(t => t.GetInt32()).Order()),
                _ => false
            };
            if (!equal) return false;
        }
        return true;
    }

    private static async Task SaveAsync(string path, Operation operation, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await AuditWriter.WritePrivateAsync(temporary, JsonSerializer.Serialize(operation), ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
