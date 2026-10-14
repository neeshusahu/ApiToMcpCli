using Microsoft.OpenApi;

public static class Extractor
{
    public static ApiRegistry Extract(OpenApiDocument document)
    {
         var ops = ExtractOperations(document.Paths);
         var baseUrl = document.Servers?.FirstOrDefault()?.Url
             ?? throw new InvalidOperationException("Spec has no servers entry");
         return new ApiRegistry(baseUrl, ops);
    }
    private static Dictionary<string, Operation> ExtractOperations(OpenApiPaths paths)
    {
        Dictionary<string, Operation> ops=new Dictionary<string, Operation>();
        foreach(var key in paths.Keys)
        {
            var operations= paths[key].Operations;
            if(operations is null)
                continue;

            foreach(var operation in operations)
            {
               // Declared per operation so values don't leak between GET/POST on the same path
               RequestBody? requestBody=null;
               Pagination? pagination=null;

               var id = operation.Value.OperationId
                   ?? throw new InvalidOperationException($"{operation.Key.Method} {key} has no operationId");

               if(ops.ContainsKey(id))
                 throw new InvalidOperationException($"Duplicate operationId: {id}");
               
                if(operation.Key==HttpMethod.Post || operation.Key==HttpMethod.Put)
                {
                    requestBody=ExtractRequestBody(operation.Value.RequestBody);
                }
                if(operation.Key==HttpMethod.Get)
                {
                    pagination=DetectPagination(operation.Value);
                }
               var parameters=ExtractParameter(operation.Value.Parameters);

               ops.Add(id,
               new Operation(
                   Name: id,
                   Resource: ResourceOf(key),
                   Summary: operation.Value.Summary ?? "",
                   Method: operation.Key,
                   Path: key,
                   Risk: DetectRisk(operation.Key),
                   PathParams: parameters,
                   Pagination: pagination,
                   Body: requestBody
                  
               ));
            }
           
        }
        return ops;
    }

    private static List<PathParam> ExtractParameter (IList<IOpenApiParameter>? parameters)
    {
       List<PathParam> pathParams=new List<PathParam>();
      
       foreach(var param in parameters ?? [])
        {
            if(param.In==ParameterLocation.Path)
            {
 
              pathParams.Add(new PathParam(param.Name!, ToFieldType(param.Schema?.Type), param.Description, param.Schema?.Pattern));
                
            }
            
        }
        return pathParams;
       
    }
    private static RequestBody? ExtractRequestBody(IOpenApiRequestBody? requestBody)
    {
       if(requestBody?.Content is null)
           return null;

       List<BodyField> bodyFields  =new List<BodyField>();
       if(requestBody.Content.TryGetValue("application/json", out var content) && content.Schema?.Properties is not null)
        {
            foreach(var prop in content.Schema.Properties)
            {
                bodyFields.Add(new BodyField(
                    prop.Key,
                    ToFieldType(prop.Value.Type)));
                   
            }
        }
        return new RequestBody(bodyFields);

    }

   
     private  static Pagination? DetectPagination(OpenApiOperation op)
{
    // 1. Request side: needs a cursor query parameter
    var cursorParam = op.Parameters?
        .FirstOrDefault(p => p.In == ParameterLocation.Query &&
                             p.Name!.Contains("cursor", StringComparison.OrdinalIgnoreCase));
    if (cursorParam is null) return null;

    var limitParam = op.Parameters!
        .FirstOrDefault(p => p.In == ParameterLocation.Query && p.Name == "limit");

    // 2. Response side: 200 -> application/json -> schema -> properties
    if (op.Responses?.TryGetValue("200", out var ok) != true) return null;
    if (ok!.Content?.TryGetValue("application/json", out var media) != true) return null;

    var props = media!.Schema?.Properties;       // $ref to UserList is already resolved
    if (props is null) return null;

    string? itemsPath = null, nextCursorPath = null, hasMorePath = null;

    foreach (var (name, schema) in props)
    {
        // Strip Null so a nullable string (String | Null) still matches String
        var type = schema.Type is { } t ? t & ~JsonSchemaType.Null : (JsonSchemaType?)null;

        if (type == JsonSchemaType.Array)
            itemsPath ??= name;
        else if (type == JsonSchemaType.String && name.Contains("cursor", StringComparison.OrdinalIgnoreCase))
            nextCursorPath ??= name;
        else if (type == JsonSchemaType.Boolean && name.StartsWith("has", StringComparison.OrdinalIgnoreCase))
            hasMorePath ??= name;
    }

    // Needs both an items array and a next cursor; hasMore is optional
    if (itemsPath is null || nextCursorPath is null) return null;

    return new Pagination(
        CursorParam: cursorParam.Name!,
        LimitParam: limitParam?.Name ?? "limit",
        DefaultLimit: limitParam?.Schema?.Default?.GetValue<Decimal>() ?? 20,
        ItemsPath: itemsPath,
        NextCursorPath: nextCursorPath,
        HasMorePath: hasMorePath);
}
 
    private static Risk DetectRisk(HttpMethod httpMethod)
    {
        if(httpMethod==HttpMethod.Get)
        return Risk.Read;
        if(httpMethod==HttpMethod.Post || httpMethod==HttpMethod.Put || httpMethod==HttpMethod.Patch)
        return Risk.Write;
        if(httpMethod==HttpMethod.Delete)
        return Risk.Destructive;

        // Fail safe: unknown methods get the strictest tier
        return Risk.Destructive;
    }
    private static string ResourceOf(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    private static FieldType ToFieldType(JsonSchemaType? type)
{
    if (type is null) return FieldType.String;          // OpenAPI default when no type is given

    var t = type.Value & ~JsonSchemaType.Null;           // strip Null; handle nullability separately

    return t switch
    {
        JsonSchemaType.String  => FieldType.String,
        JsonSchemaType.Integer => FieldType.Integer,
        JsonSchemaType.Number  => FieldType.Number,
        JsonSchemaType.Boolean => FieldType.Boolean,
        JsonSchemaType.Array   => FieldType.Array,
        JsonSchemaType.Object  => FieldType.Object,
        _ => throw new NotSupportedException($"Unsupported schema type: {type}")
    };
}

private static bool IsNullable(JsonSchemaType? type) =>
    type?.HasFlag(JsonSchemaType.Null) == true;
    }