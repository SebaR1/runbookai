using System.Text;
using RunbookAI.SystemApi.Documents;

namespace RunbookAI.SystemApi.Tests;

public class DocumentRulesTests
{
    [Theory]
    [InlineData("runbook.md", true)]
    [InlineData("RUNBOOK.PDF", true)]
    [InlineData("notas.txt", true)]
    [InlineData("informe.docx", false)]
    [InlineData("sin-extension", false)]
    public void IsSupported_accepts_the_same_formats_as_the_rag_service(string fileName, bool expected) =>
        Assert.Equal(expected, DocumentRules.IsSupported(fileName));

    [Fact]
    public void BlobName_is_one_blob_per_version_under_the_document_prefix()
    {
        var id = Guid.Parse("01a20b14-33ca-7d17-ba90-27447742c497");
        Assert.Equal("01a20b14-33ca-7d17-ba90-27447742c497/v3.pdf", DocumentRules.BlobName(id, 3, "Manual.PDF"));
        Assert.StartsWith(DocumentRules.BlobPrefix(id), DocumentRules.BlobName(id, 1, "a.md"));
    }

    [Fact]
    public void Sha256_is_lowercase_hex_like_the_rag_docHash()
    {
        // python -c "import hashlib; print(hashlib.sha256(b'hola').hexdigest())"
        Assert.Equal("b221d9dbb083a7f33428d7c2a3c3198ae925614d70210e28716ccaa7cd4ddb79",
            DocumentRules.Sha256(Encoding.UTF8.GetBytes("hola")));
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("equipo-pagos_2", true)]
    [InlineData("-empieza-con-guion", false)]
    [InlineData("con espacio", false)]
    [InlineData("ñandú", false)]
    [InlineData("", false)]
    public void IsValidNamespace(string value, bool expected) =>
        Assert.Equal(expected, DocumentRules.IsValidNamespace(value));

    [Fact]
    public void IsValidNamespace_rejects_more_than_64_characters()
    {
        Assert.True(DocumentRules.IsValidNamespace(new string('a', 64)));
        Assert.False(DocumentRules.IsValidNamespace(new string('a', 65)));
    }

    [Fact]
    public void DefaultTitle_is_the_file_name_without_extension() =>
        Assert.Equal("runbook-pagos", DocumentRules.DefaultTitle("runbook-pagos.md"));
}
