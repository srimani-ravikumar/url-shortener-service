using UrlShortener.Core.Validation;

namespace UrlShortener.Core.Tests;

/// <summary>Tests custom alias rules: length, allowed characters, reserved words and case sensitivity.</summary>
public class CustomAliasValidatorTests
{
    private static readonly CustomAliasValidator Validator = new();

    [TestCase("summer-sale")]
    [TestCase("Summer_Sale_2025")]
    [TestCase("abc")]
    [TestCase("123")]
    [TestCase("a-b_c")]
    public void Validate_AcceptsValidAliases(string alias)
    {
        Assert.That(Validator.Validate(alias).IsValid, Is.True);
    }

    [Test]
    public void Validate_EnforcesLengthLimits()
    {
        Assert.That(Validator.Validate(new string('a', CustomAliasValidator.MinLength - 1)).IsValid, Is.False);
        Assert.That(Validator.Validate(new string('a', CustomAliasValidator.MinLength)).IsValid, Is.True);
        Assert.That(Validator.Validate(new string('a', CustomAliasValidator.MaxLength)).IsValid, Is.True);
        Assert.That(Validator.Validate(new string('a', CustomAliasValidator.MaxLength + 1)).IsValid, Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ab")]
    [TestCase("has space")]
    [TestCase("slash/inside")]
    [TestCase("dot.inside")]
    [TestCase("unicodé")]
    [TestCase("trailing-newline\n")] // a regex ending in $ would wrongly accept this
    [TestCase("emoji-\U0001F600")]
    public void Validate_RejectsInvalidAliases(string? alias)
    {
        var result = Validator.Validate(alias);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
    }

    [TestCase("api")]
    [TestCase("admin")]
    [TestCase("Admin")]
    [TestCase("LOGIN")]
    [TestCase("shorten")]
    [TestCase("health")]
    [TestCase("app")]
    [TestCase("App")]
    [TestCase("signup")]
    [TestCase("recover")]
    [TestCase("verify")]
    public void Validate_RejectsReservedWords_CaseInsensitively(string alias)
    {
        var result = Validator.Validate(alias);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Does.Contain("reserved"));
    }

    [Test]
    public void Validate_RejectsAdditionalReservedWords()
    {
        var validator = new CustomAliasValidator(["forbidden"]);

        Assert.That(validator.Validate("Forbidden").IsValid, Is.False);
        Assert.That(Validator.Validate("Forbidden").IsValid, Is.True);
    }

    [Test]
    public void Validate_IsCaseSensitiveForDistinctAliases()
    {
        Assert.That(Validator.Validate("Summer-Sale").IsValid, Is.True);
        Assert.That(Validator.Validate("summer-sale").IsValid, Is.True);
    }
}
