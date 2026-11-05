using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal sealed class PublicSdkSurface
{
    private readonly MetadataReader _reader;
    private readonly PublicSdkSignatures _signatures;
    private PublicSdkSurface(MetadataReader reader) { _reader = reader; _signatures = new(reader); }

    internal static string Read(string path)
    {
        using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(input);
    }

    internal static string Read(Stream input)
    {
        if (input.Length is < 1 or > 33554432) throw new IOException("sdk_api_assembly_size_invalid");
        using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        if (reader.GetString(assembly.Name) != "NexusPipeline.Extensibility" || reader.TypeDefinitions.Count > 4096)
            throw new IOException("sdk_api_assembly_unbound");
        if (reader.ExportedTypes.Count != 0) throw new IOException("sdk_api_type_forwarding_unreviewed");
        var model = new PublicSdkSurface(reader);
        var declarations = reader.TypeDefinitions.Where(model.Visible).Select(model.Describe)
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var text = JsonSerializer.Serialize(new { format = "nexus.sdk.dotnet-public-api", version = 1,
            assembly = reader.GetString(assembly.Name) + ",Version=" + assembly.Version + ",Culture=" + reader.GetString(assembly.Culture) + ",PublicKey=" +
                (assembly.PublicKey.IsNil ? "" : Convert.ToHexStringLower(reader.GetBlobBytes(assembly.PublicKey))),
            declarations });
        if (text.Length > 4194304) throw new IOException("sdk_api_surface_limit");
        return text + "\n";
    }

    internal static void Verify(string assembly, string descriptor)
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(descriptor));
        using var actual = JsonDocument.Parse(Read(assembly));
        if (!JsonElement.DeepEquals(expected.RootElement, actual.RootElement))
            throw new IOException("sdk_public_api_descriptor_changed");
    }

    private bool Visible(TypeDefinitionHandle handle)
    {
        var value = _reader.GetTypeDefinition(handle);
        var access = value.Attributes & TypeAttributes.VisibilityMask;
        return access == TypeAttributes.Public || access is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or
            TypeAttributes.NestedFamORAssem && Visible(value.GetDeclaringType());
    }

    private static bool Visible(MethodAttributes value) =>
        (value & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;
    private static bool Visible(FieldAttributes value) =>
        (value & FieldAttributes.FieldAccessMask) is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;

    private TypeSurface Describe(TypeDefinitionHandle handle)
    {
        var value = _reader.GetTypeDefinition(handle);
        var name = _signatures.Type(handle);
        var methods = value.GetMethods().Where(item => Visible(_reader.GetMethodDefinition(item).Attributes)).ToHashSet();
        var members = new List<string>();
        var layoutRelevant = (value.Attributes & TypeAttributes.LayoutMask) is TypeAttributes.SequentialLayout or TypeAttributes.ExplicitLayout;
        var sequential = (value.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.SequentialLayout;
        var fieldIndex = 0;
        foreach (var fieldHandle in value.GetFields())
        {
            var field = _reader.GetFieldDefinition(fieldHandle);
            var instanceField = (field.Attributes & FieldAttributes.Static) == 0;
            var index = instanceField ? fieldIndex++ : -1;
            var publicField = Visible(field.Attributes);
            if (!publicField && (!layoutRelevant || (field.Attributes & FieldAttributes.Static) != 0)) continue;
            var fieldName = publicField ? _reader.GetString(field.Name) : "<layout-storage>";
            var constant = name == "NexusPipeline.Extensibility.SdkIdentity" && fieldName == "ContractFingerprint"
                ? "<public-compatibility-identity>" : Constant(field.GetDefaultValue());
            members.Add(JsonSerializer.Serialize(new { kind = "field", name = fieldName, attributes = (int)field.Attributes,
                signature = field.DecodeSignature(_signatures, null), constant, offset = field.GetOffset(),
                layoutIndex = sequential && instanceField ? index : -1,
                marshal = Blob(field.GetMarshallingDescriptor()), custom = Attributes(field.GetCustomAttributes()) }));
        }
        foreach (var methodHandle in methods)
        {
            var method = _reader.GetMethodDefinition(methodHandle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) throw new IOException("sdk_api_native_import_unreviewed");
            members.Add(JsonSerializer.Serialize(new { kind = "method", name = _reader.GetString(method.Name),
                attributes = (int)method.Attributes, impl = (int)(method.ImplAttributes &
                    (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.PreserveSig)),
                signature = PublicSdkSignatures.Method(method.DecodeSignature(_signatures, null)),
                generic = Generics(method.GetGenericParameters()), custom = Attributes(method.GetCustomAttributes()),
                parameters = method.GetParameters().Select(parameterHandle =>
                {
                    var parameter = _reader.GetParameter(parameterHandle);
                    return new { index = parameter.SequenceNumber, name = _reader.GetString(parameter.Name),
                        attributes = (int)parameter.Attributes, constant = Constant(parameter.GetDefaultValue()),
                        marshal = Blob(parameter.GetMarshallingDescriptor()), custom = Attributes(parameter.GetCustomAttributes()) };
                }).OrderBy(item => item.index).ToArray() }));
        }
        foreach (var propertyHandle in value.GetProperties())
        {
            var property = _reader.GetPropertyDefinition(propertyHandle); var accessors = property.GetAccessors();
            if (!methods.Contains(accessors.Getter) && !methods.Contains(accessors.Setter)) continue;
            members.Add(JsonSerializer.Serialize(new { kind = "property", name = _reader.GetString(property.Name),
                attributes = (int)property.Attributes, signature = PublicSdkSignatures.Method(property.DecodeSignature(_signatures, null)),
                getter = Accessor(accessors.Getter, methods), setter = Accessor(accessors.Setter, methods),
                constant = Constant(property.GetDefaultValue()), custom = Attributes(property.GetCustomAttributes()) }));
        }
        foreach (var eventHandle in value.GetEvents())
        {
            var item = _reader.GetEventDefinition(eventHandle); var accessors = item.GetAccessors();
            if (!methods.Contains(accessors.Adder) && !methods.Contains(accessors.Remover)) continue;
            members.Add(JsonSerializer.Serialize(new { kind = "event", name = _reader.GetString(item.Name),
                attributes = (int)item.Attributes, type = _signatures.Type(item.Type),
                add = Accessor(accessors.Adder, methods), remove = Accessor(accessors.Remover, methods),
                raise = Accessor(accessors.Raiser, methods), custom = Attributes(item.GetCustomAttributes()) }));
        }
        if (members.Count > 65536) throw new IOException("sdk_api_member_limit");
        var layout = value.GetLayout();
        return new(name, (int)value.Attributes, _signatures.Type(value.BaseType), layout.Size, layout.PackingSize,
            value.GetInterfaceImplementations().Select(item =>
            {
                var contract = _reader.GetInterfaceImplementation(item);
                return JsonSerializer.Serialize(new { type = _signatures.Type(contract.Interface), custom = Attributes(contract.GetCustomAttributes()) });
            })
                .Order(StringComparer.Ordinal).ToArray(),
            Generics(value.GetGenericParameters()), Attributes(value.GetCustomAttributes()), members.Order(StringComparer.Ordinal).ToArray());
    }

    private string Accessor(MethodDefinitionHandle handle, HashSet<MethodDefinitionHandle> visible) => !visible.Contains(handle) ? "" :
        _reader.GetString(_reader.GetMethodDefinition(handle).Name) + ":" +
            PublicSdkSignatures.Method(_reader.GetMethodDefinition(handle).DecodeSignature(_signatures, null));
    private string[] Generics(GenericParameterHandleCollection handles) => handles.Select(handle =>
    {
        var value = _reader.GetGenericParameter(handle);
        return JsonSerializer.Serialize(new { index = value.Index, name = _reader.GetString(value.Name), attributes = (int)value.Attributes,
            constraints = value.GetConstraints().Select(item =>
            {
                var constraint = _reader.GetGenericParameterConstraint(item);
                return JsonSerializer.Serialize(new { type = _signatures.Type(constraint.Type), custom = Attributes(constraint.GetCustomAttributes()) });
            })
                .Order(StringComparer.Ordinal).ToArray(), custom = Attributes(value.GetCustomAttributes()) });
    }).Order(StringComparer.Ordinal).ToArray();
    private string Constant(ConstantHandle handle)
    {
        if (handle.IsNil) return "";
        var value = _reader.GetConstant(handle);
        return value.TypeCode + ":" + Blob(value.Value);
    }
    private string Blob(BlobHandle handle) => handle.IsNil ? "" : Convert.ToHexStringLower(_reader.GetBlobBytes(handle));
    private string[] Attributes(CustomAttributeHandleCollection handles) => handles.Select(handle =>
    {
        var value = _reader.GetCustomAttribute(handle);
        string type, signature;
        if (value.Constructor.Kind == HandleKind.MemberReference)
        {
            var constructor = _reader.GetMemberReference((MemberReferenceHandle)value.Constructor);
            type = _signatures.Type(constructor.Parent);
            signature = PublicSdkSignatures.Method(constructor.DecodeMethodSignature(_signatures, null));
        }
        else if (value.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var constructor = _reader.GetMethodDefinition((MethodDefinitionHandle)value.Constructor);
            type = _signatures.Type(constructor.GetDeclaringType());
            signature = PublicSdkSignatures.Method(constructor.DecodeSignature(_signatures, null));
        }
        else throw new BadImageFormatException("sdk_api_attribute_constructor_invalid");
        if (type.EndsWith("System.Runtime.CompilerServices.CompilerGeneratedAttribute", StringComparison.Ordinal) ||
            type.EndsWith("System.Diagnostics.DebuggerBrowsableAttribute", StringComparison.Ordinal) ||
            type.EndsWith("System.Diagnostics.DebuggerStepThroughAttribute", StringComparison.Ordinal) ||
            type.EndsWith("System.CodeDom.Compiler.GeneratedCodeAttribute", StringComparison.Ordinal))
            return null;
        return type + ":" + signature + ":" + Blob(value.Value);
    }).Where(value => value is not null).Select(value => value!).Order(StringComparer.Ordinal).ToArray();

    private sealed record TypeSurface(string Name, int Attributes, string BaseType, int Size, int Packing,
        string[] Interfaces, string[] Generic, string[] Custom, string[] Members);
}
