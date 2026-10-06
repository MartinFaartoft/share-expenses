using SplitIt.Infrastructure.Identity;

namespace SplitIt.Tests.Identity;

/// <summary>What the emails say (spec §4): the code where Apple looks for it, no markup injected, plain text beside HTML.</summary>
public class EmailContentTests
{
    [Fact]
    public void The_sign_in_code_is_in_the_subject_and_the_first_line()
    {
        var mail = EmailContent.SignIn("123456");

        Assert.Equal("Your sign-in code is 123456", mail.Subject);
        Assert.StartsWith("Your sign-in code is 123456.", mail.Text);
        Assert.Contains("<strong>123456</strong>", mail.Html);
    }

    [Fact]
    public void The_invite_names_the_inviter_the_group_and_the_address_to_sign_in_with()
    {
        var mail = EmailContent.Invite("bob@example.com", "https://splitit.ftft.dk/", "Lisbon trip", "Alice", "Bob");

        Assert.Equal("Alice invited you to Lisbon trip on SplitIt", mail.Subject);
        Assert.Contains("Alice invited you to Lisbon trip as Bob.", mail.Text);
        Assert.Contains("Sign in with this address (bob@example.com) to join: https://splitit.ftft.dk/", mail.Text);
        Assert.Contains("""<a href="https://splitit.ftft.dk/">""", mail.Html);
    }

    [Fact]
    public void Names_cannot_inject_markup_into_the_html_part()
    {
        var mail = EmailContent.Invite("bob@example.com", "https://splitit.ftft.dk/", "<script>x</script>", "A & B", "\"Bob\"");

        Assert.DoesNotContain("<script>", mail.Html);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", mail.Html);
        Assert.Contains("A &amp; B", mail.Html);
        Assert.Contains("<script>x</script>", mail.Text);
    }
}
