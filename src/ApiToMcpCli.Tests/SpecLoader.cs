using System.Text;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

// Loads an OpenAPI YAML string the same way Program.cs loads a file
internal static class SpecLoader
{
    private static readonly OpenApiReaderSettings Settings = CreateSettings();

    private static OpenApiReaderSettings CreateSettings()
    {
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        return settings;
    }

    public static async Task<OpenApiDocument> LoadAsync(string yaml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
        var (doc, diagnostic) = await OpenApiDocument.LoadAsync(stream, settings: Settings);

        Assert.That(diagnostic?.Errors, Is.Empty, "Spec failed to parse");
        Assert.That(doc, Is.Not.Null);
        return doc!;
    }

    public static async Task<OpenApiDocument> LoadFileAsync(string path)
    {
        return await LoadAsync(await File.ReadAllTextAsync(path));
    }

    // Wraps a `paths:` block (and optional `components:` block) in a minimal valid spec
    public static Task<OpenApiDocument> LoadPathsAsync(string paths, string components = "")
    {
        var yaml = $"""
            openapi: 3.0.3
            info: {"{"} title: Test, version: 1.0.0 {"}"}
            servers:
              - url: http://localhost:5080
            paths:
            {Indent(paths)}
            {(components.Length > 0 ? "components:\n" + Indent(components) : "")}
            """;
        return LoadAsync(yaml);
    }

    private static string Indent(string block) =>
        string.Join("\n", block.Split('\n').Select(l => "  " + l));
}
