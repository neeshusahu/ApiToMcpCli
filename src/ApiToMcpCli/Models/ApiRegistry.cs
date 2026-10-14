public sealed record ApiRegistry(
    string BaseUrl,
    Dictionary<string, Operation> Operations);   // keyed by operationId