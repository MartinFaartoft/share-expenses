namespace SplitIt.Infrastructure;

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
