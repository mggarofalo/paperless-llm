using System.Buffers.Binary;
using System.IO.Compression;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Tests;

public sealed class SetupTests
{
    [Fact]
    public void ProbePngHasValidChunkChecksumsAndDecodablePixels()
    {
        var png = Convert.FromBase64String(OrganizerCli.SyntheticImage.Split(',')[1]);
        Assert.Equal(new byte[] {137,80,78,71,13,10,26,10}, png[..8]);
        int offset = 8, chunks = 0;
        using var compressed = new MemoryStream();
        while (offset < png.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var typeAndData = png.AsSpan(offset + 4, length + 4);
            uint crc = uint.MaxValue;
            foreach (var value in typeAndData)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
            }
            Assert.Equal(~crc, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)));
            if (typeAndData[..4].SequenceEqual("IDAT"u8)) compressed.Write(typeAndData[4..]);
            offset += length + 12; chunks++;
        }
        Assert.Equal(3, chunks);
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var pixels = new MemoryStream(); zlib.CopyTo(pixels);
        Assert.Equal(new byte[] { 1, 255, 255 }, pixels.ToArray());
    }

    [Fact]
    public void MissingAndAmbiguousTagHaveDifferentActionableErrors()
    {
        var hidden = Assert.Throws<PaperlessException>(() => SetupValidation.RequireReviewTag(new([], [], []), "needs review"));
        Assert.Equal("review_tag_not_visible", hidden.Code);
        Assert.Contains("object-level", hidden.Message);
        var duplicate = Assert.Throws<PaperlessException>(() => SetupValidation.RequireReviewTag(
            new([new(1, "needs review"), new(2, "NEEDS REVIEW")], [], []), "needs review"));
        Assert.Equal("review_tag_ambiguous", duplicate.Code);
    }
}
