using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SplitIt.Infrastructure.Identity;

namespace SplitIt.Tests.Identity;

/// <summary>Which sender the app starts with, and when it refuses to start (spec §4).</summary>
public class EmailSendingRegistrationTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IEmailSender Sender(string environment, params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, s.Value))).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddEmailSending(configuration, new Environment(environment));
        return services.BuildServiceProvider().GetRequiredService<IEmailSender>();
    }

    private static readonly (string, string?) Key = ("Email:Resend:ApiKey", "re_key");
    private static readonly (string, string?) From = ("Email:From", "noreply@splitit.ftft.dk");

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Without_a_key_outside_production_the_log_sender_is_used(string environment) =>
        Assert.IsType<LogEmailSender>(Sender(environment));

    [Fact]
    public void Without_a_key_production_refuses_to_start()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Sender("Production"));

        Assert.Contains("Email:Resend:ApiKey", failure.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void With_a_key_and_a_from_address_resend_is_used_in_any_environment(string environment) =>
        Assert.IsType<ResendEmailSender>(Sender(environment, Key, From));

    [Fact]
    public void A_blank_key_is_no_key() =>
        Assert.Throws<InvalidOperationException>(() => Sender("Production", ("Email:Resend:ApiKey", "  "), From));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    public void A_key_without_a_usable_from_address_refuses_to_start(string? from)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Sender("Development", Key, ("Email:From", from)));

        Assert.Contains("Email:From", failure.Message);
    }

    [Fact]
    public void A_bare_address_is_a_usable_from() =>
        Assert.IsType<ResendEmailSender>(Sender("Production", Key, ("Email:From", "noreply@splitit.ftft.dk")));
}
