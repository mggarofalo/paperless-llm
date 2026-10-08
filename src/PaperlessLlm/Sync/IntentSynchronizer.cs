using System.Text.Json;
using PaperlessLlm.Intent;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Sync;

/// <summary>Durable intent, minimal PATCH, read-back verification; never treats a response as proof of commit.</summary>
public sealed partial class IntentSynchronizer(IPaperlessClient reader, IPaperlessWriter writer, string journalDirectory,
    string reviewTag = "needs review", bool dryRun = false) : IIntentSynchronizer
{
    private sealed record Operation(string JobId, PaperlessDocument Before, JsonElement Intent, int PageCount, JsonElement Patch, string Status,
        DateTimeOffset CreatedAt, PaperlessDocument? After = null, string? Note = null, bool NoteAttempted = false)
    { }

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
            operation = await ReadOperationAsync(path, jobId, source, intent, pageCount, ct);
            if (operation.Status == "verified")
                return new("applied", await reader.GetDocumentAsync(source.Id, ct));
        }
        else
        {
            var prepared = await PrepareOperationAsync(path, jobId, source, intent, pageCount, ct);
            if (prepared.Result is not null) return prepared.Result;
            operation = prepared.Operation!;
        }

        return await ResumeAsync(path, operation, ct);
    }

    private async Task<SyncResult> ResumeAsync(string path, Operation operation, CancellationToken ct)
    {
        if (operation.Status == "metadata_verified") return await FinishNoteAsync(path, operation, ct);
        var latest = await reader.GetDocumentAsync(operation.Before.Id, ct);
        if (!Matches(latest, operation.Patch))
        {
            // A pending intent may already have committed despite an HTTP timeout. Only replay
            // against the exact before-state; never overwrite a subsequent human correction.
            if (!SameSource(operation.Before, latest)) throw new SyncConflictException("sync_conflict");
            if (dryRun) return new("dry_run", latest);
            latest = await CommitAsync(operation, ct);
        }
        if (operation.Note is not null)
        {
            VerifyNoteSource(operation.Before, latest);
            operation = operation with { Status = "metadata_verified", After = latest };
            if (dryRun) return new("dry_run", latest);
            await SaveAsync(path, operation, ct);
            return await FinishNoteAsync(path, operation, ct);
        }
        await SaveAsync(path, operation with { Status = "verified", After = latest }, ct);
        return new("applied", latest);
    }

    private static async Task<Operation> ReadOperationAsync(string path, string jobId, PaperlessDocument source, JsonElement intent, int pageCount, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidOperationException("sync_journal_too_large");
        var operation = JsonSerializer.Deserialize<Operation>(await File.ReadAllTextAsync(path, ct))
            ?? throw new InvalidOperationException("invalid_sync_journal");
        if (operation.JobId != jobId || operation.Before.Id != source.Id || !SameSource(operation.Before, source))
            throw new InvalidOperationException("sync_job_identity_mismatch");
        if (!JsonElement.DeepEquals(operation.Intent, intent) || operation.PageCount != pageCount)
            throw new InvalidOperationException("sync_intent_identity_mismatch");
        ValidateNoteJournal(operation);
        return operation;
    }

    private async Task<(Operation? Operation, SyncResult? Result)> PrepareOperationAsync(string path, string jobId, PaperlessDocument source, JsonElement intent, int pageCount, CancellationToken ct)
    {
        var taxonomy = await reader.GetTaxonomyAsync(ct);
        var validated = IntentValidator.Validate(intent.GetRawText(), source, taxonomy, pageCount);
        var current = await reader.GetDocumentAsync(source.Id, ct);
        if (!SameSource(source, current)) throw new SyncConflictException("stale_source");
        var patch = BuildPatch(validated, current);
        var note = DocumentNotes.Proposed(validated, current);
        if (patch.Count == 0 && note is null) return (null, new("no_change", current));
        var matches = taxonomy.Tags.Where(t => t.Name.Equals(reviewTag, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ProposalValidationException("review_tag_missing_or_ambiguous");
        var tags = patch.TryGetValue("tags", out var proposedTags) ? (int[])proposedTags! : current.Tags.ToArray();
        patch["tags"] = tags.Append(matches[0].Id).Distinct().Order().ToArray();
        if (dryRun) return (null, new("dry_run", current));
        var operation = new Operation(jobId, current, validated, pageCount, JsonSerializer.SerializeToElement(patch), "pending", DateTimeOffset.UtcNow, Note: note);
        await SaveAsync(path, operation, ct);
        return (operation, null);
    }

    private async Task<PaperlessDocument> CommitAsync(Operation operation, CancellationToken ct)
    {
        // IDs and tag semantics may have changed while a pending write was paused.
        var taxonomy = await reader.GetTaxonomyAsync(ct);
        try { IntentValidator.Validate(operation.Intent.GetRawText(), operation.Before, taxonomy, operation.PageCount); }
        catch (ProposalValidationException) { throw new SyncConflictException("sync_taxonomy_changed"); }
        var marker = taxonomy.Tags.Where(t => t.Name.Equals(reviewTag, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (marker.Length != 1 || !operation.Patch.GetProperty("tags").EnumerateArray().Any(t => t.GetInt32() == marker[0].Id))
            throw new SyncConflictException("sync_review_tag_changed");
        var latest = await reader.GetDocumentAsync(operation.Before.Id, ct);
        if (!SameSource(operation.Before, latest)) throw new SyncConflictException("sync_conflict");
        var patch = operation.Patch.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        await writer.PatchAsync(operation.Before.Id, patch, ct);
        latest = await reader.GetDocumentAsync(operation.Before.Id, ct);
        if (!Matches(latest, operation.Patch)) throw new SyncConflictException("sync_readback_mismatch"); return latest;
    }

    internal static Dictionary<string, object?> BuildPatch(JsonElement intent, PaperlessDocument current)
    {
        Dictionary<string, object?> patch = [];
        AddText(patch, intent, "title", "title", current.Title);
        AddText(patch, intent, "date", "created", current.Created);
        AddIdentity(patch, intent, "correspondent", current.CorrespondentId);
        AddIdentity(patch, intent, "document_type", current.DocumentTypeId);
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

    private static void AddText(Dictionary<string, object?> patch, JsonElement intent, string field, string target, string? before)
    {
        var entry = intent.GetProperty(field);
        if (entry.GetProperty("action").GetString() != "set") return;
        var value = entry.GetProperty("value").GetString();
        var comparison = field == "date" && before is { Length: >= 10 } ? before[..10] : before;
        if (value != comparison) patch[target] = value;
    }
    private static void AddIdentity(Dictionary<string, object?> patch, JsonElement intent, string field, int? before)
    {
        var entry = intent.GetProperty(field);
        if (entry.GetProperty("action").GetString() != "set") return;
        var value = entry.GetProperty("value").GetInt32();
        if (value != before) patch[field] = value;
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
