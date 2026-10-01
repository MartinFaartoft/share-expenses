namespace ShareExpenses.Infrastructure;

/// <summary>
/// The scheme and host that links sent to people point at, e.g. invite links in
/// emails. Configured as <c>App:PublicOrigin</c> and required outside Development:
/// deriving it from the incoming request would let whoever controls the Host header
/// shape the links in our emails. Development falls back to the request's own host.
/// </summary>
public sealed class PublicOrigin(string? configured)
{
    public const string ConfigKey = "App:PublicOrigin";

    public string For(HttpRequest request) => configured ?? $"{request.Scheme}://{request.Host}";

    internal static string? Validate(string? value, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return isDevelopment
                ? null
                : throw new InvalidOperationException(
                    $"{ConfigKey} is not configured. Outside Development it is required: links in " +
                    "emails must not be derived from the request's Host header.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} must be a scheme and host only, e.g. https://example.com — got '{value}'.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public static class PublicOriginRegistration
{
    public static IServiceCollection AddPublicOrigin(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment env) =>
        services.AddSingleton(new PublicOrigin(
            PublicOrigin.Validate(configuration[PublicOrigin.ConfigKey], env.IsDevelopment())));
}
