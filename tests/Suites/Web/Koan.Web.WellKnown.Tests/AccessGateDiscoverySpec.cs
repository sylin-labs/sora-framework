using System.Reflection;
using System.Globalization;
using AwesomeAssertions;
using Koan.Web.Authorization;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

public sealed class AccessGateDiscoverySpec
{
    [Fact]
    public void Unrelated_assembly_metadata_is_not_loaded_for_gate_discovery()
    {
        var unrelated = new DeclaredAssembly([]) { ThrowOnTypeRead = true };
        Action validate = () => AccessGateRegistrar.Validate([unrelated]);

        validate.Should().NotThrow();
        unrelated.TypeReads.Should().Be(0);
    }

    [Theory]
    [InlineData(false, "invalid-gate")]
    [InlineData(true, "cannot declare owner")]
    public void Referencing_assembly_still_rejects_open_generic_declarations(bool property, string expectedMessage)
    {
        var declaration = new DeclaredType(property);
        declaration.IsGenericTypeDefinition.Should().BeTrue();
        var application = Referencing(declaration);
        Action validate = () => AccessGateRegistrar.Validate([application]);

        validate.Should().Throw<AccessGateException>().WithMessage("*" + expectedMessage + "*");
        application.TypeReads.Should().Be(1);
    }

    [Fact]
    public void Inherited_gate_is_validated_once_at_its_declaring_type()
    {
        var declaring = new DeclaredType(property: true);
        var application = Referencing(declaring, new DerivedType(declaring));
        Action validate = () => AccessGateRegistrar.Validate([application]);

        validate.Should().Throw<AccessGateException>()
            .WithMessage("1 malformed [Access] declaration(s) found at boot:*MetadataDeclaration*cannot declare owner*");
    }

    [Fact]
    public void Reports_both_malformed_class_and_property_declarations_in_one_failure()
    {
        var application = Referencing(new DeclaredType(property: true, classGate: true));
        Action validate = () => AccessGateRegistrar.Validate([application]);

        validate.Should().Throw<AccessGateException>()
            .WithMessage("2 malformed [Access] declaration(s) found at boot:*invalid-gate*cannot declare owner*");
    }

    [Fact]
    public void Valid_class_and_nested_property_declarations_remain_available()
    {
        var application = Referencing(typeof(ValidDeclaration));
        Action validate = () => AccessGateRegistrar.Validate([application]);

        validate.Should().NotThrow();
        application.TypeReads.Should().Be(1);
        AccessGateCache.Compile(typeof(ValidDeclaration).GetProperty(nameof(ValidDeclaration.Secret))!)
            .ByAction.Should().ContainKey(EntityAuthorizeActions.Read);
    }

    private static DeclaredAssembly Referencing(params Type[] types)
        => new([typeof(AccessAttribute).Assembly.GetName()], types);

    private sealed class DeclaredAssembly(AssemblyName[] references, params Type[] types) : Assembly
    {
        public int TypeReads { get; private set; }
        public bool ThrowOnTypeRead { get; init; }
        public override AssemblyName[] GetReferencedAssemblies() => references;
        public override Type[] GetTypes()
        {
            TypeReads++;
            if (ThrowOnTypeRead) throw new TypeLoadException("Unrelated assembly metadata must not be loaded.");
            return types;
        }
    }

    // These declarations exist only in supplied reflection metadata. Malformed real attributes would
    // poison every AddKoan host that discovers this test assembly, including unrelated test classes.
    private sealed class DeclaredType(bool property, bool classGate = false) : TypeDelegator(typeof(MetadataDeclaration<>))
    {
        private readonly AccessAttribute _access = new(read: "invalid-gate");
        public override bool IsGenericTypeDefinition => typeImpl.IsGenericTypeDefinition;
        public override object[] GetCustomAttributes(bool inherit) => property && !classGate ? Array.Empty<Attribute>() : new Attribute[] { _access };
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
            => (!property || classGate) && attributeType == typeof(AccessAttribute) ? new AccessAttribute[] { _access } : (object[])Array.CreateInstance(attributeType, 0);
        public override PropertyInfo[] GetProperties(BindingFlags bindingAttr)
            => property ? [new DeclaredProperty(this)] : [];
    }

    private sealed class DerivedType(DeclaredType declaring) : TypeDelegator(typeof(MetadataDerived<>))
    {
        public override Type BaseType => declaring;
        public override object[] GetCustomAttributes(bool inherit) => Array.Empty<Attribute>();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => (object[])Array.CreateInstance(attributeType, 0);
        public override PropertyInfo[] GetProperties(BindingFlags bindingAttr)
            => bindingAttr.HasFlag(BindingFlags.DeclaredOnly) ? [] : declaring.GetProperties(bindingAttr);
    }

    private sealed class DeclaredProperty(Type declaring) : PropertyInfo
    {
        private readonly PropertyInfo _property = typeof(MetadataDeclaration<>).GetProperty(nameof(MetadataDeclaration<int>.Secret))!;
        private readonly AccessAttribute _access = new(read: "owner");
        public override Type DeclaringType => declaring;
        public override Type ReflectedType => declaring;
        public override string Name => _property.Name;
        public override Type PropertyType => _property.PropertyType;
        public override PropertyAttributes Attributes => _property.Attributes;
        public override bool CanRead => _property.CanRead;
        public override bool CanWrite => _property.CanWrite;
        public override MethodInfo[] GetAccessors(bool nonPublic) => _property.GetAccessors(nonPublic);
        public override MethodInfo? GetGetMethod(bool nonPublic) => _property.GetGetMethod(nonPublic);
        public override MethodInfo? GetSetMethod(bool nonPublic) => _property.GetSetMethod(nonPublic);
        public override ParameterInfo[] GetIndexParameters() => _property.GetIndexParameters();
        public override object[] GetCustomAttributes(bool inherit) => new Attribute[] { _access };
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
            => attributeType == typeof(AccessAttribute) ? new AccessAttribute[] { _access } : (object[])Array.CreateInstance(attributeType, 0);
        public override bool IsDefined(Type attributeType, bool inherit) => attributeType == typeof(AccessAttribute);
        public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
            => throw new NotSupportedException("Gate discovery must not read a property value.");
        public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
            => throw new NotSupportedException("Gate discovery must not write a property value.");
    }

    private class MetadataDeclaration<T>
    {
        public virtual string Secret { get; set; } = "";
    }

    private sealed class MetadataDerived<T> : MetadataDeclaration<T>;

    [Access(read: "anyone")]
    private sealed class ValidDeclaration
    {
        [Access(read: "is:admin")]
        public string Secret { get; set; } = "";
    }
}
