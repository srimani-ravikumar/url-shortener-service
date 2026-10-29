using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Shortener.Api.Configuration;

/// <summary>Settings of sign-up, key recovery and the mails they send (configuration section "Account").</summary>
internal sealed class AccountOptions
{
    public const string SectionName = "Account";

    /// <summary>
    /// Public address of the site; verification links are built as {WebBaseUrl}/app/verify#token=...
    /// Must be the address users reach in their browser.
    /// </summary>
    [Required]
    public Uri WebBaseUrl { get; set; } = null!;

    /// <summary>How long an emailed link works.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Shortest time between two mails to the same address, whatever the caller does.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan MailCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How many sign-up and recovery requests one client address may make per hour.</summary>
    [Range(1, 100_000)]
    public int MailRequestsPerHour { get; set; } = 5;

    /// <summary>
    /// How many verification attempts one client address may make per hour. Tokens cannot be guessed,
    /// so this only has to stop floods; it is looser than the mail limit so a retry never locks a real user out.
    /// </summary>
    [Range(1, 100_000)]
    public int VerifyRequestsPerHour { get; set; } = 30;
}

/// <summary>Connection to the mail server (configuration section "Smtp"). Supply the password through the environment.</summary>
internal sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>Upgrade the connection to TLS with STARTTLS. Turn off only for a local catch-all like MailPit.</summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>Login name; leave empty for a server that needs no login.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>The sender address shown to recipients.</summary>
    [Required, EmailAddress]
    public string From { get; set; } = string.Empty;

    /// <summary>The sender name shown to recipients.</summary>
    public string FromName { get; set; } = "trysmartly.co";
}
