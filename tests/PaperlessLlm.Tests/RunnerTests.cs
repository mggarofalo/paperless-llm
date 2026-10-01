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
    private PiRunner Runner(string script, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake.mjs");
        File.WriteAllText(path, script);
        return new PiRunner(new PiRunnerOptions(Path.Combine(directory, "home"), path, Timeout: timeout));
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
            const forbidden=Object.keys(process.env).filter(k=>/TOKEN|SECRET|OPENAI|PAPERLESS|NODE_OPTIONS|CODEX/i.test(k));
            if(forbidden.length||process.env.HOME!==process.cwd()||process.env.PI_CODING_AGENT_DIR!==process.cwd()) process.exit(21);
            console.log('{"type":"ready"}');
            """);
        Assert.True(await runner.StatusAsync());
    }

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

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
