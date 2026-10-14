

using System.Text.Json;

public static class RegistryWriter
{
    public static string Serialize(ApiRegistry registry) =>
        JsonSerializer.Serialize(registry, RegistryJson.Options);

    public static Task WriteAsync(ApiRegistry registry, string path) =>
        File.WriteAllTextAsync(path, Serialize(registry));
}