using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Eval;

namespace PaperlessLlm.Tests;

public sealed class ExperimentClaudeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-eval-claude-" + Guid.NewGuid().ToString("N"));
    public ExperimentClaudeTests() => Directory.CreateDirectory(directory);

    private const string NamedKeep = """
        {"schema_version":"1","title":{"action":"keep","value":null,"evidence":[]},"date":{"action":"keep","value":null,"evidence":[]},"correspondent":{"action":"keep","value":null,"evidence":[]},"document_type":{"action":"keep","value":null,"evidence":[]},"add_tags":[],"ocr":{"action":"keep","pages":[],"evidence":[]},"uncertainty":[],"decisions":{"title":"unchanged","date":"unchanged","correspondent":"unchanged","document_type":"unchanged","note":"not_applicable"}}
        """;

    [Fact]
    public async Task ClaudeHarnessRunsTheBridgeWithProductionRequestShapeIsolatedEnvironmentAndSplitPrompt()
    {
        var home = Path.Combine(directory, "runner-home");
        var bridge = await Write("fake-bridge.mjs", $$"""
            import { writeFileSync } from 'node:fs';
            let input = ''; for await (const chunk of process.stdin) input += chunk;
            const fail = reason => { console.error(reason); process.exit(21); };
            if (process.argv[2] !== 'infer') fail('command');
            if (process.env.PPLLM_RUNNER_HOME !== {{JsonSerializer.Serialize(Path.GetFullPath(home))}}) fail('home');
            if ('ANTHROPIC_API_KEY' in process.env || process.env.HOME !== process.cwd()) fail('environment');
            const request = JSON.parse(input);
            if (request.model !== 'claude-haiku-5-5' || request.reasoning !== 'low') fail('model');
            if (!request.instructions.startsWith('Final policy.') || request.instructions.includes('DOCUMENT INPUT JSON')) fail('instructions');
            if (!request.prompt.includes('DOCUMENT INPUT JSON') || request.prompt.includes('Final policy.') || !request.prompt.includes('"Acme"')) fail('prompt');
            if (request.prompt.includes('curator-only')) fail('leak');
            if (!request.schema.properties.decisions || !Array.isArray(request.images) || request.images.length !== 0) fail('schema');
            writeFileSync('system-prompt.txt', request.instructions);
            console.log(JSON.stringify({ type: 'result', text: {{JsonSerializer.Serialize(NamedKeep.Trim())}}, usage: { input: 100, output: 20, cacheRead: 30, cacheWrite: 40 } }));
            """);
        var cases = await Write("cases.jsonl", JsonSerializer.Serialize(Case(), EvalJson.Options));
        await Write("prompt.txt", "Final policy.");
        var recipe = await Write("recipe.json", """{"id":"claude-synthetic","promptFile":"prompt.txt","reasoning":"low","outputContract":"names"}""");
        var output = Path.Combine(directory, "run");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "synthetic-key-must-not-cross");
        try
        {
            await ExperimentRunner.RunAsync(new(cases, recipe, output, 1, "claude-haiku-5-5", 60, "holdout", ExperimentRunner.ClaudeHarness, home, bridge));
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); }
        Assert.Empty(Directory.GetFiles(output, "*.error.txt"));
        using var intent = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "synthetic.json")));
        Assert.Equal("keep", intent.RootElement.GetProperty("title").GetProperty("action").GetString());
        Assert.False(intent.RootElement.TryGetProperty("decisions", out _));
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "provenance.json")));
        Assert.Equal("claude", run.RootElement.GetProperty("harness").GetString());
        Assert.Contains("claude-bridge.mjs infer", run.RootElement.GetProperty("runner").GetString());
        Assert.Contains("claude-code-2.1.314", run.RootElement.GetProperty("runner").GetString());
        var stageFile = Assert.Single(Directory.GetFiles(output, "final.provenance.json", SearchOption.AllDirectories));
        using var stage = JsonDocument.Parse(await File.ReadAllTextAsync(stageFile));
        Assert.Equal("claude", stage.RootElement.GetProperty("harness").GetString());
        Assert.Equal(100, stage.RootElement.GetProperty("input_tokens").GetInt64());
        Assert.Equal(20, stage.RootElement.GetProperty("output_tokens").GetInt64());
        Assert.Equal(30, stage.RootElement.GetProperty("cache_read_tokens").GetInt64());
        Assert.Equal(40, stage.RootElement.GetProperty("cache_write_tokens").GetInt64());
        Assert.Equal(NamedKeep.Trim(), await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(stageFile)!, "final.txt")));
        Assert.Empty(Directory.GetDirectories(output, ".cwd-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BridgeErrorCodeBecomesTheCaseErrorWithoutEchoingStderrOrPublishingOutput()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run("""
            for await (const chunk of process.stdin) { }
            console.error('sensitive synthetic diagnostic');
            console.log('{"type":"error","code":"rate_limited"}');
            process.exitCode = 22;
            """));
        Assert.Contains("rate_limited", error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.DoesNotContain("exited", error.Message);
        Assert.Contains("sensitive synthetic diagnostic", await File.ReadAllTextAsync(Path.Combine(directory, "final.stderr.log")));
        Assert.True(File.Exists(Path.Combine(directory, "final.provenance.json")));
    }

    [Fact]
    public async Task ChildThatExitsBeforeReadingStdinStillYieldsItsOwnErrorCodeAndArtifacts()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run("""
            console.log('{"type":"error","code":"auth_required"}');
            process.exit(20);
            """));
        Assert.Contains("auth_required", error.Message);
        Assert.Contains("auth_required", await File.ReadAllTextAsync(Path.Combine(directory, "final.stdout.jsonl")));
        Assert.True(File.Exists(Path.Combine(directory, "final.provenance.json")));
    }

    [Fact]
    public async Task SuccessfulExitWithoutResultOrWithNonzeroExitIsRejected()
    {
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() => Run("""
            for await (const chunk of process.stdin) { }
            console.log('{"type":"ready"}');
            """));
        Assert.Contains("no result", missing.Message);
        var crashed = await Assert.ThrowsAsync<InvalidDataException>(() => Run("""
            for await (const chunk of process.stdin) { }
            console.log(JSON.stringify({type:'result',text:'{}'}));
            process.exitCode = 1;
            """));
        Assert.Contains("exited 1", crashed.Message);
    }

    [Fact]
    public void ExtractAcceptsOnlyStringResultTextAndLetsErrorsWinOverResults()
    {
        Assert.Equal("{}", ExperimentRunner.ExtractBridgeResult("{\"type\":\"result\",\"text\":\"{}\"}\n", 0, "final"));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractBridgeResult("{\"type\":\"result\",\"text\":{}}\n", 0, "final"));
        var error = Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractBridgeResult(
            "{\"type\":\"result\",\"text\":\"{}\"}\n{\"type\":\"error\",\"code\":\"harness_unsafe\"}\n", 0, "final"));
        Assert.Contains("harness_unsafe", error.Message);
        Assert.Equal("{}", ExperimentRunner.ExtractBridgeResult("partial tail\n{\"type\":\"result\",\"text\":\"{}\"}\n", 0, "final"));
    }

    [Fact]
    public void HarnessValidationRequiresSignInHomeAndSingleStageRecipes()
    {
        var recipe = new ExperimentRecipe { Id = "r", PromptFile = "p.txt" };
        ExperimentOptions Options(string harness, string? home = null, string pipeline = "single") =>
            new("c", "r", "o", 1, Harness: harness, RunnerHome: home);
        Assert.Throws<ArgumentException>(() => ExperimentRunner.ValidateHarness(Options("gemini"), recipe));
        Assert.Throws<ArgumentException>(() => ExperimentRunner.ValidateHarness(Options("claude"), recipe));
        Assert.Throws<ArgumentException>(() => ExperimentRunner.ValidateHarness(Options("claude", " "), recipe));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateHarness(Options("claude", directory), recipe with { Pipeline = "refine" }));
        ExperimentRunner.ValidateHarness(Options("claude", directory), recipe);
        ExperimentRunner.ValidateHarness(Options("codex"), recipe with { Pipeline = "refine" });
    }

    [Fact]
    public void StartInfoMirrorsTheWorkerAllowlistAndUtf8Transport()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "synthetic-key-must-not-cross");
        ProcessStartInfo psi;
        try { psi = ExperimentRunner.CreateClaudeStartInfo("runner/claude-bridge.mjs", "home", directory); }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); }
        Assert.Equal(["infer"], psi.ArgumentList.Skip(1));
        Assert.True(Path.IsPathRooted(psi.ArgumentList[0]));
        Assert.EndsWith("claude-bridge.mjs", psi.ArgumentList[0]);
        Assert.Equal(Path.GetFullPath("home"), psi.Environment["PPLLM_RUNNER_HOME"]);
        Assert.Equal(directory, psi.Environment["HOME"]);
        Assert.Equal(directory, psi.Environment["TEMP"]);
        Assert.Equal("1", psi.Environment["NO_COLOR"]);
        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));
        Assert.Empty(psi.StandardInputEncoding!.GetPreamble());
        Assert.Equal(Encoding.UTF8.CodePage, psi.StandardOutputEncoding!.CodePage);
    }

    [Fact]
    public async Task RequestCarriesSchemaObjectAndOnlyPngOrJpegPagesAsDataUrls()
    {
        var png = Path.Combine(directory, "page.png");
        await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        var jpeg = Path.Combine(directory, "page.jpg");
        await File.WriteAllBytesAsync(jpeg, [0xFF, 0xD8, 0xFF, 0xE0, 9, 9]);
        var text = Path.Combine(directory, "page.txt");
        await File.WriteAllTextAsync(text, "not an image");
        var request = ExperimentRunner.BuildBridgeRequest("claude-sonnet-5-5", "system", "user", [png, jpeg], "{\"type\":\"object\"}", "low");
        using var doc = JsonDocument.Parse(request);
        Assert.Equal("object", doc.RootElement.GetProperty("schema").GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", doc.RootElement.GetProperty("images")[0].GetString());
        Assert.StartsWith("data:image/jpeg;base64,", doc.RootElement.GetProperty("images")[1].GetString());
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ImageDataUrl(text));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.BuildBridgeRequest("m", "s", "u", [], "{}", "xhigh"));
    }

    [Fact]
    public async Task CliRejectsClaudeHarnessWithoutRunnerHomeBeforeAnyCall()
    {
        var cases = await Write("cli-cases.jsonl", JsonSerializer.Serialize(Case(), EvalJson.Options));
        await Write("cli-prompt.txt", "Final policy.");
        var recipe = await Write("cli-recipe.json", """{"id":"cli","promptFile":"cli-prompt.txt"}""");
        var output = Path.Combine(directory, "cli-run");
        Assert.Equal(2, await EvalCli.RunAsync(["experiment", "--cases", cases, "--split", "holdout", "--recipe", recipe, "--out", output, "--harness", "claude"]));
        Assert.Equal(2, await EvalCli.RunAsync(["experiment", "--cases", cases, "--split", "holdout", "--recipe", recipe, "--out", output, "--harness", "gemini"]));
        Assert.False(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any());
    }

    private async Task<string> Run(string script, int timeoutSeconds = 30)
    {
        var path = await Write("fake.mjs", script);
        ProcessStartInfo Configure(ProcessStartInfo info)
        {
            info.FileName = "node";
            info.ArgumentList.Clear();
            info.ArgumentList.Add(path);
            return info;
        }
        var options = new ExperimentOptions("unused", "unused", directory, 1, "claude-haiku-5-5", timeoutSeconds, "holdout",
            ExperimentRunner.ClaudeHarness, directory, path);
        return await ExperimentRunner.InvokeClaudeAsync("system", "user", "{\"type\":\"object\"}", [], "low", options, directory, "final", CancellationToken.None, Configure);
    }

    private async Task<string> Write(string name, string content)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static EvalCase Case() => new()
    {
        CaseId = "synthetic", Split = "holdout", PageCount = 0,
        Document = new() { Id = 1, Title = "Acme", Content = "source OCR" },
        Taxonomy = new() { Correspondents = [new(11, "Acme")] },
        Expected = new() { Title = new() { Action = "keep" }, Date = new() { Action = "keep" }, Correspondent = new() { Action = "keep" }, DocumentType = new() { Action = "keep" } },
        Notes = "curator-only reference"
    };

    public void Dispose() => Directory.Delete(directory, true);
}
