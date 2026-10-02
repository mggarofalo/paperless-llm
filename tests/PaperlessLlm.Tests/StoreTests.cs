using System.Text.Json;
using PaperlessLlm.Organizer;
namespace PaperlessLlm.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ppllm-store-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData("id")]
    [InlineData("state")]
    [InlineData("attempts")]
    [InlineData("job")]
    [InlineData("filename")]
    public async Task CorruptJobIdentityOrStateFailsClosed(string fault)
    {
        var store = new OrganizerStore(root);
        using var held = store.Lock();
        var job = new OrganizerJob { DocumentId = 1 };
        if (fault == "id") job.DocumentId = 0;
        if (fault == "state") job.State = (OrganizerJobState)999;
        if (fault == "attempts") job.Attempts = -1;
        if (fault == "job") job.JobId = "../invalid";
        await store.SaveAsync(store.JobPath(fault == "filename" ? 2 : 1), job, default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.JobsAsync(default));
        Assert.Equal("invalid_organizer_job", error.Message);
    }

    [Fact]
    public async Task FailedAtomicSavePreservesPreviousStateAndRemovesTemporaryFile()
    {
        var store = new OrganizerStore(root);
        using var held = store.Lock();
        await store.SaveAsync(store.CheckpointPath, new { marker = "before" }, default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(store.CheckpointPath, new { marker = "after" }, cancelled.Token));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(store.CheckpointPath));
        Assert.Equal("before", saved.RootElement.GetProperty("marker").GetString());
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task NullStateIsRejectedRatherThanInitializingOverIt()
    {
        Directory.CreateDirectory(root);
        var store = new OrganizerStore(root);
        await File.WriteAllTextAsync(store.CheckpointPath, "null");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, default));
        Assert.Equal("invalid_organizer_state", error.Message);
    }
}
