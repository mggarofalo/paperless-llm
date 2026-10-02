using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Eval;

namespace PaperlessLlm.Tests;

public sealed class ExperimentProcessTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-eval-process-" + Guid.NewGuid().ToString("N"));
    public ExperimentProcessTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task SuccessfulChildReceivesUnicodePromptAndRecordsHashesUsageAndDiagnostics()
    {
        const string prompt = "Synthetic café receipt — 金額";
        var image = Path.Combine(directory, "synthetic.png");
        await File.WriteAllTextAsync(image, "synthetic image bytes");
        var result = await Run("""
            let input=''; for await (const chunk of process.stdin) input+=chunk;
            if(input!=='Synthetic café receipt — 金額') process.exit(21);
            console.error('synthetic diagnostic');
            console.log(JSON.stringify({type:'item.completed',item:{type:'agent_message',text:'{"ok":true}'}}));
            console.log(JSON.stringify({type:'turn.completed',usage:{input_tokens:12,output_tokens:3}}));
            console.log(JSON.stringify({type:'turn.completed',usage:{input_tokens:5,output_tokens:2}}));
            """, prompt, [image, Path.Combine(directory, "missing.png")]);
        Assert.Equal("{\"ok\":true}", result);
        using var provenance = await Provenance();
        var root = provenance.RootElement;
        Assert.Equal(Hash(prompt), root.GetProperty("prompt_sha256").GetString());
        Assert.Equal(17, root.GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, root.GetProperty("output_tokens").GetInt32());
        Assert.False(root.GetProperty("stdout_truncated").GetBoolean());
        Assert.False(root.GetProperty("timed_out").GetBoolean());
        Assert.Equal(Hash("synthetic image bytes"), root.GetProperty("images")[0].GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("images")[1].GetProperty("sha256").ValueKind);
        Assert.Contains("synthetic diagnostic", await File.ReadAllTextAsync(Artifact("stderr.log")));
        Assert.Empty(Directory.GetDirectories(directory, ".cwd-*"));
    }

    [Fact]
    public async Task NonzeroExitRetainsPartialMalformedStreamAndReportsExitWithoutEchoingStderr()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run("""
            for await (const chunk of process.stdin) { }
            process.stdout.write('{"type":"turn.completed","usage":{"input_tokens":9}}\npartial tail');
            console.error('sensitive synthetic diagnostic');
            process.exitCode=7;
            """));
        Assert.Contains("exited 7", error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
        using var provenance = await Provenance();
        Assert.Equal(9, provenance.RootElement.GetProperty("input_tokens").GetInt32());
        Assert.Contains("partial tail", await File.ReadAllTextAsync(Artifact("stdout.jsonl")));
        Assert.Contains("sensitive synthetic diagnostic", await File.ReadAllTextAsync(Artifact("stderr.log")));
        Assert.Empty(Directory.GetDirectories(directory, ".cwd-*"));
    }

    [Theory]
    [InlineData("stdout", true)]
    [InlineData("stderr", false)]
    public async Task StreamCaptureIsBoundedAndTruncatedStdoutCannotBecomeAnAcceptedResult(string stream, bool rejected)
    {
        var script = $$$"""
            for await (const chunk of process.stdin) { }
            process.{{{stream}}}.write('x'.repeat(4*1024*1024+4096)+'\n');
            console.log(JSON.stringify({type:'item.completed',item:{type:'agent_message',text:'{}'}}));
            """;
        if (rejected)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(script));
            Assert.Contains("capture limit", error.Message);
        }
        else Assert.Equal("{}", await Run(script));
        using var provenance = await Provenance();
        Assert.True(provenance.RootElement.GetProperty(stream + "_truncated").GetBoolean());
        var captured = await File.ReadAllTextAsync(Artifact(stream == "stdout" ? "stdout.jsonl" : "stderr.log"));
        Assert.InRange(captured.Length, 4 * 1024 * 1024, 4 * 1024 * 1024 + 100);
        Assert.Contains("truncated at configured capture limit", captured);
        Assert.Empty(Directory.GetDirectories(directory, ".cwd-*"));
    }

    [Fact]
    public async Task TimeoutStopsChildAndPersistsBoundedDiagnosticsWithTimeoutReason()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Run("""
            for await (const chunk of process.stdin) { }
            console.error('synthetic timeout diagnostic');
            console.log('{"type":"turn.started"}');
            setInterval(()=>{},1000);
            """, timeoutSeconds: 2));
        Assert.Contains("exceeded 2s", error.Message);
        using var provenance = await Provenance();
        Assert.True(provenance.RootElement.GetProperty("timed_out").GetBoolean());
        Assert.False(provenance.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Contains("synthetic timeout diagnostic", await File.ReadAllTextAsync(Artifact("stderr.log")));
        Assert.Empty(Directory.GetDirectories(directory, ".cwd-*"));
    }

    [Fact]
    public async Task CallerCancellationStopsRunningChildButPreservesItsOutputAndDistinctReason()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = Path.Combine(directory, "ready");
        var script = """
            import { writeFileSync } from 'node:fs';
            for await (const chunk of process.stdin) { }
            console.log('{"type":"turn.started"}');
            console.error('synthetic cancelled diagnostic');
            writeFileSync(process.argv[2],'ready');
            setInterval(()=>{},1000);
            """;
        var running = Run(script, ct: cancellation.Token, readyFile: ready);
        try
        {
            while (!File.Exists(ready)) await Task.Delay(10, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        finally { cancellation.Cancel(); }
        using var provenance = await Provenance();
        Assert.True(provenance.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.False(provenance.RootElement.GetProperty("timed_out").GetBoolean());
        Assert.Contains("synthetic cancelled diagnostic", await File.ReadAllTextAsync(Artifact("stderr.log")));
        Assert.Empty(Directory.GetDirectories(directory, ".cwd-*"));
    }

    [Fact]
    public async Task ToolEventsAreRejectedEvenWhenProcessExitsSuccessfullyAndHasFinalJson()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run("""
            for await (const chunk of process.stdin) { }
            console.log('{"type":"item.completed","item":{"type":"file_change"}}');
            console.log('{"type":"item.completed","item":{"type":"agent_message","text":"{}"}}');
            """));
        Assert.Contains("tool event", error.Message);
        Assert.Contains("file_change", await File.ReadAllTextAsync(Artifact("stdout.jsonl")));
        Assert.True(File.Exists(Artifact("provenance.json")));
    }

    private async Task<string> Run(string script, string prompt = "synthetic prompt", IReadOnlyList<string>? images = null,
        int timeoutSeconds = 30, CancellationToken ct = default, string? readyFile = null)
    {
        var path = Path.Combine(directory, "fake.mjs");
        await File.WriteAllTextAsync(path, script, ct);
        ProcessStartInfo Configure(ProcessStartInfo info)
        {
            info.FileName = "node";
            info.ArgumentList.Clear();
            info.ArgumentList.Add(path);
            if (readyFile is not null) info.ArgumentList.Add(readyFile);
            return info;
        }
        return await ExperimentRunner.InvokeCodexAsync(prompt, images ?? [], "low",
            new("unused", "unused", directory, 1, "synthetic-model", timeoutSeconds), directory, "final", ct, Configure);
    }

    private string Artifact(string suffix) => Path.Combine(directory, "final." + suffix);
    private async Task<JsonDocument> Provenance() => JsonDocument.Parse(await File.ReadAllTextAsync(Artifact("provenance.json")));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() => Directory.Delete(directory, true);
}
