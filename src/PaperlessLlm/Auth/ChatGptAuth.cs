using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperlessLlm.Auth;

public sealed class ChatGptAuth(HttpClient http, TokenStore store) : IAccessTokenProvider
{
    public const string Resource = "https://api.openai.com/v1";
    private const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    private const string PlanScope = "chatgpt.tokens.use.direct";

    public async Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await store.LockAsync(cancellationToken);
        var saved = await store.ReadAsync(cancellationToken);
        return Status(saved);
    }

    public async Task<AuthStatus> LoginAsync(Func<Uri, Task> showAuthorizationUrl, int port = 1455,
        string bindAddress = "127.0.0.1", CancellationToken cancellationToken = default)
    {
        if (port is < 1024 or > 65535 || bindAddress is not ("127.0.0.1" or "0.0.0.0"))
            throw new AuthException("Use a port between 1024 and 65535 and bind address 127.0.0.1 or 0.0.0.0.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = deadline.Token;
        await using var lease = await store.LockAsync(ct);
        var saved = await store.ReadAsync(ct);
        // Persist stable host identity before the browser opens, including failed/cancelled attempts.
        await store.WriteAsync(saved, ct);
        string state = Random(), nonce = Random(), verifier = Random();
        string redirect = $"http://127.0.0.1:{port}/auth/callback";
        var query = new Dictionary<string, string>
        {
            ["client_id"] = saved.ClientId ?? "dynamic_agent_client",
            ["ext_agent_host_id"] = saved.HostId,
            ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            ["resource"] = Resource, ["state"] = state, ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        };
        if (saved.ClientId is null) query["agent_name_hint"] = "Paperless LLM";
        // Avoid placing retained ID tokens in terminal URLs; account selection remains available.
        var authorize = new Uri("https://auth.openai.com/api/accounts/authorize?" + Encode(query));
        var listener = new TcpListener(IPAddress.Parse(bindAddress), port);
        try
        {
            listener.Start(4);
            await showAuthorizationUrl(authorize);
            var callback = await ReceiveCallbackAsync(listener, port, ct);
            if (!callback.TryGetValue("state", out string? returnedState) || !IdTokenValidator.FixedEquals(state, returnedState))
                throw new AuthException("The sign-in callback state did not match. Start sign-in again.");
            if (callback.ContainsKey("error")) throw new AuthException("ChatGPT authorization was declined or failed.");
            if (!callback.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
                throw new AuthException("The sign-in callback was incomplete.");
            callback.TryGetValue("client_id", out string? issuedId);
            if (saved.ClientId is not null && issuedId is not null && issuedId != saved.ClientId)
                throw new AuthException("The sign-in registration did not match the selected account.");
            string clientId = saved.ClientId ?? issuedId ?? throw new AuthException("The sign-in registration was incomplete.");
            if (clientId == "dynamic_agent_client" || clientId.Length is < 1 or > 256
                || clientId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
                throw new AuthException("The sign-in registration was incomplete.");
            // Retain issued registration even if exchange fails; the next attempt reuses it.
            saved.ClientId = clientId;
            await store.WriteAsync(saved, ct);
            using var token = await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = clientId,
                ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect,
                ["resource"] = Resource
            }, ct);
            string idToken = Required(token.RootElement, "id_token");
            string subject = await new IdTokenValidator(http).ValidateAsync(idToken, clientId, nonce, ct);
            if (saved.Subject is not null && saved.Subject != subject)
                throw new AuthException("The verified sign-in identity did not match the selected account.");
            var updated = ParseTokens(token.RootElement, saved, allowInheritedScopes: false);
            updated.Subject = subject;
            updated.IdToken = idToken;
            await store.WriteAsync(updated, ct);
            return Status(updated);
        }
        catch (Exception ex) when (ex is SocketException or IOException or HttpRequestException or JsonException)
        {
            throw new AuthException("ChatGPT sign-in could not complete. Check connectivity and callback port availability.");
        }
        finally { listener.Stop(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await store.LockAsync(cancellationToken);
        var saved = await store.ReadAsync(cancellationToken);
        if (!Status(saved).SignedIn || !saved.Scopes.Contains(PlanScope))
            throw new AuthException("Sign in with ChatGPT and grant plan usage before running inference.", requiresSignIn: true);
        if (saved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return saved.AccessToken!;
        if (saved.EarliestRefreshAt > DateTimeOffset.UtcNow)
        {
            if (saved.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(10)) return saved.AccessToken!;
            throw new AuthException("ChatGPT credentials cannot be refreshed yet. Retry later.");
        }
        using var token = await ExchangeAsync(new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = saved.ClientId!,
            ["refresh_token"] = saved.RefreshToken!, ["resource"] = Resource
        }, cancellationToken); // No scope: preserve the existing grant.
        var updated = ParseTokens(token.RootElement, saved, allowInheritedScopes: true);
        if (token.RootElement.TryGetProperty("id_token", out var newId))
        {
            var subject = await new IdTokenValidator(http).ValidateAsync(newId.GetString()!, saved.ClientId!, null, cancellationToken);
            if (subject != saved.Subject) throw new AuthException("The refreshed sign-in identity did not match.", requiresSignIn: true);
            updated.IdToken = newId.GetString();
        }
        await store.WriteAsync(updated, cancellationToken);
        return updated.AccessToken!;
    }

    public async Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await store.LockAsync(cancellationToken);
        var saved = await store.ReadAsync(cancellationToken);
        bool revoked = saved.RefreshToken is null;
        if (saved.RefreshToken is not null)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var discovery = await http.GetAsync("https://auth.openai.com/.well-known/openid-configuration", cancellationToken);
                    if (discovery.IsSuccessStatusCode)
                    {
                        using var metadata = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(cancellationToken));
                        var endpoint = new Uri(metadata.RootElement.GetProperty("revocation_endpoint").GetString()!);
                        if (endpoint.Scheme != "https" || endpoint.Host != "auth.openai.com" || !endpoint.IsDefaultPort)
                            throw new AuthException("The OpenAI revocation endpoint was invalid.");
                        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["token"] = saved.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = saved.ClientId!
                        }), cancellationToken);
                        if (response.StatusCode == HttpStatusCode.OK) { revoked = true; break; }
                        if ((int)response.StatusCode < 500) break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or UriFormatException or AuthException)
                { /* No token or remote body is included in diagnostics. */ }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { /* HttpClient timeout: bounded retry, then clear locally with unconfirmed revocation. */ }
                if (attempt < 2) await Task.Delay(250 * (attempt + 1), cancellationToken);
            }
        }
        await store.WriteAsync(new Credentials { HostId = saved.HostId, ClientId = saved.ClientId, Subject = saved.Subject }, cancellationToken);
        return new LogoutResult(true, revoked);
    }

    private async Task<JsonDocument> ExchangeAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(fields), ct);
            if (!response.IsSuccessStatusCode)
                throw new AuthException(response.StatusCode == HttpStatusCode.BadRequest
                    ? "ChatGPT credentials were rejected. Sign in again; no API-key fallback was attempted."
                    : "ChatGPT token renewal is unavailable. Retry later; no API-key fallback was attempted.",
                    requiresSignIn: response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        { throw new AuthException("ChatGPT token exchange could not complete."); }
    }

    private static Credentials ParseTokens(JsonElement token, Credentials previous, bool allowInheritedScopes)
    {
        try
        {
            var scopes = token.TryGetProperty("scope", out var scope)
                ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : allowInheritedScopes ? previous.Scopes : [];
            if (!scopes.Contains(PlanScope) || !scopes.Contains("resource.invoke"))
                throw new AuthException("ChatGPT plan usage was not granted. Sign in again with plan usage enabled.", requiresSignIn: true);
            if (!string.Equals(Required(token, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new AuthException("ChatGPT returned an unsupported token type.");
            int expiresIn = token.GetProperty("expires_in").GetInt32();
            if (expiresIn is <= 0 or > 86400) throw new AuthException("ChatGPT returned an invalid credential lifetime.");
            DateTimeOffset? earliest = null;
            if (token.TryGetProperty("earliest_refresh_at", out var refreshAt) && refreshAt.ValueKind == JsonValueKind.Number)
                earliest = DateTimeOffset.FromUnixTimeSeconds(refreshAt.GetInt64());
            return new Credentials
            {
                HostId = previous.HostId, ClientId = previous.ClientId, Subject = previous.Subject,
                AccessToken = Required(token, "access_token"), RefreshToken = Required(token, "refresh_token"),
                IdToken = previous.IdToken, Scopes = scopes,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn), EarliestRefreshAt = earliest
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentOutOfRangeException)
        { throw new AuthException("ChatGPT returned incomplete credentials."); }
    }

    private static string Required(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw new AuthException("ChatGPT returned incomplete credentials.");

    private static AuthStatus Status(Credentials saved) => new(
        saved.ClientId is not null && saved.AccessToken is not null && saved.RefreshToken is not null && saved.Subject is not null,
        saved.Scopes.Contains(PlanScope), saved.ExpiresAt);
    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Encode(Dictionary<string, string> values) =>
        string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private static async Task<Dictionary<string, string>> ReceiveCallbackAsync(TcpListener listener, int port, CancellationToken ct)
    {
        using var connection = await listener.AcceptTcpClientAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var stream = connection.GetStream();
        var bytes = new List<byte>();
        var next = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(next, timeout.Token) == 0)
                throw new AuthException("The callback request was incomplete.");
            bytes.Add(next[0]);
            if (bytes.Count > 16384) throw new AuthException("The callback request was too large.");
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
        string line = lines[0];
        string? host = null;
        foreach (var header in lines.Skip(1))
        {
            if (header.Length == 0) break;
            if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                if (host is not null) throw new AuthException("The callback request was invalid.");
                host = header[5..].Trim();
            }
        }
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith("/auth/callback?", StringComparison.Ordinal)
            || host != $"127.0.0.1:{port}") throw new AuthException("The callback request was invalid.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parts[1][("/auth/callback?".Length)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2 || !result.TryAdd(Uri.UnescapeDataString(kv[0]), Uri.UnescapeDataString(kv[1].Replace('+', ' '))))
                throw new AuthException("The callback request was invalid.");
        }
        byte[] body = Encoding.UTF8.GetBytes("Sign-in response received. Return to Paperless LLM to see the result.");
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(headers, timeout.Token);
        await stream.WriteAsync(body, timeout.Token);
        return result;
    }
}
