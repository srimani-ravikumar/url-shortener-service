using UrlShortener.Core.Validation;

namespace UrlShortener.Core.Tests;

/// <summary>Tests destination URL validation with a short domain of trysmartly.co.</summary>
public class LongUrlValidatorTests
{
    private static readonly LongUrlValidator Validator = new(new Uri("http://trysmartly.co"));

    [TestCase("http://example.com")]
    [TestCase("https://example.com/very/long/url?ref=dev&x=1#frag")]
    [TestCase("HTTPS://EXAMPLE.COM:8443/path")]
    [TestCase("https://sub.trysmartly.co/page")] // a subdomain is a different host, so it is allowed
    [TestCase("https://trysmartly.co.example.com/page")] // only an exact host match counts as "ourselves"
    public void Validate_AcceptsValidUrls(string url)
    {
        Assert.That(Validator.Validate(url).IsValid, Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("example.com")]
    [TestCase("/relative/path")]
    [TestCase("ftp://example.com/file")]
    [TestCase("file:///c:/windows/win.ini")]
    [TestCase("javascript:alert(1)")]
    [TestCase("mailto:someone@example.com")]
    [TestCase("http://")]
    [TestCase("http://exa mple.com")]
    [TestCase(" https://example.com")]
    [TestCase("https://example.com ")]
    public void Validate_RejectsInvalidUrls(string? url)
    {
        var result = Validator.Validate(url);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
    }

    [TestCase("http://trysmartly.co/abc")]
    [TestCase("https://TRYSMARTLY.CO/abc")]
    [TestCase("https://trysmartly.co./abc")]
    [TestCase("https://trysmartly.co:8080/abc")] // the port is ignored when matching the host
    public void Validate_RejectsLinksToTheShortDomain(string url)
    {
        var result = Validator.Validate(url);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Does.Contain("short URL service"));
    }

    [Test]
    public void Validate_EnforcesMaxLength()
    {
        // Boundary check: exactly the limit passes, one character more fails.
        var prefix = "https://example.com/";

        Assert.That(Validator.Validate(prefix + new string('a', LongUrlValidator.DefaultMaxLength - prefix.Length)).IsValid, Is.True);
        Assert.That(Validator.Validate(prefix + new string('a', LongUrlValidator.DefaultMaxLength - prefix.Length + 1)).IsValid, Is.False);
    }
}
