using System.Collections.Immutable;
using System.Reflection.Metadata;

internal sealed class PublicSdkSignatures(MetadataReader reader) : ISignatureTypeProvider<string, object?>
{
    internal string Type(EntityHandle handle) => handle.Kind switch
    {
        _ when handle.IsNil => "",
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
        _ => throw new BadImageFormatException("sdk_api_type_handle_invalid")
    };

    internal static string Method(MethodSignature<string> value) =>
        value.Header.RawValue + ":" + value.GenericParameterCount + ":" + value.RequiredParameterCount + ":" +
        value.ReturnType + "(" + string.Join(",", value.ParameterTypes) + ")";

    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + shape.Rank + ";" + string.Join(",", shape.Sizes) + ";" + string.Join(",", shape.LowerBounds) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr:" + Method(signature);
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        (isRequired ? "modreq(" : "modopt(") + modifier + ")" + unmodifiedType;
    public string GetPinnedType(string elementType) => "pinned:" + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var value = metadata.GetTypeDefinition(handle);
        return value.GetDeclaringType().IsNil ? Join(metadata.GetString(value.Namespace), metadata.GetString(value.Name)) :
            GetTypeFromDefinition(metadata, value.GetDeclaringType(), rawTypeKind) + "+" + metadata.GetString(value.Name);
    }
    public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var value = metadata.GetTypeReference(handle);
        if (value.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(metadata, (TypeReferenceHandle)value.ResolutionScope, rawTypeKind) + "+" + metadata.GetString(value.Name);
        var assembly = value.ResolutionScope.Kind == HandleKind.AssemblyReference ?
            metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)value.ResolutionScope).Name) : "";
        return "[" + assembly + "]" + Join(metadata.GetString(value.Namespace), metadata.GetString(value.Name));
    }
    public string GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    private static string Join(string space, string name) => space.Length == 0 ? name : space + "." + name;
}
