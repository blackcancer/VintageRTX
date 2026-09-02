namespace System.Runtime.CompilerServices;

/// <summary>
/// Supplies the runtime-recognized assembly marker used only by dynamically
/// emitted test proxies that must implement an internal interface member.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class IgnoresAccessChecksToAttribute : Attribute
{
    /// <summary>
    /// Initializes an access-check marker for one referenced assembly.
    /// </summary>
    /// <param name="assemblyName">Simple name of the assembly whose internal members are required.</param>
    public IgnoresAccessChecksToAttribute(string assemblyName)
    {
        AssemblyName = assemblyName;
    }

    /// <summary>Gets the simple referenced assembly name.</summary>
    public string AssemblyName { get; }
}
