using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PaperlessLlm.Auth;

namespace PaperlessLlm.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-auth-test-" + Guid.NewGuid());
    private readonly RSA rsa = RSA.Create(2048);

    [Fact]
    public async Task FullPkceLoginVerifiesSignedIdentityAndRetainsRegistration()
    {
        Dictionary<string, string>? authorization = null;
        Task<HttpResponseMessage>? callback = null;
        using var browser = new HttpClient();
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("jwks.json")) return Json(Jwks());
            var form = Query(await request.Content!.ReadAsStringAsync());
            Assert.Equal("oaiapp_test", form["client_id"]);
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal(ChatGptAuth.Resource, form["resource"]);
            Assert.Equal(authorization!["redirect_uri"], form["redirect_uri"]);
            Assert.Equal(authorization["code_challenge"], Base64(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(form["code_verifier"]))));
            return Json(Tokens(Sign(authorization["nonce"])));
        });
        using var http = new HttpClient(handler);
        var auth = new ChatGptAuth(http, new TokenStore(directory));
        int port = Port();
        var status = await auth.LoginAsync(uri =>
        {
            authorization = Query(uri.Query.TrimStart('?'));
            Assert.Equal("dynamic_agent_client", authorization["client_id"]);
            Assert.Equal("Paperless LLM", authorization["agent_name_hint"]);
            Assert.Equal("S256", authorization["code_challenge_method"]);
            callback = browser.GetAsync(authorization["redirect_uri"] + "?state=" + authorization["state"] + "&code=test-code&client_id=oaiapp_test");
            return Task.CompletedTask;
        }, port);
        using var callbackResult = await callback!;
        Assert.True(status.SignedIn);
        Assert.True(status.PlanUsageGranted);
        Assert.Equal("access-token", await auth.GetAccessTokenAsync());
        Assert.Equal(2, handler.Calls); // Exchange and JWKS; no unnecessary refresh.
        string disk = await File.ReadAllTextAsync(Path.Combine(directory, "chatgpt.json"));
        Assert.Contains("oaiapp_test", disk);
        Assert.DoesNotContain("dynamic_agent_client", disk);
    }

    [Fact]
    public async Task CallbackWithWrongStateNeverExchangesTokens()
    {
        using var browser = new HttpClient();
        Task<HttpResponseMessage>? callback = null;
        var handler = new Handler(_ => throw new Exception("Must not call endpoint"));
        using var http = new HttpClient(handler);
        var auth = new ChatGptAuth(http, new TokenStore(directory));
        await Assert.ThrowsAsync<AuthException>(() => auth.LoginAsync(uri =>
        {
            var query = Query(uri.Query.TrimStart('?'));
            callback = browser.GetAsync(query["redirect_uri"] + "?state=wrong&code=secret&client_id=oaiapp_test");
            return Task.CompletedTask;
        }, Port()));
        using var result = await callback!;
        Assert.Equal(0, handler.Calls);
        Assert.False((await auth.GetStatusAsync()).SignedIn);
    }

    [Theory]
    [InlineData("wrong-nonce", "oaiapp_test", false)]
    [InlineData("expected", "wrong-audience", false)]
    [InlineData("expected", "oaiapp_test", true)]
    public async Task IdTokenRejectsNonceAudienceAndSignatureMismatch(string nonce, string audience, bool wrongKey)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(Jwks()))));
        using var alternate = RSA.Create(2048);
        string jwt = Sign(nonce, audience, wrongKey ? alternate : null);
        var error = await Assert.ThrowsAsync<AuthException>(() => new IdTokenValidator(http).ValidateAsync(jwt, "oaiapp_test", "expected"));
        Assert.DoesNotContain(jwt, error.Message);
    }

    [Fact]
    public async Task RotatingRefreshIsSerializedAndStoredBeforeNextCaller()
    {
        var store = new TokenStore(directory);
        await Seed(store);
        var handler = new Handler(async request =>
        {
            var form = Query(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("refresh-old", form["refresh_token"]);
            Assert.Equal("oaiapp_test", form["client_id"]);
            Assert.Equal(ChatGptAuth.Resource, form["resource"]);
            Assert.False(form.ContainsKey("scope"));
            await Task.Delay(100);
            return Json(new { access_token = "new-access", refresh_token = "new-refresh", token_type = "Bearer", expires_in = 3600 });
        });
        using var http = new HttpClient(handler);
        // Separate store and auth instances share a file lock, not only an in-memory semaphore.
        var first = new ChatGptAuth(http, store);
        var second = new ChatGptAuth(http, new TokenStore(directory));
        var results = await Task.WhenAll(first.GetAccessTokenAsync(), second.GetAccessTokenAsync());
        Assert.All(results, value => Assert.Equal("new-access", value));
        Assert.Equal(1, handler.Calls);
        string disk = await File.ReadAllTextAsync(Path.Combine(directory, "chatgpt.json"));
        Assert.Contains("new-refresh", disk);
        Assert.DoesNotContain("refresh-old", disk);
    }

    [Fact]
    public async Task FailedRefreshKeepsPriorCredentialsAndRedactsBody()
    {
        var store = new TokenStore(directory);
        await Seed(store);
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("secret response") })));
        var error = await Assert.ThrowsAsync<AuthException>(() => new ChatGptAuth(http, store).GetAccessTokenAsync());
        Assert.DoesNotContain("secret", error.Message);
        Assert.Contains("refresh-old", await File.ReadAllTextAsync(Path.Combine(directory, "chatgpt.json")));
    }

    [Fact]
    public async Task LogoutRevokesRefreshTokenAndKeepsHostAndClientMapping()
    {
        var store = new TokenStore(directory);
        await Seed(store);
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(new { revocation_endpoint = "https://auth.openai.com/api/accounts/oauth/revoke" });
            var form = Query(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh-old", form["token"]);
            Assert.Equal("refresh_token", form["token_type_hint"]);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var auth = new ChatGptAuth(http, store);
        Assert.True((await auth.LogoutAsync()).RemoteRevocationConfirmed);
        Assert.False((await auth.GetStatusAsync()).SignedIn);
        string disk = await File.ReadAllTextAsync(Path.Combine(directory, "chatgpt.json"));
        Assert.Contains("oaiapp_test", disk);
        Assert.DoesNotContain("refresh-old", disk);
    }

    [Fact]
    public async Task RefreshThatDropsPlanPermissionPausesWithoutReplacingStoredGrant()
    {
        var store = new TokenStore(directory);
        await Seed(store);
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new
        {
            access_token = "new-access", refresh_token = "new-refresh", token_type = "Bearer",
            expires_in = 3600, scope = "openid"
        }))));
        var error = await Assert.ThrowsAsync<AuthException>(() => new ChatGptAuth(http, store).GetAccessTokenAsync());
        Assert.True(error.RequiresSignIn);
        Assert.Contains("refresh-old", await File.ReadAllTextAsync(Path.Combine(directory, "chatgpt.json")));
    }

    [Fact]
    public async Task LogoutFailureReportsUnconfirmedRevocationAndClearsLocally()
    {
        var store = new TokenStore(directory);
        await Seed(store);
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        var auth = new ChatGptAuth(http, store);
        var result = await auth.LogoutAsync();
        Assert.True(result.LocalCredentialsCleared);
        Assert.False(result.RemoteRevocationConfirmed);
        Assert.Equal(3, handler.Calls);
        Assert.False((await auth.GetStatusAsync()).SignedIn);
    }

    [Fact]
    public async Task ReauthorizationWithoutReturnedScopeCannotInheritPriorGrant()
    {
        var store = new TokenStore(directory);
        await Seed(store);
        Dictionary<string, string>? authorization = null;
        Task<HttpResponseMessage>? callback = null;
        using var browser = new HttpClient();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("jwks.json")) return Task.FromResult(Json(Jwks()));
            return Task.FromResult(Json(new
            {
                access_token = "ungranted-access", refresh_token = "ungranted-refresh", id_token = Sign(authorization!["nonce"]),
                token_type = "Bearer", expires_in = 3600
                // A fresh authorization must explicitly return the granted scopes.
            }));
        }));
        var auth = new ChatGptAuth(http, store);
        var error = await Assert.ThrowsAsync<AuthException>(() => auth.LoginAsync(uri =>
        {
            authorization = Query(uri.Query.TrimStart('?'));
            Assert.Equal("oaiapp_test", authorization["client_id"]);
            Assert.False(authorization.ContainsKey("agent_name_hint"));
            callback = browser.GetAsync(authorization["redirect_uri"] + "?state=" + authorization["state"] + "&code=new-code");
            return Task.CompletedTask;
        }, Port()));
        using var callbackResult = await callback!;
        Assert.True(error.RequiresSignIn);
        string disk = await File.ReadAllTextAsync(Path.Combine(directory, "chatgpt.json"));
        Assert.Contains("refresh-old", disk);
        Assert.DoesNotContain("ungranted-refresh", disk);
    }

    [Fact]
    public async Task LogoutHttpTimeoutStillClearsLocallyAndReportsUnconfirmed()
    {
        var store = new TokenStore(directory);
        await Seed(store);
        var handler = new Handler(_ => throw new TaskCanceledException("synthetic HTTP timeout"));
        using var http = new HttpClient(handler);
        var auth = new ChatGptAuth(http, store);
        var result = await auth.LogoutAsync();
        Assert.True(result.LocalCredentialsCleared);
        Assert.False(result.RemoteRevocationConfirmed);
        Assert.Equal(3, handler.Calls);
        Assert.False((await auth.GetStatusAsync()).SignedIn);
    }

    [Theory]
    [InlineData("subject", true)]
    [InlineData("other-subject", false)]
    public async Task RefreshOptionalIdentityTokenIsVerifiedBeforeRotation(string subject, bool succeeds)
    {
        var store = new TokenStore(directory);
        await Seed(store);
        string replacementId = Sign("unused-refresh-nonce", subject: subject);
        using var http = new HttpClient(new Handler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("jwks.json") ? Json(Jwks()) : Json(new
            {
                access_token = "new-access", refresh_token = "new-refresh", id_token = replacementId,
                token_type = "Bearer", expires_in = 3600
            }))));
        var auth = new ChatGptAuth(http, store);
        if (succeeds) Assert.Equal("new-access", await auth.GetAccessTokenAsync());
        else Assert.True((await Assert.ThrowsAsync<AuthException>(() => auth.GetAccessTokenAsync())).RequiresSignIn);
        string disk = await File.ReadAllTextAsync(Path.Combine(directory, "chatgpt.json"));
        if (succeeds)
        {
            Assert.Contains("new-refresh", disk);
            Assert.Contains(replacementId, disk);
        }
        else
        {
            Assert.Contains("refresh-old", disk);
            Assert.DoesNotContain("new-refresh", disk);
        }
    }

    private async Task Seed(TokenStore store)
    {
        await using var lease = await store.LockAsync(default);
        await store.WriteAsync(new Credentials
        {
            ClientId = "oaiapp_test", Subject = "subject", AccessToken = "access-old", RefreshToken = "refresh-old",
            Scopes = ["chatgpt.tokens.use.direct", "resource.invoke"], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        }, default);
    }

    private string Sign(string nonce, string audience = "oaiapp_test", RSA? key = null, string subject = "subject") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://auth.openai.com", Audience = audience, Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["nonce"] = nonce },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? rsa) { KeyId = "test" }, SecurityAlgorithms.RsaSha256)
        });
    private object Jwks()
    {
        var p = rsa.ExportParameters(false);
        return new { keys = new[] { new { kty = "RSA", kid = "test", use = "sig", alg = "RS256", n = Base64(p.Modulus!), e = Base64(p.Exponent!) } } };
    }
    private static string Base64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static object Tokens(string idToken) => new
    {
        access_token = "access-token", refresh_token = "refresh-token", id_token = idToken,
        token_type = "Bearer", expires_in = 3600,
        scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct"
    };
    private static Dictionary<string, string> Query(string query) => query.Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request); }
    }
    public void Dispose()
    {
        rsa.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
