using System.Text.Json.Nodes;

[TestFixture]
public class RegistryWriterTests
{
    private static ApiRegistry SampleRegistry() => new(
        "http://localhost:5080",
        new Dictionary<string, Operation>
        {
            ["listUsers"] = new Operation(
                Name: "listUsers",
                Resource: "users",
                Summary: "List users",
                Method: HttpMethod.Get,
                Path: "/users",
                Risk: Risk.Read,
                PathParams: [],
                Pagination: new Pagination("cursor", "limit", 20, "data", "nextCursor", null),
                Body: null),
            ["deleteUser"] = new Operation(
                Name: "deleteUser",
                Resource: "users",
                Summary: "Delete a user",
                Method: HttpMethod.Delete,
                Path: "/users/{id}",
                Risk: Risk.Destructive,
                PathParams: [new PathParam("id", FieldType.String, null, null)],
                Pagination: null,
                Body: null),
        });

    [Test]
    public void Serialize_UsesCamelCasePropertiesAndEnums()
    {
        var json = JsonNode.Parse(RegistryWriter.Serialize(SampleRegistry()))!;
        var op = json["operations"]!["deleteUser"]!;

        Assert.Multiple(() =>
        {
            Assert.That(json["baseUrl"]!.GetValue<string>(), Is.EqualTo("http://localhost:5080"));
            Assert.That(op["risk"]!.GetValue<string>(), Is.EqualTo("destructive"));
            Assert.That(op["pathParams"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("string"));
            Assert.That(op["method"]!["method"]!.GetValue<string>(), Is.EqualTo("DELETE"));
        });
    }

    [Test]
    public void Serialize_OmitsNullProperties()
    {
        var json = JsonNode.Parse(RegistryWriter.Serialize(SampleRegistry()))!;
        var list = json["operations"]!["listUsers"]!.AsObject();
        var delete = json["operations"]!["deleteUser"]!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(list.ContainsKey("pagination"), Is.True);
            Assert.That(list.ContainsKey("body"), Is.False);
            Assert.That(list["pagination"]!.AsObject().ContainsKey("hasMorePath"), Is.False);
            Assert.That(delete.ContainsKey("pagination"), Is.False);
            Assert.That(delete["pathParams"]![0]!.AsObject().ContainsKey("description"), Is.False);
        });
    }

    [Test]
    public void Serialize_IsIndented()
    {
        Assert.That(RegistryWriter.Serialize(SampleRegistry()), Does.Contain("\n  \"baseUrl\""));
    }

    [Test]
    public async Task WriteAsync_WritesSerializedRegistryToFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"registry-{Guid.NewGuid():N}.json");
        try
        {
            var registry = SampleRegistry();
            await RegistryWriter.WriteAsync(registry, path);

            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(RegistryWriter.Serialize(registry)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
