using PaperlessLlm.Runner;
using System.Text.Json;
using PaperlessLlm.Organizer;

namespace PaperlessLlm.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection("Process environment")]
public sealed class CliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-cli-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> previous = [];
    public CliTests()
    {
        Directory.CreateDirectory(directory);
        Set("STATE_DIRECTORY", Path.Combine(directory, "state"));
        Set("RUNNER_HOME", Path.Combine(directory, "auth"));
        Set("POLL_SECONDS", "3600");
        Set("MODEL", "synthetic-model");
        Set("PROMPT_FILE", Path.Combine(AppContext.BaseDirectory, "prompts", "organization.txt"));
        var bridge = Path.Combine(directory, "fake.mjs");
        File.WriteAllText(bridge, """
            const command = process.argv[2];
            const emit = value => console.log(JSON.stringify(value));
            if (command === 'login') {
              emit({type:'device_code',url:'https://auth.openai.com/codex/device',code:'ABCD-1234'});
              emit({type:'ready'});
            } else if(command === 'status') emit({type:'ready'});
            else if(command === 'logout') emit({type:'logged_out'});
            else if(command === 'models') emit({type:'models',models:['synthetic-model']});
            else {
              let input='';for await(const c of process.stdin)input+=c;
              const request=JSON.parse(input);
              if(request.schema.properties.ready) emit({type:'result',text:'{"ready":true}'});
              else {
                const keep={action:'keep',value:null,evidence:[]};
                const set=value=>({action:'set',value,evidence:['Synthetic receipt']});
                emit({type:'result',text:JSON.stringify({schema_version:'1',title:keep,date:set('2026-04-02'),
                  correspondent:set('Cedar Market'),document_type:set('Receipt'),
                  add_tags:[{name:'receipts',evidence:['Receipt']}],ocr:{action:'keep',pages:[],evidence:[]},uncertainty:[]})});
              }
            }
            """);
        Set("RUNNER_BRIDGE", bridge);
        Set("NODE", "node");
    }
    private void Set(string key, string value)
    {
        var name = "PPLLM_" + key;
        previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }
    public void Dispose()
    {
        foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("status")]
    [InlineData("models")]
    [InlineData("probe")]
    [InlineData("probe", "organization")]
    [InlineData("auth", "status")]
    [InlineData("auth", "login")]
    [InlineData("auth", "logout")]
    public async Task OfflineCommandsWorkWithoutPaperlessSecrets(params string[] args)
    {
        Set("PAPERLESS_URL", "");
        Set("PAPERLESS_TOKEN_FILE", "");
        Assert.Equal(0, await OrganizerCli.RunAsync(args, default));
    }

    [Fact]
    public async Task HealthFailsForUninitializedState()
        => Assert.Equal(1, await OrganizerCli.RunAsync(["health"], default));

    [Theory]
    [InlineData("unknown")]
    [InlineData("auth")]
    [InlineData("auth", "unknown")]
    [InlineData("probe", "unknown")]
    public async Task InvalidCommandsFailBeforeNetworkAccess(params string[] args)
        => await Assert.ThrowsAsync<ArgumentException>(() => OrganizerCli.RunAsync(args, default));

    [Fact]
    public async Task EntrypointReturnsFailureForInvalidConfiguration()
    {
        Set("POLL_SECONDS", "not an integer");
        Assert.Equal(1, await Program.Main(["worker"]));
    }
    [Fact]
    public async Task BulkCommandsWorkOfflineWithActiveWorkerAndExposeDurableProgress()
    {
        var state = Path.Combine(directory, "state");
        var store = new OrganizerStore(state);
        using var held = store.Lock();
        await store.SaveAsync(store.CheckpointPath, new OrganizerCheckpoint { SourceUrl = "https://example.test/" }, default);
        await store.SaveAsync(store.JobPath(1), new OrganizerJob { DocumentId = 1, State = OrganizerJobState.Completed }, default);
        await store.SaveAsync(store.JobPath(2), new OrganizerJob { DocumentId = 2, State = OrganizerJobState.Failed }, default);
        Set("PAPERLESS_URL", ""); Set("PAPERLESS_TOKEN_FILE", ""); Set("NODE", "nonexistent");
        Assert.Equal(0, await OrganizerCli.RunAsync(["reprocess", "--all", "--preview", "--json"], default));
        Assert.Empty(await new ReprocessingQueue(state).ListAsync());
        Assert.Equal(0, await OrganizerCli.RunAsync(["reprocess", "--all", "--preview"], default));
        Assert.Equal(0, await OrganizerCli.RunAsync(["reprocess", "--ids", "1-2,1", "--notes-only", "--json"], default));
        var run = Assert.Single(await new ReprocessingQueue(state).ListAsync());
        Assert.Equal(2, run.Items.Length);
        Assert.Equal(0, await OrganizerCli.RunAsync(["status", "--run", run.RunId], default));
        Assert.Equal(0, await OrganizerCli.RunAsync(["status", "--run", run.RunId, "--json"], default));
        Assert.Equal(0, await OrganizerCli.RunAsync(["runs"], default));
        Assert.Equal(0, await OrganizerCli.RunAsync(["runs", "cancel", run.RunId], default));
        Assert.Equal(0, await OrganizerCli.RunAsync(["status", "--run", run.RunId, "--watch"], default));
        Assert.Equal(0, await OrganizerCli.RunAsync(["runs", "resume", run.RunId], default));
        Assert.Equal(0, await OrganizerCli.RunAsync(["reprocess", "1"], default));
        Assert.Equal(2, (await new ReprocessingQueue(state).ListAsync()).Count);
        var requestId = Guid.NewGuid().ToString("N");
        Assert.Equal(0, await OrganizerCli.RunAsync(["reprocess", "--all", "--request-id", requestId], default));
        await store.SaveAsync(store.JobPath(1), new OrganizerJob { DocumentId = 1, State = OrganizerJobState.Running }, default);
        Assert.Equal(0, await OrganizerCli.RunAsync(["reprocess", "--all", "--request-id", requestId], default));
        Assert.Equal(3, (await new ReprocessingQueue(state).ListAsync()).Count);
        Assert.Null((await new ReprocessingQueue(state).GetAsync(requestId)).Items[0].SkipReason);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrganizerCli.RunAsync(["status", "--run", run.RunId, "--watch"], cancellation.Token));
    }
    [Theory]
    [InlineData("reprocess")]
    [InlineData("reprocess", "--all", "--ids", "1")]
    [InlineData("reprocess", "--ids")]
    [InlineData("reprocess", "--ids", "0")]
    [InlineData("reprocess", "--ids", "1-0")]
    [InlineData("reprocess", "--ids", "1-10001")]
    [InlineData("reprocess", "--ids", "1-x")]
    [InlineData("reprocess", "--ids", "1-2-3")]
    [InlineData("reprocess", "--ids", "x")]
    [InlineData("reprocess", "--all", "--processed-before", "nonsense")]
    [InlineData("reprocess", "--all", "--limit", "bad")]
    [InlineData("reprocess", "--all", "--limit", "0")]
    [InlineData("reprocess", "--all", "--all")]
    [InlineData("reprocess", "--all", "--wat")]
    [InlineData("reprocess", "--all", "--request-id", "bad")]
    [InlineData("reprocess", "--all", "--include-unenrolled")]
    [InlineData("reprocess", "--ids", "1", "--include-unenrolled", "--limit", "2")]
    [InlineData("runs", "stop")]
    [InlineData("status", "--run")]
    [InlineData("status", "--run", "invalid", "--bogus")]
    public async Task InvalidBulkOptionsFailBeforeNetworkOrStateWrites(params string[] args) =>
        await Assert.ThrowsAsync<ArgumentException>(() => OrganizerCli.RunAsync(args, default));

    [Fact] public void SelectionArgumentsKeepUtcCutoffAndDeduplicateRanges()
    {
        var args = ReprocessingArguments.Parse(["--ids", "2,1-3", "--processed-before", "2026-10-01T00:00:00Z", "--limit", "3"]);
        Assert.Equal(new[] { 1, 2, 3 }, args.Selection.Ids);
        Assert.Equal(TimeSpan.Zero, args.Selection.ProcessedBefore!.Value.Offset);
        Assert.Equal(3, args.Selection.Limit);
    }
}
