namespace PaperlessLlm.Auth;

public sealed class AuthException(string message, bool requiresSignIn = false) : Exception(message)
{
    public bool RequiresSignIn { get; } = requiresSignIn;
}
