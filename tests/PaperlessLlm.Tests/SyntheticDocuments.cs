using PaperlessLlm.Paperless;
namespace PaperlessLlm.Tests;
internal static class SyntheticDocuments
{
    internal static PaperlessDocument Document(int id = 1, string revision = "revision-1") =>
        new(id, "Synthetic refund", "<script>private OCR</script> TOTAL -15.99", "2026-01-01", "2026-01-02", null, null, [2, 23], "application/pdf", "private.pdf", revision);
    internal static PaperlessTaxonomy Taxonomy => new([new(2, "needs review", true), new(23, "HSA reimbursed"), new(12, "receipts")], [new(1, "Synthetic merchant")], [new(1, "Receipt")]);
}
