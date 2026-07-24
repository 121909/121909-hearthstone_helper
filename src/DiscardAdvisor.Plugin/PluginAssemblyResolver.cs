using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DiscardAdvisor.Plugin;

internal static class PluginAssemblyResolver
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> PrivateAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DiscardAdvisor.Domain",
        "DiscardAdvisor.Rules",
        "DiscardAdvisor.Search",
        "System.Buffers",
        "System.Collections.Immutable",
        "System.Memory",
        "System.Numerics.Vectors",
        "System.Runtime.CompilerServices.Unsafe"
    };

    private static bool _registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
                return;
            AppDomain.CurrentDomain.AssemblyResolve += ResolvePrivateAssembly;
            _registered = true;
        }
    }

    internal static Assembly? ResolvePrivateAssembly(object? sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name);
        if (string.IsNullOrWhiteSpace(requested.Name) || !PrivateAssemblyNames.Contains(requested.Name))
            return null;

        var directory = Path.GetDirectoryName(typeof(PluginAssemblyResolver).Assembly.Location);
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        var path = Path.Combine(directory, requested.Name + ".dll");
        if (!File.Exists(path))
            return null;

        try
        {
            var candidate = AssemblyName.GetAssemblyName(path);
            if (!string.Equals(candidate.Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                return null;
            return Assembly.LoadFrom(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }
}
