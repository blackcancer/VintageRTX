using System.Reflection;
using System.Reflection.Emit;

namespace VintageRTX.Test;

/// <summary>
/// Creates small interface doubles without introducing a mocking framework into
/// the runtime validation project. Each test supplies only the API calls that
/// belong to the contract it exercises; all other value-type returns receive
/// their CLR default value.
/// </summary>
internal class RuntimeCoverageDispatchProxy : DispatchProxy
{
    /// <summary>Stable prefix for dynamically emitted access-bypass assemblies.</summary>
    private const string AccessBypassAssemblyName = "VintageRTX.Test.AccessBypassProxies";
    /// <summary>Caches one emitted proxy type for each incompatible interface.</summary>
    private static readonly Dictionary<Type, Type> AccessBypassProxyTypes = [];
    /// <summary>Serializes access to dynamic type creation and the proxy-type cache.</summary>
    private static readonly object AccessBypassProxyLock = new();
    private Func<MethodInfo, object?[]?, object?>? handler;

    /// <summary>
    /// Required by <see cref="DispatchProxy"/> when it emits the concrete proxy
    /// type in a dynamic assembly.
    /// </summary>
    public RuntimeCoverageDispatchProxy()
    {
    }

    /// <summary>
    /// Creates requested fixture operation with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="handler">The handler input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The create result consumed by the caller&apos;s assertion.</returns>
    internal static T Create<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        T proxy = HasInaccessibleInterfaceMethod(typeof(T))
            ? (T)CreateAccessBypassProxy(typeof(T))
            : DispatchProxy.Create<T, RuntimeCoverageDispatchProxy>();
        ((RuntimeCoverageDispatchProxy)(object)proxy).handler = handler;
        return proxy;
    }

    /// <summary>
    /// Forwards one method implemented by the access-bypass proxy to the same
    /// deterministic handler used by ordinary <see cref="DispatchProxy"/> instances.
    /// </summary>
    /// <param name="method">Interface method represented by the emitted proxy member.</param>
    /// <param name="arguments">Boxed arguments supplied by the caller.</param>
    /// <returns>The handler result converted by the emitted caller to the declared return type.</returns>
    public object? ForwardInterfaceMethod(MethodInfo method, object?[]? arguments)
    {
        return handler?.Invoke(method, arguments) ?? DefaultValue(method.ReturnType);
    }

    /// <summary>
    /// Detects interfaces that cannot be implemented by the framework proxy
    /// because one of their inherited members is not visible to ProxyBuilder.
    /// Vintage Story 1.22.7 exposes such an internal abstract IPlayer member.
    /// </summary>
    /// <param name="interfaceType">Interface requested by the deterministic fixture.</param>
    /// <returns><see langword="true"/> when an emitted access-bypass proxy is required.</returns>
    private static bool HasInaccessibleInterfaceMethod(Type interfaceType)
    {
        return EnumerateInterfaceMethods(interfaceType).Any(static method => !method.IsPublic);
    }

    /// <summary>
    /// Creates or reuses an emitted proxy whose assembly explicitly ignores
    /// access checks for the interface and deterministic-test assemblies.
    /// </summary>
    /// <param name="interfaceType">Interface implemented by the generated proxy.</param>
    /// <returns>A new proxy instance ready to receive its deterministic handler.</returns>
    private static object CreateAccessBypassProxy(Type interfaceType)
    {
        lock (AccessBypassProxyLock)
        {
            if (!AccessBypassProxyTypes.TryGetValue(interfaceType, out Type? proxyType))
            {
                proxyType = BuildAccessBypassProxyType(interfaceType);
                AccessBypassProxyTypes.Add(interfaceType, proxyType);
            }

            return Activator.CreateInstance(proxyType)
                ?? throw new InvalidOperationException($"Could not create proxy for '{interfaceType.FullName}'.");
        }
    }

    /// <summary>
    /// Emits one concrete proxy type and direct forwarding stubs for every
    /// declared or inherited interface method.
    /// </summary>
    /// <param name="interfaceType">Interface implemented by the generated proxy.</param>
    /// <returns>The completed runtime type.</returns>
    private static Type BuildAccessBypassProxyType(Type interfaceType)
    {
        AssemblyName assemblyName = new($"{AccessBypassAssemblyName}.{interfaceType.Name}");
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Run);
        ApplyAccessBypass(assembly, interfaceType.Assembly.GetName().Name!);
        ApplyAccessBypass(assembly, typeof(RuntimeCoverageDispatchProxy).Assembly.GetName().Name!);

        ModuleBuilder module = assembly.DefineDynamicModule(assemblyName.Name!);
        TypeBuilder type = module.DefineType(
            $"{interfaceType.Name}RuntimeCoverageProxy",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(RuntimeCoverageDispatchProxy),
            [interfaceType]);
        DefineDefaultConstructor(type);

        foreach (MethodInfo method in EnumerateInterfaceMethods(interfaceType))
        {
            DefineForwardingMethod(type, method);
        }

        return type.CreateType()
            ?? throw new TypeLoadException($"Could not complete proxy for '{interfaceType.FullName}'.");
    }

    /// <summary>
    /// Applies the runtime-recognized access-check bypass attribute to a
    /// generated assembly for one referenced assembly name.
    /// </summary>
    /// <param name="assembly">Generated proxy assembly receiving the attribute.</param>
    /// <param name="targetAssemblyName">Assembly whose internal interface members must be callable.</param>
    private static void ApplyAccessBypass(AssemblyBuilder assembly, string targetAssemblyName)
    {
        ConstructorInfo constructor = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute)
            .GetConstructor([typeof(string)])
            ?? throw new MissingMethodException(
                typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).FullName,
                ".ctor(string)");
        assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [targetAssemblyName]));
    }

    /// <summary>
    /// Defines the public parameterless constructor required by fixture code.
    /// </summary>
    /// <param name="type">Proxy type under construction.</param>
    private static void DefineDefaultConstructor(TypeBuilder type)
    {
        ConstructorInfo baseConstructor = typeof(RuntimeCoverageDispatchProxy).GetConstructor(Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(RuntimeCoverageDispatchProxy).FullName, ".ctor()");
        ConstructorBuilder constructor = type.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        ILGenerator il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseConstructor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Defines one interface implementation that boxes arguments, invokes the
    /// shared handler and converts its result to the declared return type.
    /// </summary>
    /// <param name="type">Proxy type under construction.</param>
    /// <param name="interfaceMethod">Interface method implemented by the emitted member.</param>
    private static void DefineForwardingMethod(TypeBuilder type, MethodInfo interfaceMethod)
    {
        ParameterInfo[] parameters = interfaceMethod.GetParameters();
        Type[] parameterTypes = parameters.Select(static parameter => parameter.ParameterType).ToArray();
        MethodAttributes attributes = MethodAttributes.Public
            | MethodAttributes.Virtual
            | MethodAttributes.Final
            | MethodAttributes.HideBySig
            | MethodAttributes.NewSlot;
        if (interfaceMethod.IsSpecialName)
        {
            attributes |= MethodAttributes.SpecialName;
        }

        MethodBuilder method = type.DefineMethod(
            interfaceMethod.Name,
            attributes,
            interfaceMethod.CallingConvention,
            interfaceMethod.ReturnType,
            interfaceMethod.ReturnParameter.GetRequiredCustomModifiers(),
            interfaceMethod.ReturnParameter.GetOptionalCustomModifiers(),
            parameterTypes,
            parameters.Select(static parameter => parameter.GetRequiredCustomModifiers()).ToArray(),
            parameters.Select(static parameter => parameter.GetOptionalCustomModifiers()).ToArray());
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, interfaceMethod);
        il.Emit(OpCodes.Ldtoken, interfaceMethod.DeclaringType!);
        il.Emit(
            OpCodes.Call,
            typeof(MethodBase).GetMethod(
                nameof(MethodBase.GetMethodFromHandle),
                [typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle)])!);
        il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        EmitArgumentsArray(il, parameterTypes);
        il.Emit(
            OpCodes.Call,
            typeof(RuntimeCoverageDispatchProxy).GetMethod(
                nameof(ForwardInterfaceMethod),
                BindingFlags.Instance | BindingFlags.Public)!);
        EmitReturn(il, interfaceMethod.ReturnType);
        type.DefineMethodOverride(method, interfaceMethod);
    }

    /// <summary>
    /// Emits a boxed object array containing all arguments of a forwarding stub.
    /// </summary>
    /// <param name="il">IL stream receiving the array construction.</param>
    /// <param name="parameterTypes">Declared parameter types in source order.</param>
    private static void EmitArgumentsArray(ILGenerator il, Type[] parameterTypes)
    {
        il.Emit(OpCodes.Ldc_I4, parameterTypes.Length);
        il.Emit(OpCodes.Newarr, typeof(object));
        for (int index = 0; index < parameterTypes.Length; index++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldarg, index + 1);
            if (parameterTypes[index].IsValueType)
            {
                il.Emit(OpCodes.Box, parameterTypes[index]);
            }

            il.Emit(OpCodes.Stelem_Ref);
        }
    }

    /// <summary>
    /// Emits conversion of the boxed handler result followed by the method return.
    /// </summary>
    /// <param name="il">IL stream receiving the return conversion.</param>
    /// <param name="returnType">Declared interface return type.</param>
    private static void EmitReturn(ILGenerator il, Type returnType)
    {
        if (returnType == typeof(void))
        {
            il.Emit(OpCodes.Pop);
        }
        else if (returnType.IsValueType)
        {
            il.Emit(OpCodes.Unbox_Any, returnType);
        }
        else
        {
            il.Emit(OpCodes.Castclass, returnType);
        }

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Enumerates each method declared by an interface or any inherited
    /// interface exactly once.
    /// </summary>
    /// <param name="interfaceType">Root interface requested by the fixture.</param>
    /// <returns>Stable method sequence used to emit the proxy.</returns>
    private static IEnumerable<MethodInfo> EnumerateInterfaceMethods(Type interfaceType)
    {
        return new[] { interfaceType }
            .Concat(interfaceType.GetInterfaces())
            .SelectMany(static type => type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Distinct(MethodIdentityComparer.Instance);
    }

    /// <summary>
    /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="targetMethod">The target Method input used to configure this deterministic test path.</param>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>The invoke result consumed by the caller&apos;s assertion.</returns>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return handler?.Invoke(targetMethod, args) ?? DefaultValue(targetMethod.ReturnType);
    }

    /// <summary>
    /// Executes the default Value step used by the deterministic runtime Coverage Dispatch Proxy fixture.
    /// </summary>
    /// <param name="type">The type input used to configure this deterministic test path.</param>
    /// <returns>The default Value result consumed by the caller&apos;s assertion.</returns>
    internal static object? DefaultValue(Type type)
    {
        return type == typeof(void) || !type.IsValueType
            ? null
            : Activator.CreateInstance(type);
    }

    /// <summary>
    /// Compares reflected methods by their defining module and metadata token.
    /// </summary>
    private sealed class MethodIdentityComparer : IEqualityComparer<MethodInfo>
    {
        /// <summary>Gets the singleton comparer used during proxy emission.</summary>
        internal static MethodIdentityComparer Instance { get; } = new();

        /// <summary>
        /// Reports whether two reflected methods identify the same metadata member.
        /// </summary>
        /// <param name="left">First reflected method.</param>
        /// <param name="right">Second reflected method.</param>
        /// <returns><see langword="true"/> when both inputs identify the same method.</returns>
        public bool Equals(MethodInfo? left, MethodInfo? right)
        {
            return ReferenceEquals(left, right)
                || (left is not null
                    && right is not null
                    && left.Module == right.Module
                    && left.MetadataToken == right.MetadataToken);
        }

        /// <summary>
        /// Returns a stable hash code for one reflected method identity.
        /// </summary>
        /// <param name="method">Reflected method to hash.</param>
        /// <returns>Hash code compatible with <see cref="Equals(MethodInfo?, MethodInfo?)"/>.</returns>
        public int GetHashCode(MethodInfo method)
        {
            return HashCode.Combine(method.Module, method.MetadataToken);
        }
    }
}
