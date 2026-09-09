using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Koan.Core.Hosting.Bootstrap;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 — boot-time fail-fast validation of every <c>[Access]</c> declaration. Scans the populated
/// <see cref="AssemblyCache"/> at registrar time (after the bootstrapper's assembly-closure pass), compiles each
/// <c>[Access]</c>-bearing entity, and AGGREGATES every malformed declaration into ONE
/// <see cref="AccessGateException"/> so a developer sees all bad gates in a single boot rather than one-at-a-time
/// on first request. A typo can therefore never reach production as a silently-open or silently-denied gate.
/// </summary>
internal static class AccessGateRegistrar
{
    public static void Validate()
    {
        var failures = new List<string>();
        foreach (var type in DiscoverAccessEntities())
        {
            try
            {
                AccessGateCache.Compile(type);
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    AccessGateCache.Compile(property);
            }
            catch (AccessGateException ex)
            {
                failures.Add(ex.Message);
            }
        }

        if (failures.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append(failures.Count).AppendLine(" malformed [Access] declaration(s) found at boot:");
        foreach (var f in failures) sb.Append("  • ").AppendLine(f);
        throw new AccessGateException(sb.ToString());
    }

    private static IEnumerable<Type> DiscoverAccessEntities()
    {
        foreach (var assembly in AssemblyCache.Instance.GetAllAssemblies())
        {
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type is null || type.IsGenericTypeDefinition) continue;
                if (type.GetCustomAttribute<AccessAttribute>(inherit: true) is not null
                    || type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Any(property => property.GetCustomAttribute<AccessAttribute>(inherit: true) is not null))
                    yield return type;
            }
        }
    }
}
