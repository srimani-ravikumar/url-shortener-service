using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using UrlShortener.Shortener.Api.Configuration;

namespace UrlShortener.Shortener.Api.Email;

/// <summary>Sends plain-text mail.</summary>
internal interface IEmailSender
{
    /// <summary>Delivers a mail to the server. Throws <see cref="EmailSendException"/> if it could not be handed over.</summary>
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}

/// <summary>The mail server could not take the message; trying again later may work.</summary>
internal sealed class EmailSendException(Exception inner) : Exception("The email could not be sent.", inner);

/// <summary>Sends mail through an SMTP server (MailKit). A new connection per message is fine at sign-up volumes.</summary>
internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            // Built inside the try: an address MailKit refuses to parse must not surface as a 500.
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, _options.From));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            var security = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            await client.ConnectAsync(_options.Host, _options.Port, security, cancellationToken);

            if (!string.IsNullOrEmpty(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The recipient is personal data, so log only the failure.
            logger.LogError(ex, "Could not send a mail through {Host}:{Port}.", _options.Host, _options.Port);
            throw new EmailSendException(ex);
        }
    }
}
