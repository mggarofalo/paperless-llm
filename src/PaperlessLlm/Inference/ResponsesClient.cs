using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Auth;

namespace PaperlessLlm.Inference;

public sealed record InferenceInput(string Text, IReadOnlyList<string>? ImageDataUrls = null);
public sealed record AvailableModel(string Slug, string DisplayName);
public sealed class InferenceException(string message) : Exception(message);

// All requests use the official public endpoint and OAuth provider. No tools or API-key fallback.
public sealed class ResponsesClient(HttpClient http, IAccessTokenProvider tokens)
{
    public async Task<IReadOnlyList<AvailableModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));
        using var response = await SendAsync(request, cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.GetProperty("models").EnumerateArray()
                .Where(model => model.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list")
                .Select(model => new AvailableModel(model.GetProperty("slug").GetString()!, model.GetProperty("display_name").GetString()!))
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InferenceException("OpenAI returned an invalid model catalog."); }
    }

    public async Task<string> CompleteAsync(string model, string instructions, IReadOnlyList<InferenceInput> inputs,
        JsonElement? schema = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128) throw new ArgumentException("Choose an explicit model.", nameof(model));
        if (inputs.Count == 0) throw new ArgumentException("At least one input is required.", nameof(inputs));
        var messages = new List<object>();
        long totalBytes = Encoding.UTF8.GetByteCount(instructions);
        foreach (var input in inputs)
        {
            var content = new List<object> { new { type = "input_text", text = input.Text } };
            totalBytes += Encoding.UTF8.GetByteCount(input.Text);
            foreach (var image in input.ImageDataUrls ?? [])
            {
                bool supported = new[] { "data:image/png;base64,", "data:image/jpeg;base64,", "data:image/webp;base64," }
                    .Any(prefix => image.StartsWith(prefix, StringComparison.Ordinal));
                if (!supported) throw new ArgumentException("Images must be embedded PNG, JPEG, or WebP data URLs.", nameof(inputs));
                totalBytes += image.Length;
                content.Add(new { type = "input_image", image_url = image, detail = "high" });
            }
            messages.Add(new { role = "user", content });
        }
        if (totalBytes > 40 * 1024 * 1024) throw new ArgumentException("Inference input exceeds the 40 MiB safety limit.", nameof(inputs));
        var payload = new Dictionary<string, object>
        {
            ["model"] = model, ["instructions"] = instructions, ["input"] = messages,
            ["store"] = false, ["stream"] = true
        };
        if (schema.HasValue) payload["text"] = new { format = new { type = "json_schema", name = "paperless_proposal", strict = true, schema = schema.Value } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await SendAsync(request, deadline.Token);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new InferenceException("OpenAI did not return the required event stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        return await ReadCompletedAsync(stream, deadline.Token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AuthException("ChatGPT authorization was rejected. Sign in again or check plan permissions.", requiresSignIn: true);
            throw new InferenceException(status switch
            {
                HttpStatusCode.TooManyRequests => "ChatGPT usage is limited. Retry later; no paid API fallback was attempted.",
                _ => "OpenAI inference is unavailable. The request was not applied or automatically retried."
            });
        }
        catch (HttpRequestException) { throw new InferenceException("OpenAI could not be reached. No changes were applied."); }
    }

    // Do not release streamed deltas: late failures can follow apparently valid text.
    public static async Task<string> ReadCompletedAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var bounded = new BoundedStream(stream, 8 * 1024 * 1024);
        using var reader = new StreamReader(bounded, Encoding.UTF8, false, 8192, leaveOpen: true);
        var eventData = new StringBuilder();
        int total = 0;
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                total += line.Length;
                if (total > 8 * 1024 * 1024) throw new InferenceException("The inference stream exceeded its size limit.");
                if (line.Length != 0)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal)) eventData.AppendLine(line[5..].TrimStart(' '));
                    continue;
                }
                if (eventData.Length == 0) continue;
                string data = eventData.ToString();
                eventData.Clear();
                if (data.Trim() == "[DONE]") break;
                using var json = JsonDocument.Parse(data);
                var root = json.RootElement;
                string? type = root.GetProperty("type").GetString();
                if (type is "response.failed" or "response.incomplete" or "error")
                    throw new InferenceException("Inference failed or was incomplete. No proposal was accepted.");
                if (type != "response.completed") continue;
                var result = root.GetProperty("response");
                if (result.GetProperty("status").GetString() != "completed")
                    throw new InferenceException("Inference did not complete successfully.");
                var text = new StringBuilder();
                foreach (var output in result.GetProperty("output").EnumerateArray())
                {
                    string? kind = output.GetProperty("type").GetString();
                    if (kind == "reasoning") continue;
                    if (kind != "message" || output.GetProperty("role").GetString() != "assistant")
                        throw new InferenceException("Inference returned an unexpected tool or output item.");
                    foreach (var part in output.GetProperty("content").EnumerateArray())
                    {
                        if (part.GetProperty("type").GetString() != "output_text")
                            throw new InferenceException("Inference refused or returned unsupported content.");
                        text.Append(part.GetProperty("text").GetString());
                    }
                }
                if (text.Length == 0) throw new InferenceException("Inference completed without output text.");
                return text.ToString();
            }
            throw new InferenceException("Inference stream ended before completion. No proposal was accepted.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        { throw new InferenceException("Inference returned an invalid or interrupted stream. No proposal was accepted."); }
    }

    private sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int amount = inner.Read(buffer, offset, Math.Min(count, (int)Math.Max(1, limit - read + 1)));
            Check(amount); return amount;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int amount = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, (int)Math.Max(1, limit - read + 1))], cancellationToken);
            Check(amount); return amount;
        }
        private void Check(int count)
        {
            read += count;
            if (read > limit) throw new InferenceException("The inference stream exceeded its size limit.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
