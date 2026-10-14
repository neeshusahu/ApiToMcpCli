# ApiToMcpCli (`mcpgen`)

A .NET command-line tool that reads an **OpenAPI 3 YAML spec** and turns it into a **`registry.json`**. A generic MCP server (ApiToMcpServer) loads that file and uses it to call the API on behalf of an AI agent.

You don't write a separate MCP tool for each endpoint. The CLI describes each API operation once as data: its path, method, parameters, body, pagination and risk level. The server then exposes a single generic tool that can call any operation in the registry by its `operationId`.

```
openapi.yaml ──► mcpgen generate ──► registry.json ──► ApiToMcpServer ──► HTTP API
  (spec)          (this project)       (data)          (generic MCP tool)
```

---

## Requirements

- .NET 9 SDK
- NuGet packages (already referenced in `ApiToMcpCli.csproj`):

| Package | Purpose |
|---|---|
| `Microsoft.OpenApi` (3.x) | OpenAPI object model, document loading, `$ref` resolution |
| `Microsoft.OpenApi.YamlReader` (3.x) | Adds YAML parsing to the reader |
| `System.CommandLine` | CLI parsing (`generate` command, `--output` option) |
| `Microsoft.Extensions.DependencyInjection` | Registers the shared `OpenApiReaderSettings` |

## Usage

```bash
cd src/ApiToMcpCli
dotnet run -- generate openapi.yaml -o registry.json
# Wrote 8 operations to registry.json
```

| Argument / option | Description | Default |
|---|---|---|
| `inputPath` | Path to the OpenAPI YAML file | required |
| `-o`, `--output` | Where to write the registry | `registry.json` |

Exit code `0` means success. On any error (file not found, spec parse errors, missing `operationId`, and so on), the CLI prints the message to stderr and exits with `1`.

To use the result, point the MCP server at the file through its configuration key `ApiRegistry:Path`.

---

## How extraction works with Microsoft.OpenApi

The pipeline has three stages: **load → extract → write**.

### 1. Load: parse YAML into an `OpenApiDocument`

The core `Microsoft.OpenApi` package only reads JSON. YAML support comes from a separate package, which is plugged into the reader settings. `ServiceCollectionExtension.cs` registers those settings once:

```csharp
var readerSettings = new OpenApiReaderSettings();
readerSettings.AddYamlReader();          // from Microsoft.OpenApi.YamlReader
```

`Program.cs` then loads the file:

```csharp
await using var stream = File.OpenRead(inputPath);
var (doc, diagnostic) = await OpenApiDocument.LoadAsync(stream, settings: settings);
```

`LoadAsync` returns two things:

- **`OpenApiDocument`**: a strongly typed object graph (`Servers`, `Paths`, `Components`, and so on).
- **`OpenApiDiagnostic`**: parse and validation errors. If `diagnostic.Errors` contains anything, the CLI stops and prints every error instead of producing a partial registry.

During loading, the library **resolves `$ref`s**. A reference such as `$ref: '#/components/schemas/UserList'` becomes a reference object that forwards to the target schema. Code can therefore read `schema.Properties` directly without following references by hand. Pagination detection and request-body extraction both depend on this.

### 2. Extract: walk the document (`Extractor.cs`)

`Extractor.Extract(OpenApiDocument)` builds an `ApiRegistry`:

```
document.Servers[0].Url          → baseUrl
document.Paths                   → for each path ("/users/{id}")
  .Operations                    →   for each (HttpMethod, OpenApiOperation)
     OperationId                 →     registry key + name
     Summary                     →     summary
     Parameters (in: path)       →     pathParams
     RequestBody (POST/PUT)      →     body
     Parameters + Responses(GET) →     pagination
     HttpMethod                  →     risk
```

In Microsoft.OpenApi 2.x and later, `OpenApiPathItem.Operations` is keyed by `System.Net.Http.HttpMethod`. The extractor therefore compares against `HttpMethod.Get`, `HttpMethod.Post` and so on.

These rules are applied for each part of an operation:

**Base URL**
- Taken from the first entry in `servers`. If the spec has no `servers` entry, extraction fails.

**Operation identity**
- `operationId` is **required** on every operation, because it becomes the dictionary key and the name the MCP tool is called with.
- A missing or duplicate `operationId` throws an error.
- `resource` is the first path segment (`/users/{id}` → `users`) and is used for display only.

**Path parameters** (`ExtractParameter`)
- Only parameters with `in: path` are kept. For each one the extractor records the name, the type, the `description` and the schema `pattern`.
- Query and header parameters are not emitted as `pathParams`. The pagination check reads query parameters separately (see below).

**Request body** (`ExtractRequestBody`, POST and PUT only)
- Looks at `requestBody.content["application/json"].schema.properties`. Because `$ref`s are already resolved, `UserInput` expands to its `name` and `email` fields.
- Only top-level properties are emitted, each as a name and a type.

**Pagination** (`DetectPagination`, GET only). Cursor pagination is inferred from the request and the response together:
1. **Request:** a query parameter whose name contains `cursor` becomes `cursorParam`. If a query parameter named `limit` exists, its schema `default` becomes `defaultLimit`. Otherwise the default is `20`.
2. **Response:** the `200 → application/json → schema` properties are scanned:
   - the first **array** property becomes `itemsPath`
   - the first **string** property whose name contains `cursor` becomes `nextCursorPath`
   - the first **boolean** property whose name starts with `has` becomes `hasMorePath` (optional)
3. If either `itemsPath` or `nextCursorPath` is missing, the operation is treated as not paginated.

**Types** (`ToFieldType`)
- `JsonSchemaType` is a flags enum. In OpenAPI 3.0, `nullable: true` loads as `String | Null`. The extractor strips the `Null` flag before mapping, so a nullable string still counts as `string`.
- A missing type defaults to `string`. An unsupported type throws an error.

**Risk** (`DetectRisk`)

| HTTP method | `risk` |
|---|---|
| GET | `read` |
| POST / PUT / PATCH | `write` |
| DELETE | `destructive` |
| anything else | `destructive` (the strictest level, used when the method is unknown) |

The MCP server can use `risk` to decide whether a call needs human approval.

### 3. Write: serialize to JSON (`RegistryWriter`)

`RegistryWriter` serializes the `ApiRegistry` record with `System.Text.Json`. The output uses camelCase property names, indented formatting and camelCase enum strings (`"read"`, `"string"`), and omits `null` properties. As a result, `pagination` and `body` appear only on operations that have them.

---

## Example

### Input: `openapi.yaml` (excerpt)

```yaml
openapi: 3.0.3
info: { title: User Management API, version: 1.0.0 }
servers:
  - url: http://localhost:5080

paths:
  /users:
    get:
      operationId: listUsers
      summary: List users. Use nextCursor to get the next page.
      parameters:
        - { name: cursor, in: query, schema: { type: string } }
        - { name: limit,  in: query, schema: { type: integer, default: 20 } }
      responses:
        '200':
          description: A page of users
          content:
            application/json:
              schema: { $ref: '#/components/schemas/UserList' }
    post:
      operationId: createUser
      summary: Create a user
      requestBody:
        content:
          application/json:
            schema: { $ref: '#/components/schemas/UserInput' }
      responses:
        '201': { description: Created }

  /users/{id}:
    delete:
      operationId: deleteUser
      summary: Delete a user. Cannot be undone.
      parameters:
        - { name: id, in: path, required: true, schema: { type: string } }
      responses:
        '204': { description: Deleted }

components:
  schemas:
    UserInput:
      type: object
      properties:
        name:  { type: string }
        email: { type: string }
    UserList:
      type: object
      properties:
        data:       { type: array, items: { $ref: '#/components/schemas/User' } }
        nextCursor: { type: string, nullable: true }
        hasNext:    { type: boolean }
```

### Output: `registry.json`

```json
{
  "baseUrl": "http://localhost:5080",
  "operations": {
    "listUsers": {
      "name": "listUsers",
      "resource": "users",
      "summary": "List users. Use nextCursor to get the next page.",
      "method": { "method": "GET" },
      "path": "/users",
      "risk": "read",
      "pathParams": [],
      "pagination": {
        "cursorParam": "cursor",
        "limitParam": "limit",
        "defaultLimit": 20,
        "itemsPath": "data",
        "nextCursorPath": "nextCursor",
        "hasMorePath": "hasNext"
      }
    },
    "createUser": {
      "name": "createUser",
      "resource": "users",
      "summary": "Create a user",
      "method": { "method": "POST" },
      "path": "/users",
      "risk": "write",
      "pathParams": [],
      "body": {
        "fields": [
          { "name": "name",  "type": "string" },
          { "name": "email", "type": "string" }
        ]
      }
    },
    "deleteUser": {
      "name": "deleteUser",
      "resource": "users",
      "summary": "Delete a user. Cannot be undone.",
      "method": { "method": "DELETE" },
      "path": "/users/{id}",
      "risk": "destructive",
      "pathParams": [
        { "name": "id", "type": "string" }
      ]
    }
  }
}
```

`method` is written as `{ "method": "GET" }` because the model stores a `System.Net.Http.HttpMethod` object and `System.Text.Json` serializes its `Method` property. The server reads it back as `operation.Method.Method`.

### Registry field reference

| Field | Description |
|---|---|
| `baseUrl` | `servers[0].url`. The MCP server uses it as the `HttpClient` base address. |
| `operations` | Dictionary keyed by `operationId` |
| `name` | The `operationId`. The MCP tool is called with this value. |
| `resource` | First path segment. Display only. |
| `summary` | The operation's `summary`, which tells the model what the operation does |
| `method` | HTTP method |
| `path` | Path template with `{param}` placeholders |
| `risk` | `read`, `write` or `destructive` |
| `pathParams[]` | `name`, `type`, plus optional `description` and `pattern` |
| `pagination` | Present only on cursor-paginated GET operations |
| `body.fields[]` | Top-level JSON body fields (`name`, `type`). Present only on POST and PUT. |

---

## Running the tests

The NUnit tests are in `src/ApiToMcpCli.Tests`. Run them from the repository root:

```bash
dotnet test
```

## Project layout

```
ApiToMcpCli.sln
README.md
.gitignore / .gitattributes
src/
├── ApiToMcpCli.Tests/                 # NUnit tests for Extractor and RegistryWriter
└── ApiToMcpCli/
    ├── Program.cs                     # CLI entry: `generate` command, load + diagnostics
    ├── ServiceCollectionExtension.cs  # Registers OpenApiReaderSettings with the YAML reader
    ├── Extractor.cs                   # OpenApiDocument → ApiRegistry
    ├── RegistryWriter/
    │   ├── RegistryWriter.cs          # Serialize + write file
    │   └── RegistryJson.cs            # JSON options (camelCase, enums as strings, skip nulls)
    ├── Models/                        # ApiRegistry, Operation, PathParam, Pagination, RequestBody
    └── openapi.yaml                   # Sample spec (User Management API)
```
