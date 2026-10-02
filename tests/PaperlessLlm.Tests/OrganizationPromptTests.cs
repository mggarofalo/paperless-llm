using PaperlessLlm.Intent;

namespace PaperlessLlm.Tests;

public sealed class OrganizationPromptTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "ppllm-policy-" + Guid.NewGuid() + ".txt");
    public void Dispose() => File.Delete(path);

    [Fact]
    public async Task ReloadsBetweenAttemptsButPreservesSnapshotAndHash()
    {
        await File.WriteAllTextAsync(path, "Policy A");
        var prompt = new OrganizationPrompt(path, "gpt-6-sol");
        var first = await prompt.BuildAsync(SyntheticDocuments.Document(), SyntheticDocuments.Taxonomy, 0, default);
        await File.WriteAllTextAsync(path, "Policy B");
        var second = await prompt.BuildAsync(SyntheticDocuments.Document(), SyntheticDocuments.Taxonomy, 0, default);
        Assert.Equal("Policy A", first.Instructions);
        Assert.Equal("Policy B", second.Instructions);
        Assert.NotEqual(first.PromptSha256, second.PromptSha256);
        Assert.NotEqual(first.PolicyVersion, second.PolicyVersion);
        Assert.True(second.NamedOutput);
        Assert.Contains("Synthetic merchant", second.Prompt);
        var repeat = await prompt.BuildAsync(SyntheticDocuments.Document(), SyntheticDocuments.Taxonomy, 0, default);
        Assert.Equal(second.PolicyVersion, repeat.PolicyVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("policy\0secret")]
    public async Task InvalidFileFailsWithoutFallback(string text)
    {
        await File.WriteAllTextAsync(path, text);
        await Assert.ThrowsAsync<OrganizationPromptException>(() => new OrganizationPrompt(path, "model").ReadAsync());
    }

    [Fact]
    public async Task MissingOversizedAndNonUtf8FilesFailWithoutLeakingContents()
    {
        var prompt = new OrganizationPrompt(path, "model");
        await Assert.ThrowsAsync<OrganizationPromptException>(() => prompt.ReadAsync());
        await File.WriteAllBytesAsync(path, new byte[OrganizationPrompt.MaxBytes + 1]);
        await Assert.ThrowsAsync<OrganizationPromptException>(() => prompt.ReadAsync());
        await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0xff]);
        var error = await Assert.ThrowsAsync<OrganizationPromptException>(() => prompt.ReadAsync());
        Assert.Equal("organization_prompt_unreadable_or_invalid", error.Message);
    }
}
