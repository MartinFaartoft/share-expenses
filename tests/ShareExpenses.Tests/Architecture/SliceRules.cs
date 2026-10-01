using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ShareExpenses.Tests.Architecture;

/// <summary>Where the slices, the shared code and the slice catalog live in an assembly.</summary>
internal sealed record SliceLayout(string SlicesNamespace, string SharedNamespace, string CatalogType);

/// <summary>
/// The slice boundary rules from spec §12, checked over compiled IL with Mono.Cecil.
/// Each rule returns its violations as readable strings; an empty list is a pass.
/// </summary>
internal static class SliceRules
{
    private const string StoreOptions = "Marten.StoreOptions";
    private const string EndpointRouteBuilder = "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder";

    private static readonly string[] FrameworkNamespaces =
        ["Microsoft.AspNetCore", "Marten", "JasperFx", "Microsoft.EntityFrameworkCore", "Npgsql"];

    public static ModuleDefinition Load(System.Reflection.Assembly assembly) =>
        ModuleDefinition.ReadModule(assembly.Location);

    /// <summary>Public types in a slice are its sealed-record events and its <c>&lt;Name&gt;Slice</c> entry point.</summary>
    public static IReadOnlyList<string> PublicSurface(ModuleDefinition module, SliceLayout layout) =>
        module.GetTypes()
            .Where(t => IsEffectivelyPublic(t) && SliceOf(t, layout) is not null)
            .Where(t => !IsSealedRecord(t) && !IsEntryPoint(t, SliceOf(t, layout)!))
            .Select(t => $"{SliceOf(t, layout)}: {t.FullName} is public, but is neither a sealed record event " +
                         $"nor the {SliceOf(t, layout)}Slice entry point")
            .ToList();

    /// <summary>A slice may use another slice's public types (its events), never its internals.</summary>
    public static IReadOnlyList<string> CrossSliceDependencies(ModuleDefinition module, SliceLayout layout) =>
        module.GetTypes()
            .Select(t => (type: t, slice: SliceOf(t, layout)))
            .Where(x => x.slice is not null)
            .SelectMany(x => ReferencedTypes(x.type)
                .Where(r => r.Module == module)
                .Select(r => r.Resolve())
                .OfType<TypeDefinition>()
                .Where(r => SliceOf(r, layout) is { } other && other != x.slice && !IsEffectivelyPublic(r))
                .Select(r => $"{x.slice}: {x.type.FullName} depends on {r.FullName}, which is internal to {SliceOf(r, layout)}"))
            .Distinct()
            .ToList();

    /// <summary>Shared code is pure: no slices, no ASP.NET, no persistence.</summary>
    public static IReadOnlyList<string> SharedIsPure(ModuleDefinition module, SliceLayout layout) =>
        module.GetTypes()
            .Where(t => IsIn(Namespace(t), layout.SharedNamespace))
            .SelectMany(t => ReferencedTypes(t)
                .Select(Namespace)
                .Where(ns => IsIn(ns, layout.SlicesNamespace) || FrameworkNamespaces.Any(f => IsIn(ns, f)))
                .Select(ns => $"Shared: {t.FullName} depends on {ns}"))
            .Distinct()
            .ToList();

    /// <summary>Every slice has an entry point, and the catalog calls both of its methods.</summary>
    public static IReadOnlyList<string> EntryPointsWired(ModuleDefinition module, SliceLayout layout)
    {
        var catalog = module.GetType(layout.CatalogType)
            ?? throw new InvalidOperationException($"Catalog {layout.CatalogType} not found");
        var calledFrom = new Dictionary<string, HashSet<string>>
        {
            ["Register"] = CallsIn(catalog, "Register"),
            ["Map"] = CallsIn(catalog, "Map"),
        };

        var violations = new List<string>();
        foreach (var slice in module.GetTypes().Select(t => SliceOf(t, layout)).OfType<string>().Distinct().Order())
        {
            var entry = module.GetType($"{layout.SlicesNamespace}.{slice}.{slice}Slice");
            if (entry is null || !IsEntryPoint(entry, slice))
            {
                violations.Add($"{slice}: no public static {slice}Slice with Register(StoreOptions) and Map(IEndpointRouteBuilder)");
                continue;
            }

            foreach (var (method, calls) in calledFrom)
                if (!calls.Contains($"{entry.FullName}::{method}"))
                    violations.Add($"{slice}: {catalog.Name}.{method} does not call {entry.Name}.{method}");
        }
        return violations;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>The slice a type belongs to: the namespace segment directly under the slices root.</summary>
    private static string? SliceOf(TypeReference type, SliceLayout layout)
    {
        var ns = Namespace(type);
        if (!ns.StartsWith(layout.SlicesNamespace + ".", StringComparison.Ordinal)) return null;
        return ns[(layout.SlicesNamespace.Length + 1)..].Split('.')[0];
    }

    /// <summary>Nested types have no namespace of their own in Cecil; use the outermost declaring type's.</summary>
    private static string Namespace(TypeReference type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type.Namespace;
    }

    private static bool IsIn(string ns, string root) =>
        ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    private static bool IsEffectivelyPublic(TypeDefinition type) =>
        type.IsPublic || (type.IsNestedPublic && IsEffectivelyPublic(type.DeclaringType));

    private static bool IsSealedRecord(TypeDefinition type) =>
        type.IsClass && type.IsSealed && type.Methods.Any(m => m.Name == "<Clone>$");

    private static bool IsEntryPoint(TypeDefinition type, string slice) =>
        type.Name == $"{slice}Slice" && type.IsPublic && type.IsAbstract && type.IsSealed
        && HasStaticMethod(type, "Register", StoreOptions)
        && HasStaticMethod(type, "Map", EndpointRouteBuilder);

    private static bool HasStaticMethod(TypeDefinition type, string name, string parameterType) =>
        type.Methods.Any(m => m.Name == name && m.IsStatic && m.IsPublic
                              && m.Parameters is [var p] && p.ParameterType.FullName == parameterType);

    private static HashSet<string> CallsIn(TypeDefinition type, string method) =>
        type.Methods.Where(m => m.Name == method && m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (MethodReference)i.Operand)
            .Select(m => $"{m.DeclaringType.FullName}::{m.Name}")
            .ToHashSet();

    /// <summary>Every type a type mentions: signatures, members, attributes and method bodies.</summary>
    private static IEnumerable<TypeReference> ReferencedTypes(TypeDefinition type)
    {
        var refs = new List<TypeReference>();
        if (type.BaseType is not null) refs.Add(type.BaseType);
        refs.AddRange(type.Interfaces.Select(i => i.InterfaceType));
        refs.AddRange(type.CustomAttributes.Select(a => a.AttributeType));
        refs.AddRange(type.Fields.Select(f => f.FieldType));
        refs.AddRange(type.Properties.Select(p => p.PropertyType));

        foreach (var method in type.Methods)
        {
            refs.Add(method.ReturnType);
            refs.AddRange(method.Parameters.Select(p => p.ParameterType));
            refs.AddRange(method.CustomAttributes.Select(a => a.AttributeType));
            if (!method.HasBody) continue;

            refs.AddRange(method.Body.Variables.Select(v => v.VariableType));
            foreach (var operand in method.Body.Instructions.Select(i => i.Operand))
            {
                switch (operand)
                {
                    case TypeReference t:
                        refs.Add(t);
                        break;
                    case MethodReference m:
                        refs.Add(m.DeclaringType);
                        refs.Add(m.ReturnType);
                        refs.AddRange(m.Parameters.Select(p => p.ParameterType));
                        if (m is GenericInstanceMethod g) refs.AddRange(g.GenericArguments);
                        break;
                    case FieldReference f:
                        refs.Add(f.DeclaringType);
                        refs.Add(f.FieldType);
                        break;
                }
            }
        }

        return refs.SelectMany(Flatten);
    }

    /// <summary>Unwraps arrays, by-refs, pointers and generic instantiations to the types inside.</summary>
    private static IEnumerable<TypeReference> Flatten(TypeReference type)
    {
        switch (type)
        {
            case GenericInstanceType g:
                foreach (var t in Flatten(g.ElementType)) yield return t;
                foreach (var t in g.GenericArguments.SelectMany(Flatten)) yield return t;
                break;
            case TypeSpecification s:
                foreach (var t in Flatten(s.ElementType)) yield return t;
                break;
            case GenericParameter:
                break;
            default:
                yield return type;
                break;
        }
    }
}
