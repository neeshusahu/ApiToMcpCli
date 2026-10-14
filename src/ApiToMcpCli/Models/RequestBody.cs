public sealed record RequestBody(List<BodyField> Fields);


public sealed record BodyField(
    string Name,              // "email"
    FieldType Type
    
);

public enum PropType
{
    String,
    Array,
    Boolean,
    Int

}