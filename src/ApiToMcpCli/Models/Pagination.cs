public sealed record Pagination(
    string CursorParam,       // "cursor"
    string LimitParam,        // "limit"
    Decimal DefaultLimit,         // 20, from the limit parameter's schema
    string? ItemsPath,         // "data"
    string NextCursorPath,    // "nextCursor"
    string? HasMorePath);     // "hasNext"; null if the API has no such field