using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ShareExpenses.Tests.Architecture;

/// <summary>Where the slices, the shared code and the slice catalog live in an assembly.</summary>
internal sealed record SliceLayout(string SlicesNamespace, string SharedNamespace, string CatalogType);

/// <summary>What a public type in a slice is for (spec §12).</summary>
internal enum PublicRole
{
    /// <summary>A sealed record the slice owns and emits: the only coupling between slices.</summary>
    Event,

    /// <summary><c>&lt;Name&gt;Slice</c>: what the slice contributes to the store.</summary>
    EntryPoint,

    /// <summary>A static class of Wolverine endpoints, public so Wolverine discovers it.</summary>
    Endpoint,

    /// <summary>A Razor Component — the slice's screen (spec §3). Razor always generates it public.</summary>
    Component,

    /// <summary>A type in an endpoint's signature, or a component's parameters — public because Wolverine's generated code, or Razor's, uses it.</summary>
    Contract,

    /// <summary>None of the above: a violation.</summary>
    Stray,
}

/// <summary>
/// The slice boundary rules from spec §12, checked over compiled IL with Mono.Cecil.
/// Each rule returns its violations as readable strings; an empty list is a pass.
/// </summary>
internal static class SliceRules
{
    private const string StoreOptions = "Marten.StoreOptions";

    private static readonly string[] FrameworkNamespaces =
        ["Microsoft.AspNetCore", "Marten", "JasperFx", "Wolverine", "Microsoft.EntityFrameworkCore", "Npgsql"];

    public static ModuleDefinition Load(System.Reflection.Assembly assembly) =>
        ModuleDefinition.ReadModule(assembly.Location);

    /// <summary>
    /// Every effectively public type in a slice, by role. Wolverine forces endpoints and
    /// everything in their signatures to be public, so "public" no longer means "event":
    /// contracts are worked out from the endpoints, and events are the sealed records left.
    /// </summary>
    public static IReadOnlyDictionary<TypeDefinition, PublicRole> PublicRoles(ModuleDefinition module, SliceLayout layout)
    {
        var inSlices = module.GetTypes().Where(t => IsEffectivelyPublic(t) && SliceOf(t, layout) is not null).ToList();
        var contracts = Contracts(inSlices.Where(IsWolverineEndpoint), inSlices.Where(IsComponent), layout);

        return inSlices.ToDictionary(t => t, t =>
            IsEntryPoint(t, SliceOf(t, layout)!) ? PublicRole.EntryPoint
            : IsWolverineEndpoint(t) ? PublicRole.Endpoint
            : IsComponent(t) ? PublicRole.Component
            : contracts.Contains(t) ? PublicRole.Contract
            : IsSealedRecord(t) ? PublicRole.Event
            : PublicRole.Stray);
    }

    /// <summary>The events a slice owns, by full name: its public sealed records that are not contracts.</summary>
    public static IReadOnlyList<string> Events(ModuleDefinition module, SliceLayout layout) =>
        PublicRoles(module, layout).Where(r => r.Value == PublicRole.Event).Select(r => r.Key.FullName).Order().ToList();

    /// <summary>
    /// Public types in a slice are its events, its optional <c>&lt;Name&gt;Slice</c>, its
    /// Wolverine endpoints, and the types in their signatures — nothing else.
    /// </summary>
    public static IReadOnlyList<string> PublicSurface(ModuleDefinition module, SliceLayout layout) =>
        PublicRoles(module, layout)
            .Where(r => r.Value == PublicRole.Stray)
            .Select(r => $"{SliceOf(r.Key, layout)}: {r.Key.FullName} is public, but is neither a sealed record event, " +
                         $"the {SliceOf(r.Key, layout)}Slice entry point, a Wolverine endpoint, a component, nor in an endpoint's signature")
            .ToList();

    /// <summary>
    /// A slice may use another slice's events, and nothing else — not its internals, and
    /// not the types Wolverine forced public (its state, request, response).
    /// </summary>
    public static IReadOnlyList<string> CrossSliceDependencies(ModuleDefinition module, SliceLayout layout)
    {
        var roles = PublicRoles(module, layout);
        return module.GetTypes()
            .Select(t => (type: t, slice: SliceOf(t, layout)))
            .Where(x => x.slice is not null)
            .SelectMany(x => ReferencedTypes(x.type)
                .Where(r => r.Module == module)
                .Select(r => r.Resolve())
                .OfType<TypeDefinition>()
                .Where(r => SliceOf(r, layout) is { } other && other != x.slice
                            && roles.GetValueOrDefault(r, PublicRole.Stray) != PublicRole.Event)
                .Select(r => $"{x.slice}: {x.type.FullName} depends on {r.FullName}, which is not an event of {SliceOf(r, layout)}"))
            .Distinct()
            .ToList();
    }

    /// <summary>Shared code is pure: no slices, no ASP.NET, no persistence, no Wolverine.</summary>
    public static IReadOnlyList<string> SharedIsPure(ModuleDefinition module, SliceLayout layout) =>
        module.GetTypes()
            .Where(t => IsIn(Namespace(t), layout.SharedNamespace))
            .SelectMany(t => ReferencedTypes(t)
                .Select(Namespace)
                .Where(ns => IsIn(ns, layout.SlicesNamespace) || FrameworkNamespaces.Any(f => IsIn(ns, f)))
                .Select(ns => $"Shared: {t.FullName} depends on {ns}"))
            .Distinct()
            .ToList();

    /// <summary>
    /// A slice's <c>&lt;Name&gt;Slice</c> is optional — only slices that own events or
    /// projections need one — but where it exists it must be well-formed and called
    /// from the catalog's <c>Register</c>.
    /// </summary>
    public static IReadOnlyList<string> RegistrationsWired(ModuleDefinition module, SliceLayout layout)
    {
        var catalog = module.GetType(layout.CatalogType)
            ?? throw new InvalidOperationException($"Catalog {layout.CatalogType} not found");
        var called = CallsIn(catalog, "Register");

        var violations = new List<string>();
        foreach (var slice in module.GetTypes().Select(t => SliceOf(t, layout)).OfType<string>().Distinct().Order())
        {
            var entry = module.GetType($"{layout.SlicesNamespace}.{slice}.{slice}Slice");
            if (entry is null)
                continue;
            if (!IsEntryPoint(entry, slice))
                violations.Add($"{slice}: {slice}Slice is not a public static class with Register(StoreOptions)");
            else if (!called.Contains($"{entry.FullName}::Register"))
                violations.Add($"{slice}: {catalog.Name}.Register does not call {entry.Name}.Register");
        }
        return violations;
    }

    /// <summary>
    /// Every slice's <c>State</c> — what it folds from the stream, whether to decide
    /// or to read — carries a unique Marten <c>[DocumentAlias]</c>. Marten names a type
    /// by its bare class name, so two slices' <c>State</c>s otherwise collide — and only
    /// fail at runtime, in whichever slice is used second.
    /// </summary>
    public static IReadOnlyList<string> StateAliases(ModuleDefinition module, SliceLayout layout)
    {
        var folded = module.GetTypes()
            .Where(t => t.Name == "State" && SliceOf(t, layout) is not null)
            .Select(t => (what: $"{SliceOf(t, layout)}.{t.Name}", alias: t.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.FullName == "Marten.Schema.DocumentAliasAttribute")
                ?.ConstructorArguments[0].Value as string))
            .ToList();

        return folded.Where(s => s.alias is null)
            .Select(s => $"{s.what} has no [DocumentAlias]")
            .Concat(folded.Where(s => s.alias is not null)
                .GroupBy(s => s.alias)
                .Where(g => g.Count() > 1)
                .Select(g => $"{string.Join(", ", g.Select(s => s.what).Order())}: alias '{g.Key}' is not unique"))
            .ToList();
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
        && type.Methods.Any(m => m.Name == "Register" && m.IsStatic && m.IsPublic
                                 && m.Parameters is [var p] && p.ParameterType.FullName == StoreOptions);

    private static readonly HashSet<string> WolverineRouteAttributes =
        ["Get", "Post", "Put", "Delete", "Patch", "Head", "Options"];

    /// <summary>A Razor Component: derives from <c>ComponentBase</c>, as generated components do.</summary>
    private static bool IsComponent(TypeDefinition type) =>
        type.BaseType?.FullName == "Microsoft.AspNetCore.Components.ComponentBase";

    /// <summary>A static class with at least one <c>[Wolverine&lt;Verb&gt;]</c> route method.</summary>
    private static bool IsWolverineEndpoint(TypeDefinition type) =>
        type.IsAbstract && type.IsSealed
        && type.Methods.Any(m => m.IsPublic && m.CustomAttributes.Any(a =>
            a.AttributeType.Namespace == "Wolverine.Http"
            && a.AttributeType.Name.StartsWith("Wolverine", StringComparison.Ordinal)
            && a.AttributeType.Name.EndsWith("Attribute", StringComparison.Ordinal)
            && WolverineRouteAttributes.Contains(a.AttributeType.Name["Wolverine".Length..^"Attribute".Length])));

    /// <summary>
    /// Types of the endpoints' own slices that appear in their public methods' signatures,
    /// and — because C# requires it — in the public members of those types, transitively.
    /// </summary>
    private static HashSet<TypeDefinition> Contracts(
        IEnumerable<TypeDefinition> endpoints, IEnumerable<TypeDefinition> components, SliceLayout layout)
    {
        var found = new HashSet<TypeDefinition>();
        var pending = new Stack<(TypeReference type, string slice)>();
        foreach (var endpoint in endpoints)
            foreach (var method in endpoint.Methods.Where(m => m.IsPublic))
            {
                pending.Push((method.ReturnType, SliceOf(endpoint, layout)!));
                foreach (var p in method.Parameters) pending.Push((p.ParameterType, SliceOf(endpoint, layout)!));
            }
        // A component's parameters must be public too: they are its public properties.
        foreach (var component in components)
            foreach (var property in component.Properties.Where(p => p.GetMethod is { IsPublic: true }))
                pending.Push((property.PropertyType, SliceOf(component, layout)!));

        while (pending.TryPop(out var next))
        {
            // Filter by namespace before resolving: only this slice's types need resolving.
            var own = Flatten(next.type).Where(t => SliceOf(t, layout) == next.slice);
            foreach (var type in own.Select(t => t.Resolve()).OfType<TypeDefinition>())
            {
                if (!found.Add(type))
                    continue;
                foreach (var p in type.Properties.Where(p => p.GetMethod is { IsPublic: true }))
                    pending.Push((p.PropertyType, next.slice));
                foreach (var f in type.Fields.Where(f => f.IsPublic))
                    pending.Push((f.FieldType, next.slice));
                foreach (var p in type.Methods.Where(m => m.IsConstructor && m.IsPublic).SelectMany(m => m.Parameters))
                    pending.Push((p.ParameterType, next.slice));
            }
        }
        return found;
    }

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
