using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That a placed image reaches a browser only when its leading bytes are the format its name says (SPEC.md §9.5).
/// </summary>
[Unit]
public sealed class ImageSignatureTests
{
    public static TheoryData<string, byte[]> Images => new()
    {
        { "a.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0] },
        { "a.JPG", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0] },
        { "a.jpeg", [0xFF, 0xD8, 0xFF, 0xDB] },
        { "a.gif", "GIF89a\0\0"u8.ToArray() },
        { "a.gif", "GIF87a"u8.ToArray() },
        { "a.webp", "RIFF\0\0\0\0WEBPVP8 "u8.ToArray() },
    };

    [Theory]
    [MemberData(nameof(Images))]
    public void Bytes_that_begin_as_the_named_format_match(string name, byte[] bytes) =>
        Assert.True(ImageSignature.Matches(name, bytes));

    [Theory]
    [InlineData("a.png", "<html><script>alert(1)</script>")]
    [InlineData("a.jpg", "GIF89a")]
    [InlineData("a.gif", "<svg onload=alert(1)>")]
    [InlineData("a.webp", "RIFF\0\0\0\0AVI LIST")]
    [InlineData("a.webp", "RIFF")]
    [InlineData("a.png", "")]
    public void Bytes_of_another_kind_under_an_image_name_do_not(string name, string text) =>
        Assert.False(ImageSignature.Matches(name, System.Text.Encoding.Latin1.GetBytes(text)));

    [Theory]
    [InlineData("a.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData("a.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A })]
    [InlineData("a.jpg", new byte[] { 0xFF, 0xD8 })]
    [InlineData("a.jpeg", new byte[] { 0xFF, 0xD8, 0x00 })]
    [InlineData("a.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39 })]
    public void Only_the_start_of_the_signature_does_not_match(string name, byte[] bytes)
    {
        // Review, quality lens (0.62.9): shortening the PNG signature to four bytes and the JPEG's to two survived, so a file
        // beginning only as part of the signature was served as the image its name said.
        Assert.False(ImageSignature.Matches(name, bytes));
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData("a.svg")]
    [InlineData("png")]
    [InlineData("a.png.txt")]
    public void A_name_that_is_not_an_images_is_never_one_whatever_its_bytes(string name)
    {
        Assert.False(ImageSignature.IsImageName(name));
        Assert.Null(ImageSignature.ContentType(name));
        Assert.False(ImageSignature.Matches(name, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("A.JPEG", "image/jpeg")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    public void The_type_comes_from_the_extension(string name, string type) =>
        Assert.Equal(type, ImageSignature.ContentType(name));
}
