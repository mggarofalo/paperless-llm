using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace PaperlessLlm.Auth;

public sealed class AuthException(string message, bool requiresSignIn = false) : Exception(message)
{
    public bool RequiresSignIn { get; } = requiresSignIn;
}

public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed record AuthStatus(bool SignedIn, bool PlanUsageGranted, DateTimeOffset? ExpiresAt);
public sealed record LogoutResult(bool LocalCredentialsCleared, bool RemoteRevocationConfirmed);

// Never log or serialize this type to a user-facing response.
internal sealed class Credentials
{
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public string? ClientId { get; set; }
    public string? Subject { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? EarliestRefreshAt { get; set; }
}

public sealed class TokenStore
{
    private readonly string directory;
    private readonly string path;

    public TokenStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        path = Path.Combine(this.directory, "chatgpt.json");
    }

    internal async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var options = PrivateOptions(FileMode.OpenOrCreate);
                options.Share = FileShare.None;
                return new FileStream(Path.Combine(directory, ".auth.lock"), options);
            }
            catch (IOException)
            {
                if (attempt == 99) break;
                await Task.Delay(100, cancellationToken);
            }
        }
        throw new AuthException("Authentication storage is busy. Retry after the current operation finishes.");
    }

    internal async Task<Credentials> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return new Credentials();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new AuthException("Credential storage must not be a symbolic link.");
            var data = await File.ReadAllBytesAsync(path, cancellationToken);
            if (data.Length > 128 * 1024) throw new AuthException("Credential storage is invalid.");
            var saved = JsonSerializer.Deserialize<Credentials>(data);
            if (saved is null || string.IsNullOrWhiteSpace(saved.HostId) || saved.Scopes is null
                || saved.ClientId == "dynamic_agent_client") throw new AuthException("Credential storage is invalid.");
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AuthException("Credential storage could not be read.");
        }
    }

    internal async Task WriteAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(directory, ".auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporary, PrivateOptions(FileMode.CreateNew)))
            {
                await JsonSerializer.SerializeAsync(stream, credentials, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuthException("Credential storage could not be saved.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void EnsurePrivateDirectory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new AuthException("Credential directory must not be a symbolic link.");
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                var owner = WindowsIdentity.GetCurrent().User
                    ?? throw new AuthException("Cannot determine the credential storage owner.");
                security.SetOwner(owner);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new AuthException("Credential directory must not be a symbolic link.");
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuthException("Private credential storage could not be prepared.");
        }
    }

    private static FileStreamOptions PrivateOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
