using System.Text.Json;
using PaperlessLlm.Auth;
using PaperlessLlm.Inference;
using PaperlessLlm.Runner;

namespace PaperlessLlm.Tests;

public sealed class RunnerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-runner-test-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new { type = "object" });
    private const string Image = "data:image/png;base64,aGVsbG8=";
    private static readonly string CodexDeviceUrl = RunnerHarness.Codex.DeviceLoginUrl!;
    [Theory]
    [InlineData("{\"input\":12,\"output\":3,\"cacheRead\":2,\"cacheWrite\":0}", true)]
    [InlineData("null", false)]
    [InlineData("{\"input\":-1}", false)]
    [InlineData("{\"input\":\"secret\"}", false)]
    [InlineData("{\"input\":0.5}", false)]
    public async Task ProviderUsageIsOptionalAndNumericOnly(string usage, bool reported)
    {
        var runner = Runner("let input='';for await(const c of process.stdin)input+=c;console.log(JSON.stringify({type:'result',text:'{}',usage:" + usage + "}));");
        var result = await runner.RunWithUsageAsync("model", "policy", "text", [], Schema);
        Assert.Equal(reported, result.Usage is not null);
        if (reported) Assert.Equal(new PaperlessLlm.Organizer.ProviderUsage(12, 3, 2, 0), result.Usage);
        Assert.Equal("{}", result.Text);
    }
    private BridgeRunner Runner(string script, TimeSpan? timeout = null, bool terminalLogin = false)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake.mjs");
        File.WriteAllText(path, script);
        return new BridgeRunner(new BridgeRunnerOptions(Path.Combine(directory, "home"), path, Timeout: timeout, DeviceLoginUrl: CodexDeviceUrl, TerminalLogin: terminalLogin));
    }

    [Fact]
    public async Task TransportsTextOnlyRequestAndLowReasoning()
    {
        Runner("""
            let input=''; for await(const c of process.stdin) input+=c;
            const r=JSON.parse(input);
            if(r.images.length!==0||r.reasoning!=='low') process.exit(21);
            console.log(JSON.stringify({type:'result',text:'{}'}));
            """);
        var runner = new BridgeRunner(new(Path.Combine(directory, "home"), Path.Combine(directory, "fake.mjs"), Reasoning: "low"));
        Assert.Equal("{}", await runner.RunAsync("gpt-6-sol", "policy", "OCR", [], Schema));
    }

    [Fact]
    public async Task TransportsImagesSchemaAndPromptThroughStdinAndCleansWork()
    {
        var runner = Runner("""
            let input=''; for await(const c of process.stdin) input+=c;
            const r=JSON.parse(input);
            if(r.model!=='gpt-6-luna'||r.schema.type!=='object'||r.images[0]!=='data:image/png;base64,aGVsbG8='||r.prompt!=='private OCR') process.exit(21);
            console.log(JSON.stringify({type:'result',text:'{"title":"test"}'}));
            """);
        var result = await runner.RunAsync("gpt-6-luna", "policy", "private OCR", [Image], Schema);
        Assert.Equal("{\"title\":\"test\"}", result);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory, "home", "work")));
    }

    [Fact]
    public async Task ChildEnvironmentDoesNotContainApplicationSecretsOrNodeOptions()
    {
        var runner = Runner("""
            import { realpathSync } from 'node:fs';
            const forbidden=Object.keys(process.env).filter(k=>/TOKEN|SECRET|OPENAI|ANTHROPIC|CLAUDE|PAPERLESS|NODE_OPTIONS|CODEX|PI_/i.test(k));
            // macOS resolves /var to /private/var in cwd; compare the actual directories.
            if(forbidden.length||realpathSync(process.env.HOME)!==realpathSync(process.cwd())||!process.env.PPLLM_RUNNER_HOME) process.exit(21);
            console.log('{"type":"ready"}');
            """);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "synthetic-separately-billed-key");
        try { Assert.True(await runner.StatusAsync()); }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); }
    }

    [Fact]
    public async Task HarnessWithoutDeviceCodeLoginRejectsAnyDeviceCode()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake.mjs");
        File.WriteAllText(path, "console.log(JSON.stringify({type:'device_code',url:'https://auth.openai.com/codex/device',code:'ABCD'}));");
        var runner = new BridgeRunner(new(Path.Combine(directory, "home"), path));
        var error = await Assert.ThrowsAsync<AuthException>(() => runner.LoginAsync(_ => { }));
        Assert.Equal("runner_device_code_invalid", error.Message);
    }

    [Fact]
    public async Task TerminalLoginIsConfirmedBySavedStatus()
    {
        var marker = Path.Combine(directory, "home", "signed-in").Replace("\\", "/");
        var runner = Runner($$"""
            import { existsSync, writeFileSync } from 'node:fs';
            if(process.argv[2]==='login') writeFileSync('{{marker}}','');
            console.log(JSON.stringify({type:existsSync('{{marker}}')?'ready':'auth_required'}));
            """, terminalLogin: true);
        var displayed = false;
        await runner.LoginAsync(_ => displayed = true);
        Assert.False(displayed);
        Assert.True(await runner.StatusAsync());
    }

    [Theory]
    [InlineData("process.exitCode=0;", "runner_sign_in_incomplete")]
    [InlineData("if(process.argv[2]==='login') process.exitCode=21;", "runner_sign_in_failed")]
    public async Task TerminalLoginFailureRequiresSignIn(string script, string expected)
    {
        var runner = Runner(script + "if(process.argv[2]==='status') console.log('{\"type\":\"auth_required\"}');", terminalLogin: true);
        var error = await Assert.ThrowsAsync<AuthException>(() => runner.LoginAsync(_ => { }));
        Assert.Equal(expected, error.Message);
        Assert.True(error.RequiresSignIn);
    }

    [Theory]
    [InlineData("codex", "bridge.mjs", "pi", HarnessLogin.DeviceCode)]
    [InlineData("claude", "claude-bridge.mjs", "claude", HarnessLogin.Terminal)]
    public void HarnessNameSelectsBridgeAuthHomeAndLogin(string name, string bridge, string auth, HarnessLogin login)
    {
        var harness = RunnerHarness.Parse(name);
        Assert.Equal((bridge, auth, login), (harness.BridgeFile, harness.AuthSubdirectory, harness.Login));
        Assert.Equal(login == HarnessLogin.DeviceCode, harness.DeviceLoginUrl is not null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Claude")]
    [InlineData("openai")]
    public void UnknownHarnessFailsWithoutFallback(string name) =>
        Assert.Throws<ArgumentException>(() => RunnerHarness.Parse(name));

    [Fact]
    public async Task DeviceCodeCallbackIsDeliveredBeforeProcessCompletes()
    {
        var runner = Runner("console.log(JSON.stringify({type:'device_code',url:'https://auth.openai.com/codex/device',code:'ABCD-1234'}));setTimeout(()=>console.log('{\"type\":\"ready\"}'),200);");
        DeviceLogin? code = null;
        await runner.LoginAsync(value => code = value);
        Assert.Equal("ABCD-1234", code!.Code);
    }

    [Fact]
    public async Task AuthFailureIsTypedAndDoesNotExposeStderr()
    {
        var runner = Runner("console.error('private secret credential');console.log('{\"type\":\"error\",\"code\":\"auth_required\"}');process.exitCode=20;");
        var error = await Assert.ThrowsAsync<AuthException>(() => runner.StatusAsync());
        Assert.True(error.RequiresSignIn);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task RateLimitIsTypedSeparatelyFromCredentialFailure()
    {
        var runner = Runner("console.log('{\"type\":\"error\",\"code\":\"rate_limited\"}');process.exitCode=22;");
        var error = await Assert.ThrowsAsync<RunnerRateLimitException>(() => runner.StatusAsync());
        Assert.Equal("runner_rate_limited", error.Message);
    }

    [Fact]
    public async Task MissingCredentialStatusIsFalseAndModelsAreParsed()
    {
        var runner = Runner("console.log(process.argv[2]==='models'?'{\"type\":\"models\",\"models\":[\"gpt-6-luna\"]}':'{\"type\":\"auth_required\"}');");
        Assert.False(await runner.StatusAsync());
        Assert.Equal(["gpt-6-luna"], await runner.ListModelsAsync());
    }

    [Fact]
    public async Task TimeoutKillsChildAndReleasesVolumeLock()
    {
        var runner = Runner("setInterval(()=>{},1000);", TimeSpan.FromMilliseconds(250));
        var error = await Assert.ThrowsAsync<InferenceException>(() => runner.StatusAsync());
        Assert.Equal("runner_timeout", error.Message);
        using var held = new FileStream(Path.Combine(directory, "home", ".runner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        var runner = Runner("setInterval(()=>{},1000);");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.StatusAsync(cancellation.Token));
    }

    [Fact]
    public async Task OutputLimitKillsChildWithoutWaitingForTimeout()
    {
        var runner = Runner("process.stdout.write('x'.repeat(5*1024*1024));setInterval(()=>{},1000);");
        var error = await Assert.ThrowsAsync<InferenceException>(() => runner.StatusAsync());
        Assert.Equal("runner_output_limit", error.Message);
    }

    [Fact]
    public async Task VolumeLockRejectsConcurrentAuthAndInference()
    {
        var runner = Runner("console.log('{\"type\":\"ready\"}');");
        Directory.CreateDirectory(Path.Combine(directory, "home"));
        using var held = new FileStream(Path.Combine(directory, "home", ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var error = await Assert.ThrowsAsync<InferenceException>(() => runner.StatusAsync());
        Assert.Equal("runner_busy", error.Message);
    }

    [Theory]
    [InlineData("private-provider-body", "runner_request_failed")]
    [InlineData("model_access_denied", "runner_model_access_denied")]
    [InlineData("context_limit", "runner_context_limit")]
    [InlineData("transport_failed", "runner_transport_failed")]
    public async Task ProviderErrorsOnlyExposeAllowlistedCodes(string code, string expected)
    {
        var runner = Runner($"console.log(JSON.stringify({{type:'error',code:'{code}'}}));process.exitCode=21;");
        var error = await Assert.ThrowsAsync<InferenceException>(() => runner.StatusAsync());
        Assert.Equal(expected, error.Message);
    }

    [Theory]
    [InlineData("console.log('not json');")]
    [InlineData("process.stdout.write('{\"type\":\"ready\"}');")]
    public async Task MalformedOrUnterminatedProtocolIsRejected(string script)
    {
        var error = await Assert.ThrowsAsync<InferenceException>(() => Runner(script).StatusAsync());
        Assert.Equal("runner_protocol_invalid", error.Message);
    }

    [Theory]
    [InlineData("https://attacker.example/device", "ABCD")]
    [InlineData("https://auth.openai.com/codex/device", "bad code")]
    public async Task DeviceCodeCannotRedirectUserToUntrustedUrl(string url, string code)
    {
        var runner = Runner($"console.log(JSON.stringify({{type:'device_code',url:'{url}',code:'{code}'}}));");
        var displayed = false;
        var error = await Assert.ThrowsAsync<AuthException>(() => runner.LoginAsync(_ => displayed = true));
        Assert.Equal("runner_device_code_invalid", error.Message);
        Assert.False(displayed);
    }

    [Fact]
    public async Task MissingResultIsNotAcceptedAsInferenceSuccess()
    {
        var error = await Assert.ThrowsAsync<InferenceException>(() => Runner("console.log('{\"type\":\"ready\"}');")
            .RunAsync("model", "policy", "text", [], Schema));
        Assert.Equal("runner_missing_result", error.Message);
    }

    [Fact]
    public async Task NonImageDataUrlsNeverReachChild()
    {
        var error = await Assert.ThrowsAsync<InferenceException>(() => Runner("throw Error('must not run')")
            .RunAsync("model", "policy", "text", ["file:///secret"], Schema));
        Assert.Equal("runner_image_invalid", error.Message);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
