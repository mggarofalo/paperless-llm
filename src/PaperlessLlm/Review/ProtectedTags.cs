using PaperlessLlm.Paperless;

namespace PaperlessLlm.Review;

public sealed class ProposalValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class ProtectedTags
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "needs review", "inbox", "receipt to log", "hsa reimbursed", "hsa unreimbursed", "expense", "sweetgum", "wallingford" };

    public static bool IsProtected(NamedEntity tag) => tag.IsInboxTag || Protected.Contains(string.Join(' ', tag.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

}
