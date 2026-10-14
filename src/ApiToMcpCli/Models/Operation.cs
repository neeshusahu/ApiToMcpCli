public sealed record Operation(
    string Name,              // operationId, e.g. "deleteUser"
    string? Resource,          // first path segment, e.g. "users" (display only)
    string Summary,           // becomes part of the tool description
    HttpMethod Method,            // "GET", "POST", "PUT", "DELETE"
    string Path,              // "/users/{id}"
    Risk Risk,
    List<PathParam> PathParams,  // ["id"]
    Pagination? Pagination,   // only when Kind == List
    RequestBody? Body);       // only for POST/PUT; unused in this build);          // false for Write in this build

public sealed record PathParam(
    string Name,          // "id"
    FieldType Type,       // String
    string? Description,  // shown to the model
    string? Pattern);     // optional regex from the spec
public enum Risk { Read, Write, Destructive }
 
public enum FieldType { String, Integer, Number, Boolean, Array, Object }