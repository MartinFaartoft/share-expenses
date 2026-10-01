using System.Collections.Concurrent;
using System.Reflection;

namespace ShareExpenses.Tests.Specs;

/// <summary>
/// Folds a spec's history into a slice's <c>State</c> by the same convention Marten
/// uses: <c>static Create(TEvent)</c> starts the state, <c>Apply(TEvent)</c> evolves it
/// (returning the new state, or mutating it and returning nothing).
///
/// Stricter than Marten in one way, on purpose: an event the state has no method for
/// throws, unless the spec lists it in <c>ignoring</c>. Marten would skip it silently,
/// which is exactly how a state falls behind a new event unnoticed (the fold
/// checklists, spec §12). Listing it makes "this state does not care" a decision.
/// </summary>
internal static class Fold
{
    private static readonly ConcurrentDictionary<(Type State, Type Event, string Name), MethodInfo?> Methods = new();

    public static TState? Of<TState>(IEnumerable<object> history, params Type[] ignoring) where TState : class
    {
        TState? state = null;
        foreach (var e in history)
        {
            var create = Find(typeof(TState), e.GetType(), "Create", BindingFlags.Static);
            var apply = Find(typeof(TState), e.GetType(), "Apply", BindingFlags.Instance);

            if (state is null && create is not null)
                state = (TState)Invoke(create, null, e)!;
            else if (apply is not null)
            {
                if (state is null)
                    throw new InvalidOperationException(
                        $"{e.GetType().Name} was applied before {typeof(TState).Name} was created");
                var next = Invoke(apply, state, e);
                state = apply.ReturnType == typeof(void) ? state : (TState)next!;
            }
            else if (!ignoring.Contains(e.GetType()))
                throw new InvalidOperationException(
                    $"{typeof(TState).FullName} does not fold {e.GetType().Name}. Add Create/Apply for it, " +
                    $"or list it in `ignoring` if this state genuinely does not care.");
        }
        return state;
    }

    private static MethodInfo? Find(Type state, Type @event, string name, BindingFlags kind) =>
        Methods.GetOrAdd((state, @event, name), key =>
            key.State.GetMethod(key.Name, BindingFlags.Public | kind, [key.Event]));

    // Unwrapped, so a spec fails with the state's own exception, not TargetInvocationException.
    private static object? Invoke(MethodInfo method, object? target, object e) =>
        method.Invoke(target, BindingFlags.DoNotWrapExceptions, binder: null, [e], culture: null);
}
