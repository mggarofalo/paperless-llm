using PaperlessLlm.Worker;

namespace PaperlessLlm.Tests;

public sealed class HealthTests
{
    [Fact]
    public void LongBatchIsHealthyWhileJobsContinueToAdvance()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new WorkerStatus(true, 1, false, null, now.AddHours(-1), null, null,
            new Dictionary<string, int>(), [new ReviewJob { UpdatedAt = now.AddMinutes(-2), Status = JobStatus.Processing }]);
        Assert.True(Program.IsHealthy(status, now, TimeSpan.FromMinutes(20)));
        Assert.False(Program.IsHealthy(status, now.AddHours(1), TimeSpan.FromMinutes(20)));
        Assert.False(Program.IsHealthy(status with { AuthenticationPaused = true }, now, TimeSpan.FromMinutes(20)));
        Assert.False(Program.IsHealthy(status with { PauseReason = "audit_capacity_reached" }, now, TimeSpan.FromMinutes(20)));
    }
}
