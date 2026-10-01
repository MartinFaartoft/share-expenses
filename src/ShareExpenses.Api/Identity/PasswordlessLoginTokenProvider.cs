using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ShareExpenses.Api.Identity;

/// <summary>
/// Stateless, self-validating sign-in link tokens (spec §4). Its own options type
/// so the 15-minute lifespan does not leak into Identity's default providers.
/// Not single-use on its own — consumption tracking is layered on top.
/// </summary>
public sealed class PasswordlessLoginTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordlessLoginTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<IdentityUser>> logger)
    : DataProtectorTokenProvider<IdentityUser>(dataProtectionProvider, options, logger)
{
    public const string ProviderName = "PasswordlessLogin";
    public const string Purpose = "passwordless-login";
}

public sealed class PasswordlessLoginTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordlessLoginTokenProviderOptions()
    {
        Name = PasswordlessLoginTokenProvider.ProviderName;
        TokenLifespan = TimeSpan.FromMinutes(15);
    }
}
