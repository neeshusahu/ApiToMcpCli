using Microsoft.OpenApi;

[TestFixture]
public class ExtractorTests
{
    // ---------- document level ----------

    [Test]
    public async Task Extract_UsesFirstServerAsBaseUrl()
    {
        var doc = await SpecLoader.LoadAsync("""
            openapi: 3.0.3
            info: { title: Test, version: 1.0.0 }
            servers:
              - url: https://api.example.com
              - url: https://backup.example.com
            paths: {}
            """);

        var registry = Extractor.Extract(doc);

        Assert.That(registry.BaseUrl, Is.EqualTo("https://api.example.com"));
        Assert.That(registry.Operations, Is.Empty);
    }

    [Test]
    public async Task Extract_NoServers_Throws()
    {
        var doc = await SpecLoader.LoadAsync("""
            openapi: 3.0.3
            info: { title: Test, version: 1.0.0 }
            paths: {}
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Extractor.Extract(doc));
        Assert.That(ex!.Message, Does.Contain("no servers"));
    }

    // ---------- operation identity ----------

    [Test]
    public async Task Extract_MissingOperationId_Throws()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /users:
              get:
                responses: { '200': { description: ok } }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Extractor.Extract(doc));
        Assert.That(ex!.Message, Does.Contain("/users").And.Contain("operationId"));
    }

    [Test]
    public async Task Extract_DuplicateOperationId_Throws()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /users:
              get:
                operationId: getThing
                responses: { '200': { description: ok } }
            /orgs:
              get:
                operationId: getThing
                responses: { '200': { description: ok } }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Extractor.Extract(doc));
        Assert.That(ex!.Message, Does.Contain("Duplicate operationId: getThing"));
    }

    [Test]
    public async Task Extract_PopulatesBasicOperationFields()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /users/{id}/roles:
              get:
                operationId: getUserRoles
                summary: Get roles for a user
                parameters:
                  - { name: id, in: path, required: true, schema: { type: string } }
                responses: { '200': { description: ok } }
            """);

        var op = Extractor.Extract(doc).Operations["getUserRoles"];

        Assert.Multiple(() =>
        {
            Assert.That(op.Name, Is.EqualTo("getUserRoles"));
            Assert.That(op.Resource, Is.EqualTo("users"));
            Assert.That(op.Summary, Is.EqualTo("Get roles for a user"));
            Assert.That(op.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(op.Path, Is.EqualTo("/users/{id}/roles"));
        });
    }

    [Test]
    public async Task Extract_MissingSummary_DefaultsToEmpty()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /users:
              get:
                operationId: listUsers
                responses: { '200': { description: ok } }
            """);

        Assert.That(Extractor.Extract(doc).Operations["listUsers"].Summary, Is.Empty);
    }

    // ---------- risk ----------

    [TestCase("get", Risk.Read)]
    [TestCase("post", Risk.Write)]
    [TestCase("put", Risk.Write)]
    [TestCase("patch", Risk.Write)]
    [TestCase("delete", Risk.Destructive)]
    public async Task Extract_MapsHttpMethodToRisk(string method, Risk expected)
    {
        var doc = await SpecLoader.LoadPathsAsync($$"""
            /users:
              {{method}}:
                operationId: op
                responses: { '200': { description: ok } }
            """);

        Assert.That(Extractor.Extract(doc).Operations["op"].Risk, Is.EqualTo(expected));
    }

    // ---------- path parameters ----------

    [Test]
    public async Task Extract_PathParams_KeepsOnlyPathLocation()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /users/{id}:
              get:
                operationId: getUser
                parameters:
                  - name: id
                    in: path
                    required: true
                    description: The user id
                    schema: { type: string, pattern: '^u_[a-z0-9]+$' }
                  - { name: expand, in: query, schema: { type: string } }
                  - { name: X-Trace, in: header, schema: { type: string } }
                responses: { '200': { description: ok } }
            """);

        var pathParams = Extractor.Extract(doc).Operations["getUser"].PathParams;

        Assert.That(pathParams, Has.Count.EqualTo(1));
        Assert.That(pathParams[0], Is.EqualTo(
            new PathParam("id", FieldType.String, "The user id", "^u_[a-z0-9]+$")));
    }

    [TestCase("string", FieldType.String)]
    [TestCase("integer", FieldType.Integer)]
    [TestCase("number", FieldType.Number)]
    [TestCase("boolean", FieldType.Boolean)]
    public async Task Extract_PathParams_MapsSchemaType(string schemaType, FieldType expected)
    {
        var doc = await SpecLoader.LoadPathsAsync($$"""
            /items/{key}:
              get:
                operationId: getItem
                parameters:
                  - { name: key, in: path, required: true, schema: { type: {{schemaType}} } }
                responses: { '200': { description: ok } }
            """);

        Assert.That(Extractor.Extract(doc).Operations["getItem"].PathParams[0].Type, Is.EqualTo(expected));
    }

    [Test]
    public async Task Extract_PathParams_MissingSchemaType_DefaultsToString()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items/{key}:
              get:
                operationId: getItem
                parameters:
                  - { name: key, in: path, required: true, schema: {} }
                responses: { '200': { description: ok } }
            """);

        Assert.That(Extractor.Extract(doc).Operations["getItem"].PathParams[0].Type, Is.EqualTo(FieldType.String));
    }

    [Test]
    public async Task Extract_NoParameters_GivesEmptyPathParams()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /users:
              get:
                operationId: listUsers
                responses: { '200': { description: ok } }
            """);

        Assert.That(Extractor.Extract(doc).Operations["listUsers"].PathParams, Is.Empty);
    }

    // ---------- request body ----------

    private const string UserInputComponents = """
        schemas:
          UserInput:
            type: object
            properties:
              name: { type: string }
              age: { type: integer }
              active: { type: boolean }
              tags: { type: array, items: { type: string } }
              address: { type: object, properties: { city: { type: string } } }
        """;

    [TestCase("post")]
    [TestCase("put")]
    public async Task Extract_RequestBody_ResolvesRefAndMapsTopLevelFields(string method)
    {
        var doc = await SpecLoader.LoadPathsAsync($$"""
            /users:
              {{method}}:
                operationId: saveUser
                requestBody:
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/UserInput' }
                responses: { '200': { description: ok } }
            """, UserInputComponents);

        var body = Extractor.Extract(doc).Operations["saveUser"].Body;

        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Fields, Is.EqualTo(new[]
        {
            new BodyField("name", FieldType.String),
            new BodyField("age", FieldType.Integer),
            new BodyField("active", FieldType.Boolean),
            new BodyField("tags", FieldType.Array),
            new BodyField("address", FieldType.Object),
        }));
    }

    [Test]
    public async Task Extract_RequestBody_IgnoredForPatch()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /users:
              patch:
                operationId: patchUser
                requestBody:
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/UserInput' }
                responses: { '200': { description: ok } }
            """, UserInputComponents);

        Assert.That(Extractor.Extract(doc).Operations["patchUser"].Body, Is.Null);
    }

    [Test]
    public async Task Extract_RequestBody_NonJsonContent_GivesEmptyFields()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /upload:
              post:
                operationId: upload
                requestBody:
                  content:
                    text/plain:
                      schema: { type: string }
                responses: { '200': { description: ok } }
            """);

        var body = Extractor.Extract(doc).Operations["upload"].Body;

        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Fields, Is.Empty);
    }

    [Test]
    public async Task Extract_PostWithoutRequestBody_HasNullBody()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /jobs/run:
              post:
                operationId: runJob
                responses: { '202': { description: accepted } }
            """);

        Assert.That(Extractor.Extract(doc).Operations["runJob"].Body, Is.Null);
    }

    // ---------- pagination ----------

    private const string PagedListComponents = """
        schemas:
          Item:
            type: object
            properties:
              id: { type: string }
          ItemList:
            type: object
            properties:
              data: { type: array, items: { $ref: '#/components/schemas/Item' } }
              nextCursor: { type: string, nullable: true }
              hasNext: { type: boolean }
        """;

    [Test]
    public async Task Extract_Pagination_DetectedFromCursorParamAndResponseShape()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items:
              get:
                operationId: listItems
                parameters:
                  - { name: cursor, in: query, schema: { type: string } }
                  - { name: limit, in: query, schema: { type: integer, default: 50 } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema: { $ref: '#/components/schemas/ItemList' }
            """, PagedListComponents);

        var pagination = Extractor.Extract(doc).Operations["listItems"].Pagination;

        Assert.That(pagination, Is.EqualTo(new Pagination(
            CursorParam: "cursor",
            LimitParam: "limit",
            DefaultLimit: 50,
            ItemsPath: "data",
            NextCursorPath: "nextCursor",
            HasMorePath: "hasNext")));
    }

    [Test]
    public async Task Extract_Pagination_NoLimitParam_DefaultsTo20()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items:
              get:
                operationId: listItems
                parameters:
                  - { name: pageCursor, in: query, schema: { type: string } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema: { $ref: '#/components/schemas/ItemList' }
            """, PagedListComponents);

        var pagination = Extractor.Extract(doc).Operations["listItems"].Pagination;

        Assert.That(pagination, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(pagination!.CursorParam, Is.EqualTo("pageCursor"));
            Assert.That(pagination.LimitParam, Is.EqualTo("limit"));
            Assert.That(pagination.DefaultLimit, Is.EqualTo(20m));
        });
    }

    [Test]
    public async Task Extract_Pagination_HasMoreIsOptional()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items:
              get:
                operationId: listItems
                parameters:
                  - { name: cursor, in: query, schema: { type: string } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema:
                          type: object
                          properties:
                            items: { type: array, items: { type: string } }
                            next_cursor: { type: string }
            """);

        var pagination = Extractor.Extract(doc).Operations["listItems"].Pagination;

        Assert.That(pagination, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(pagination!.ItemsPath, Is.EqualTo("items"));
            Assert.That(pagination.NextCursorPath, Is.EqualTo("next_cursor"));
            Assert.That(pagination.HasMorePath, Is.Null);
        });
    }

    [Test]
    public async Task Extract_Pagination_NullWithoutCursorQueryParam()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items:
              get:
                operationId: listItems
                parameters:
                  - { name: limit, in: query, schema: { type: integer } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema: { $ref: '#/components/schemas/ItemList' }
            """, PagedListComponents);

        Assert.That(Extractor.Extract(doc).Operations["listItems"].Pagination, Is.Null);
    }

    [Test]
    public async Task Extract_Pagination_NullWhenResponseHasNoItemsArray()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items:
              get:
                operationId: listItems
                parameters:
                  - { name: cursor, in: query, schema: { type: string } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema:
                          type: object
                          properties:
                            nextCursor: { type: string }
            """);

        Assert.That(Extractor.Extract(doc).Operations["listItems"].Pagination, Is.Null);
    }

    [Test]
    public async Task Extract_Pagination_NullWhenResponseHasNoNextCursor()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items:
              get:
                operationId: listItems
                parameters:
                  - { name: cursor, in: query, schema: { type: string } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema:
                          type: object
                          properties:
                            data: { type: array, items: { type: string } }
            """);

        Assert.That(Extractor.Extract(doc).Operations["listItems"].Pagination, Is.Null);
    }

    [Test]
    public async Task Extract_Pagination_OnlyDetectedForGet()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items/search:
              post:
                operationId: searchItems
                parameters:
                  - { name: cursor, in: query, schema: { type: string } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema: { $ref: '#/components/schemas/ItemList' }
            """, PagedListComponents);

        Assert.That(Extractor.Extract(doc).Operations["searchItems"].Pagination, Is.Null);
    }

    // ---------- multiple operations on one path ----------

    [Test]
    public async Task Extract_OperationsOnSamePath_DoNotShareBodyOrPagination()
    {
        var doc = await SpecLoader.LoadPathsAsync("""
            /items:
              get:
                operationId: listItems
                parameters:
                  - { name: cursor, in: query, schema: { type: string } }
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema: { $ref: '#/components/schemas/ItemList' }
              post:
                operationId: createItem
                requestBody:
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Item' }
                responses: { '201': { description: created } }
            """, PagedListComponents);

        var ops = Extractor.Extract(doc).Operations;

        Assert.Multiple(() =>
        {
            Assert.That(ops["listItems"].Pagination, Is.Not.Null);
            Assert.That(ops["listItems"].Body, Is.Null);
            Assert.That(ops["createItem"].Body, Is.Not.Null);
            Assert.That(ops["createItem"].Pagination, Is.Null);
        });
    }

    // ---------- sample spec ----------

    [Test]
    public async Task Extract_SampleSpec_ProducesExpectedOperations()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "openapi.yaml");
        var doc = await SpecLoader.LoadFileAsync(path);

        var registry = Extractor.Extract(doc);

        Assert.That(registry.BaseUrl, Is.EqualTo("http://localhost:5080"));
        Assert.That(registry.Operations.Keys, Is.EquivalentTo(new[]
        {
            "listUsers", "createUser", "getUser", "updateUser", "deleteUser",
            "listOrganizations", "getOrganization", "deleteOrganization",
        }));
        Assert.Multiple(() =>
        {
            Assert.That(registry.Operations["listUsers"].Pagination?.HasMorePath, Is.EqualTo("hasNext"));
            Assert.That(registry.Operations["updateUser"].Body?.Fields.Select(f => f.Name),
                Is.EqualTo(new[] { "name", "email" }));
            Assert.That(registry.Operations["deleteOrganization"].Risk, Is.EqualTo(Risk.Destructive));
        });
    }
}
