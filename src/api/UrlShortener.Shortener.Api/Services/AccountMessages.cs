namespace UrlShortener.Shortener.Api.Services;

/// <summary>The texts of the mails the service sends.</summary>
internal static class AccountMessages
{
    private const string Brand = "trysmartly.co";

    public static (string Subject, string Body) SignUp(Uri link, TimeSpan lifetime) => (
        $"Confirm your email for {Brand}",
        $"""
        Hello,

        Confirm your email address to get your API key for {Brand}:

        {link}

        The link works once and expires in {Describe(lifetime)}.
        If you did not ask for this, ignore this mail; nothing happens without the link.
        """);

    public static (string Subject, string Body) AlreadyRegistered(Uri recoverLink) => (
        $"You already have an account on {Brand}",
        $"""
        Hello,

        Someone asked to create an account for this address, but it already has one.
        If that was you and you lost your API key, you can get a new one here:

        {recoverLink}

        If it was not you, ignore this mail.
        """);

    public static (string Subject, string Body) Recover(Uri link, TimeSpan lifetime) => (
        $"Get a new API key for {Brand}",
        $"""
        Hello,

        Use this link to get a new API key. Your current key stops working once you do:

        {link}

        The link works once and expires in {Describe(lifetime)}.
        If you did not ask for this, ignore this mail; your key stays as it is.
        """);

    private static string Describe(TimeSpan lifetime)
    {
        var hours = (int)Math.Round(lifetime.TotalHours);
        if (hours >= 1)
        {
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        var minutes = Math.Max(1, (int)Math.Round(lifetime.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
