using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Intent;

public sealed class OrganizationPromptException() : Exception("organization_prompt_unreadable_or_invalid");

/// <summary>Snapshot the operator's policy once per inference; never interpolate document text into it.</summary>
public sealed class OrganizationPrompt(string path, string model) : IIntentContextBuilder
{
    public const int MaxBytes = 64 * 1024;
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "prompts", "organization.txt");

    public async Task<string> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[MaxBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBytes) throw new InvalidDataException();
            var text = new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new InvalidDataException();
            return text;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        { throw new OrganizationPromptException(); }
    }

    public async Task<IntentContext> BuildAsync(PaperlessDocument source, PaperlessTaxonomy taxonomy, int pageCount, CancellationToken ct)
    {
        var instructions = await ReadAsync(ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)));
        var schema = JsonDocument.Parse(NamedIntentContract.Schema()).RootElement.Clone();
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { model, prompt = hash, schema, contract = "names-1", reasoning = "low",
                runner = "pi-0.99.2", maxOutputTokens = 16000, context = "existing-ocr-1",
                IntentPrompt.MaxOcrCharacters, IntentPrompt.MaxEntities, validator = "intent-validator-1" }))));
        return new(instructions, NamedIntentContract.Payload(IntentPrompt.Build(source, taxonomy, 0), taxonomy),
            schema, fingerprint, true, hash);
    }
}
