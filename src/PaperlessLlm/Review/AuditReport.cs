using System.Net;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Review;

/// <summary>Self-contained HTML evidence. Untrusted source/model text is always encoded.</summary>
internal static class AuditReport
{
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static async Task<string> BuildAsync(AuditRecord record, IReadOnlyList<AuditAttachment> attachments,
        string stagingDirectory, string auditJson, CancellationToken ct)
    {
        var proposal = record.Proposal;
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'">
            <title>Private Paperless review</title><style>
            :root{color-scheme:light;--ink:#152b3b;--muted:#536777;--line:#d8e1e7;--paper:#fff;--ground:#eff3f6;--add:#e1f5eb;--remove:#fff0ec}
            *{box-sizing:border-box}body{margin:0;background:var(--ground);color:var(--ink);font:15px/1.6 system-ui,-apple-system,Segoe UI,sans-serif}
            main{max-width:1500px;margin:auto;padding:36px 28px 64px}header{margin-bottom:28px}h1{font-size:clamp(24px,3vw,38px);line-height:1.2;margin:8px 0 12px;overflow-wrap:anywhere}
            h2{font-size:20px;margin:0 0 14px}h3{font-size:15px;margin:0}.eyebrow{text-transform:uppercase;letter-spacing:.12em;font-size:11px;font-weight:700;color:var(--muted)}
            .muted,.caption{color:var(--muted)}.caption{font-size:13px}.badge{display:inline-block;padding:3px 10px;background:#e1eaf1;border-radius:20px;font-size:12px;font-weight:650}
            .notice{border-left:4px solid #607e91;padding:12px 16px;background:#e7eef3;margin:18px 0}.warning{background:#fff0d8;border-left-color:#a86a13}
            .card{background:var(--paper);border:1px solid var(--line);border-radius:12px;padding:22px;margin:22px 0;min-width:0}
            a{color:#125a82;text-underline-offset:3px}nav{display:flex;flex-wrap:wrap;gap:18px;margin-top:18px}table{width:100%;border-collapse:collapse;table-layout:fixed}
            th,td{padding:12px 14px;text-align:left;border-bottom:1px solid var(--line);vertical-align:top;overflow-wrap:anywhere}th{font-size:12px;color:var(--muted);background:#f5f8fa}
            th:first-child{width:18%}tr.changed td:last-child{background:var(--add)}.keep{display:block;font-size:12px;color:var(--muted)}
            .comparison{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:18px}.comparison>section{min-width:0}.comparison h3{padding:10px 12px;background:#f3f7fa;border:1px solid var(--line);border-bottom:0;border-radius:6px 6px 0 0}
            pre{font:13px/1.7 ui-monospace,SFMono-Regular,Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere;tab-size:4;margin:0;padding:14px;background:#fbfcfd;border:1px solid var(--line);border-radius:0 0 6px 6px;max-height:780px;overflow:auto}
            .line{display:block;padding:0 5px;min-height:1.7em}.removed{background:var(--remove);border-left:3px solid #ca6c55}.added{background:var(--add);border-left:3px solid #3d9370}.gap{background:#f0f3f5;color:transparent}
            .legend{display:flex;gap:18px;font-size:13px;margin:0 0 14px}.legend span{padding:2px 8px}.evidence{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:20px}.evidence ul{margin:8px 0;padding-left:22px}
            .pages{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,420px),1fr));gap:20px}.pages figure{margin:0;border:1px solid var(--line);border-radius:8px;background:#edf1f4;padding:12px}.pages img{display:block;width:100%;height:auto;background:white}.pages figcaption{font-size:13px;padding:10px 0 0;overflow-wrap:anywhere}
            details summary{cursor:pointer;font-weight:650;padding:10px 0}details pre{max-height:500px;border-radius:6px}footer{font-size:12px;color:var(--muted);overflow-wrap:anywhere}
            @media(max-width:760px){main{padding:22px 14px}.card{padding:16px}.comparison,.evidence{grid-template-columns:1fr}th,td{padding:8px}th:first-child{width:23%}}
            @media print{body{background:white}main{max-width:none;padding:0}.card{break-inside:avoid}pre{max-height:none;overflow:visible}nav,details{display:none}.pages{grid-template-columns:1fr 1fr}}
            </style></head><body><main><header><div class="eyebrow">Private accuracy review · read only</div>
            """);
        html.Append("<h1>").Append(E(record.Source.Title)).Append("</h1><span class=\"badge\">").Append(E(Outcome(record.Outcome)))
            .Append("</span><p class=\"caption\">Document ").Append(record.Source.Id).Append(" · Model ").Append(E(record.Model))
            .Append(" · ").Append(E(record.RecordedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'"))).Append("</p>");
        html.Append("<div class=\"notice\">Nothing was changed in Paperless. Compare every suggestion with the original pages; model evidence and explanations may also be wrong.</div>");
        if (record.ErrorCode is not null) html.Append("<div class=\"notice warning\">This attempt needs attention: ").Append(E(record.ErrorCode)).Append(". No result was applied.</div>");
        if (proposal is null) html.Append("<p>No validated proposal was saved for this attempt. Existing values and any retained source pages are shown below.</p>");
        html.Append("<nav aria-label=\"Report sections\"><a href=\"#metadata\">Metadata comparison</a><a href=\"#ocr\">OCR comparison</a><a href=\"#pages\">Source pages</a><a href=\"#audit\">Technical audit</a>");
        if (SourceLink(record.SourceUrl, record.Source.Id) is { } link)
            html.Append("<a rel=\"noreferrer noopener\" href=\"").Append(E(link)).Append("\">Open in Paperless ↗</a>");
        html.Append("</nav></header><section class=\"card\" id=\"metadata\"><h2>Metadata comparison</h2><p class=\"caption\">Green cells show proposed changes. Unchanged and protected tags remain on the document.</p><table><thead><tr><th>Field</th><th>Existing value</th><th>Proposed value</th></tr></thead><tbody>");
        Row(html, "Title", record.Source.Title, Text(proposal, "title"));
        Row(html, "Document date", DateOnly(record.Source.Created), Text(proposal, "date"));
        Row(html, "Correspondent", Name(record.Source.CorrespondentId, record.Taxonomy?.Correspondents),
            Id(proposal, "correspondent") is { } correspondent ? Name(correspondent, record.Taxonomy?.Correspondents) : null);
        Row(html, "Document type", Name(record.Source.DocumentTypeId, record.Taxonomy?.DocumentTypes),
            Id(proposal, "document_type") is { } documentType ? Name(documentType, record.Taxonomy?.DocumentTypes) : null);
        var existingTags = record.Source.Tags.Select(id => Name(id, record.Taxonomy?.Tags)).ToArray();
        var newTags = Array(proposal, "add_tags").Where(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _))
            .Select(v => v.GetInt32()).Except(record.Source.Tags).Select(id => Name(id, record.Taxonomy?.Tags)).ToArray();
        Row(html, "Tags", existingTags.Length == 0 ? "None" : string.Join(", ", existingTags),
            newTags.Length == 0 ? null : string.Join(", ", existingTags.Concat(newTags)),
            newTags.Length == 0 ? null : "Add only: " + string.Join(", ", newTags));
        html.Append("</tbody></table></section>");
        html.Append("<section class=\"card evidence\"><section><h2>Model's supporting evidence</h2>");
        TextList(html, Array(proposal, "evidence"), "No supporting evidence supplied.");
        html.Append("</section><section><h2>Uncertainty to check</h2>");
        TextList(html, Array(proposal, "uncertainty"), "No uncertainty listed. This is not a guarantee of accuracy.");
        html.Append("</section></section><section class=\"card\" id=\"ocr\"><h2>OCR comparison</h2>");
        var proposedOcr = Text(proposal, "ocr_text");
        if (proposedOcr is null)
            html.Append("<p class=\"caption\">No OCR replacement proposed. Existing OCR is preserved; this does not certify its accuracy.</p>");
        else if (proposedOcr == record.Source.Content)
            html.Append("<p class=\"caption\">The proposed OCR is identical to the existing text.</p>");
        else html.Append("<div class=\"legend\"><span class=\"removed\">− Existing lines removed or replaced</span><span class=\"added\">+ Proposed lines added or replaced</span></div>");
        var diff = Diff(record.Source.Content, proposedOcr);
        html.Append("<div class=\"comparison\"><section><h3>Existing OCR</h3><pre aria-label=\"Existing OCR\">")
            .Append(diff.Before).Append("</pre></section><section><h3>Proposed OCR</h3><pre aria-label=\"Proposed OCR\">")
            .Append(diff.After).Append("</pre></section></div></section>");
        html.Append("<section class=\"card\" id=\"pages\"><h2>Source pages from this attempt</h2><p class=\"caption\">Retained page images for this review attempt. Enlarge with your browser's zoom to check names, dates, amounts, and minus signs.</p><div class=\"pages\">");
        var imageAttachments = attachments.Where(a => Path.GetExtension(a.FileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp").ToArray();
        long embeddedBytes = 0;
        foreach (var attachment in imageAttachments)
        {
            var path = Path.Combine(stagingDirectory, attachment.FileName);
            var length = new FileInfo(path).Length;
            embeddedBytes += length;
            if (embeddedBytes > 32 * 1024 * 1024) throw new InvalidOperationException("audit_preview_size_limit");
            try
            {
                var mediaType = ImageRenderer.ValidateImage(path);
                var bytes = await File.ReadAllBytesAsync(path, ct);
                html.Append("<figure><img alt=\"Retained source page ").Append(E(attachment.FileName)).Append("\" src=\"data:")
                    .Append(mediaType).Append(";base64,").Append(Convert.ToBase64String(bytes)).Append("\"><figcaption>")
                    .Append(E(attachment.FileName)).Append(" · ").Append(length.ToString("N0")).Append(" bytes</figcaption></figure>");
            }
            catch (PaperlessException)
            {
                html.Append("<p class=\"notice warning\">Preview unavailable for ").Append(E(attachment.FileName))
                    .Append(". The retained file is not a verified PNG or JPEG.</p>");
            }
        }
        if (imageAttachments.Length == 0) html.Append("<p class=\"muted\">No page images were retained for this attempt. Do not evaluate a transcription without the original scan.</p>");
        html.Append("</div></section><section class=\"card\" id=\"audit\"><h2>Technical audit</h2><p class=\"caption\">Contains private source text, the exact prompt and response, taxonomy, and file hashes. Share only with someone authorized to see the documents.</p><details><summary>Show the complete audit record</summary><pre>")
            .Append(E(auditJson)).Append("</pre></details></section><footer>Source revision: ").Append(E(record.Source.RevisionHash))
            .Append("<br>This self-contained report loads no scripts, fonts, or images from the network. Keep it with its private audit files.</footer></main></body></html>");
        return html.ToString();
    }

    private static string Outcome(string value) => value switch
    { "proposal_ready" => "Ready for human review", "source_changed" => "Source changed — review again", "review_failed" => "Review did not complete", _ => value };
    private static string? SourceLink(string? value, int id) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? new Uri(uri.AbsoluteUri.TrimEnd('/') + $"/documents/{id}/details").AbsoluteUri : null;
    private static JsonElement? Field(JsonElement? proposal, string key) =>
        proposal is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(key, out var field) ? field : null;
    private static string? Text(JsonElement? proposal, string key) => Field(proposal, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static int? Id(JsonElement? proposal, string key) => Field(proposal, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var id) ? id : null;
    private static JsonElement[] Array(JsonElement? proposal, string key) => Field(proposal, key) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().ToArray() : [];
    private static string Name(int? id, IReadOnlyList<NamedEntity>? entities) => id is null ? "None" : entities?.FirstOrDefault(e => e.Id == id)?.Name ?? $"ID {id}";
    private static string DateOnly(string? value) => value is { Length: >= 10 } ? value[..10] : value ?? "None";

    private static void Row(StringBuilder html, string field, string before, string? proposed, string? explanation = null)
    {
        var changed = proposed is not null && proposed != before;
        html.Append("<tr").Append(changed ? " class=\"changed\"" : "").Append("><td><strong>").Append(E(field)).Append("</strong></td><td>")
            .Append(E(before)).Append("</td><td>").Append(E(proposed ?? before));
        html.Append("<span class=\"keep\">").Append(E(explanation ?? (changed ? "Change proposed" : "No change proposed"))).Append("</span></td></tr>");
    }

    private static void TextList(StringBuilder html, JsonElement[] values, string empty)
    {
        var strings = values.Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).ToArray();
        if (strings.Length == 0) { html.Append("<p class=\"muted\">").Append(E(empty)).Append("</p>"); return; }
        html.Append("<ul>");
        foreach (var text in strings) html.Append("<li>").Append(E(text)).Append("</li>");
        html.Append("</ul>");
    }

    private static (string Before, string After) Diff(string before, string? after)
    {
        if (after is null) return (E(before), "No replacement proposed.");
        if (after == before) return (E(before), E(after));
        var left = before.ReplaceLineEndings("\n").Split('\n');
        var right = after.ReplaceLineEndings("\n").Split('\n');
        var a = new StringBuilder(); var b = new StringBuilder();
        static void Line(StringBuilder target, string text, string css) => target.Append("<span class=\"line ").Append(css).Append("\">").Append(E(text)).Append("</span>");
        // Bound comparison work for large OCR; always retain the complete text.
        if ((long)left.Length * right.Length > 250000)
        {
            foreach (var line in left) Line(a, line, "removed");
            foreach (var line in right) Line(b, line, "added");
            return (a.ToString(), b.ToString());
        }
        var lengths = new int[left.Length + 1, right.Length + 1];
        for (int i = left.Length - 1; i >= 0; i--)
            for (int j = right.Length - 1; j >= 0; j--)
                lengths[i, j] = left[i] == right[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        int x = 0, y = 0;
        while (x < left.Length || y < right.Length)
        {
            if (x < left.Length && y < right.Length && left[x] == right[y]) { Line(a, left[x++], ""); Line(b, right[y++], ""); }
            else if (x < left.Length && (y == right.Length || lengths[x + 1, y] >= lengths[x, y + 1]))
            { Line(a, left[x++], "removed"); Line(b, "", "gap"); }
            else { Line(a, "", "gap"); Line(b, right[y++], "added"); }
        }
        return (a.ToString(), b.ToString());
    }
}
