using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using UrlShortener.Shortener.Api.Email;

namespace UrlShortener.Shortener.Api.Tests;

/// <summary>Collects the mails the API "sends" so tests can read the links in them.</summary>
internal sealed partial class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<SentMail> _sent = new();

    /// <summary>Every mail handed over, oldest first.</summary>
    public IReadOnlyList<SentMail> Sent => _sent.ToList();

    /// <summary>When set, sending fails like an unreachable mail server.</summary>
    public bool Fail { get; set; }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            return Task.FromException(new EmailSendException(new IOException("mail server down")));
        }

        _sent.Enqueue(new SentMail(to, subject, body));
        return Task.CompletedTask;
    }

    /// <summary>The token in the newest mail to the address; fails the test if there is none.</summary>
    public string TokenSentTo(string address)
    {
        var mail = Sent.LastOrDefault(mail => mail.To == address)
            ?? throw new AssertionException($"No mail was sent to {address}.");

        var match = TokenInLink().Match(mail.Body);
        return match.Success ? match.Groups[1].Value : throw new AssertionException("The mail has no verification link.");
    }

    [GeneratedRegex(@"#token=([A-Za-z0-9_-]+)")]
    private static partial Regex TokenInLink();
}

/// <summary>One mail handed to the fake sender.</summary>
internal sealed record SentMail(string To, string Subject, string Body);
