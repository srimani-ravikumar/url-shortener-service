using UrlShortener.Core.Validation;

namespace UrlShortener.Core.Tests;

/// <summary>Tests the sanity check and normalization applied to account email addresses.</summary>
public class EmailAddressValidatorTests
{
    private static readonly EmailAddressValidator Validator = new();

    [TestCase("user@example.com", "user@example.com")]
    [TestCase("  User@Example.COM  ", "user@example.com")]
    [TestCase("first.last+tag@sub.example.co.uk", "first.last+tag@sub.example.co.uk")]
    public void Validate_AcceptsValidAddresses_AndNormalizesThem(string email, string expected)
    {
        var result = Validator.Validate(email, out var normalized);

        Assert.That(result.IsValid, Is.True);
        Assert.That(normalized, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no-at-sign.example.com")]
    [TestCase("two@@example.com")]
    [TestCase("two@at@example.com")]
    [TestCase("@example.com")]
    [TestCase("user@")]
    [TestCase("user@nodot")]
    [TestCase("user@.example.com")]
    [TestCase("user@example..com")]
    [TestCase("user@example.com.")]
    [TestCase("us er@example.com")]
    [TestCase("user@exa mple.com")]
    [TestCase("user@example.com\nBcc: other@example.com")] // header injection into the mail
    public void Validate_RejectsInvalidAddresses(string? email)
    {
        var result = Validator.Validate(email, out var normalized);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
        Assert.That(normalized, Is.Empty);
    }

    [Test]
    public void Validate_EnforcesLengthLimits()
    {
        var longestLocal = new string('a', 64) + "@example.com";
        var tooLongLocal = new string('a', 65) + "@example.com";
        var tooLongOverall = "a@" + new string('b', EmailAddressValidator.MaxLength) + ".com";

        Assert.That(Validator.Validate(longestLocal, out _).IsValid, Is.True);
        Assert.That(Validator.Validate(tooLongLocal, out _).IsValid, Is.False);
        Assert.That(Validator.Validate(tooLongOverall, out _).IsValid, Is.False);
    }
}
