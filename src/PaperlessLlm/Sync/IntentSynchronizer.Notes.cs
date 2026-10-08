using PaperlessLlm.Intent;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Sync;

public sealed partial class IntentSynchronizer
{
    private static void ValidateNoteJournal(Operation operation)
    {
        if (operation.Note != DocumentNotes.Proposed(operation.Intent, operation.Before))
            throw new InvalidOperationException("sync_note_identity_mismatch");
        if (operation.Status == "metadata_verified" && (operation.After is null || operation.Note is null))
            throw new InvalidOperationException("invalid_note_journal");
        if (operation.NoteAttempted && (operation.Note is null || operation.Status == "pending"))
            throw new InvalidOperationException("invalid_note_journal");
    }

    private static bool SameSource(PaperlessDocument before, PaperlessDocument after) =>
        before.RevisionHash == after.RevisionHash && DocumentNotes.SameNotes(before, after);

    private static void VerifyNoteSource(PaperlessDocument before, PaperlessDocument latest)
    {
        if (latest.Content != before.Content || !DocumentNotes.SameNotes(before, latest))
            throw new SyncConflictException("sync_note_source_changed");
    }

    private async Task<SyncResult> FinishNoteAsync(string path, Operation operation, CancellationToken ct)
    {
        var latest = await reader.GetDocumentAsync(operation.Before.Id, ct);
        if (latest.Notes is null) throw new SyncConflictException("sync_note_context_missing");
        if (!latest.Notes.Any(n => n.Note == operation.Note))
        {
            if (dryRun) return new("dry_run", latest);
            // Paperless POST has no idempotency key. A persisted attempt may still be
            // in flight even if GET cannot see it; never blindly send it a second time.
            if (operation.NoteAttempted) throw new SyncConflictException("sync_note_write_unconfirmed");
            if (!SameSource(operation.After!, latest)) throw new SyncConflictException("sync_note_source_changed");
            VerifyNoteSource(operation.Before, latest);
            operation = operation with { NoteAttempted = true };
            await SaveAsync(path, operation, ct);
            await writer.AddNoteAsync(latest.Id, operation.Note!, ct);
            latest = await reader.GetDocumentAsync(latest.Id, ct);
            if (latest.Notes?.Any(n => n.Note == operation.Note) != true)
                throw new SyncConflictException("sync_note_readback_mismatch");
        }
        await SaveAsync(path, operation with { Status = "verified", After = latest }, ct);
        return new("applied", latest);
    }
}
