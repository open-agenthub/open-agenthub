using AgentHub.ArtifactRenderer;
using Xunit;

namespace ArtifactRenderer.Tests;

public sealed class LibreOfficeConverterTests
{
    [Fact]
    public void Command_uses_an_isolated_profile_and_pdf_output_directory()
    {
        var command = LibreOfficeConverter.BuildCommand(
            "/work/in/report.docx", "/work/out", "/work/profile");

        Assert.Equal("soffice", command.FileName);
        Assert.Equal([
            "--headless",
            "-env:UserInstallation=file:///work/profile",
            "--convert-to", "pdf",
            "--outdir", "/work/out",
            "/work/in/report.docx",
        ], command.ArgumentList);
        Assert.False(command.UseShellExecute);
        Assert.True(command.RedirectStandardError);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".pptx")]
    [InlineData(".xlsx")]
    public void Supported_extensions_are_explicit(string extension) =>
        Assert.True(LibreOfficeConverter.IsSupported(extension));

    [Theory]
    [InlineData(".doc")]
    [InlineData(".svg")]
    [InlineData(".html")]
    public void Legacy_or_active_content_formats_are_rejected(string extension) =>
        Assert.False(LibreOfficeConverter.IsSupported(extension));
}
