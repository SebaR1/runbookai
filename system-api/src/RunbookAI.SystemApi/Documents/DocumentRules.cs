using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RunbookAI.SystemApi.Documents;

// Pure functions, unit tested without infrastructure.
public static partial class DocumentRules
{
    // Same set the RAG service accepts. Checking here too fails fast (415) without
    // storing anything.
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".txt"] = "text/plain",
        [".pdf"] = "application/pdf",
    };

    public static IReadOnlyCollection<string> SupportedExtensions => ContentTypes.Keys;

    public static bool IsSupported(string fileName) => ContentTypes.ContainsKey(Path.GetExtension(fileName));

    public static string ContentTypeFor(string fileName) =>
        ContentTypes.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");

    // One "folder" per document; each version is its own blob, so older versions
    // stay as history. Deleting the document removes the whole prefix.
    public static string BlobPrefix(Guid documentId) => $"{documentId}/";

    public static string BlobName(Guid documentId, int version, string fileName) =>
        $"{BlobPrefix(documentId)}v{version}{Path.GetExtension(fileName).ToLowerInvariant()}";

    // Lowercase hex, same format as the docHash the RAG service stores in Chroma.
    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static string DefaultTitle(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    // Namespaces end up in Chroma filters and, later, maybe in URLs or index names:
    // keep them boring.
    public static bool IsValidNamespace(string value) => NamespacePattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex NamespacePattern();
}
