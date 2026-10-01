using PaperlessLlm.Paperless;

namespace PaperlessLlm.Organizer;

public static class SetupValidation
{
    public static void RequireReviewTag(PaperlessTaxonomy taxonomy, string name)
    {
        var matches = taxonomy.Tags.Count(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (matches == 1) return;
        throw new PaperlessException(matches == 0
            ? "Configured review tag is not visible to this Paperless token. Check PPLLM_TAG, the instance URL, and the service account's global and object-level view permissions on the existing tag. Do not create a duplicate."
            : "Multiple visible tags match PPLLM_TAG. Configure an unambiguous existing review tag.",
            matches == 0 ? "review_tag_not_visible" : "review_tag_ambiguous");
    }
}
