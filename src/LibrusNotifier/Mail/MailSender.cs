using System.Net.Sockets;
using LibrusNotifier.Configuration;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LibrusNotifier.Mail;

/// <param name="Body">Treść tekstowa (dla maila z <paramref name="HtmlBody"/> – wersja alternatywna).</param>
/// <param name="HtmlBody">Treść HTML; gdy brak, mail jest czystym tekstem.</param>
public sealed record OutgoingMail(IReadOnlyList<string> To, string Subject, string Body, string? HtmlBody = null);

/// <summary>Błąd połączenia lub uwierzytelnienia SMTP: kolejne maile w tym cyklu też by się nie wysłały.</summary>
public sealed class MailTransportException(string message, Exception innerException) : Exception(message, innerException);

public interface IMailSender
{
    /// <exception cref="MailTransportException">Błąd połączenia lub logowania do serwera SMTP.</exception>
    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken);

    /// <summary>Zamyka połączenie po serii wysyłek (koniec cyklu).</summary>
    Task CloseAsync();
}

/// <summary>Wysyłka przez Gmail (MailKit). Parametry SMTP są wbudowane; połączenie jest otwierane przy pierwszym mailu cyklu.</summary>
public sealed class GmailSender(GmailSettings settings) : IMailSender, IAsyncDisposable
{
    public const string Host = "smtp.gmail.com";
    public const int Port = 465;
    public const string SenderName = "LibrusNotifier";

    private SmtpClient? _client;

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        var message = BuildMessage(settings.Email, mail);

        var client = await ConnectAsync(cancellationToken);
        try
        {
            await client.SendAsync(message, cancellationToken);
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            await CloseAsync();
            throw new MailTransportException($"Błąd połączenia z {Host}: {e.Message}", e);
        }
    }

    /// <summary>Mail z HTML-em to multipart/alternative (tekst + HTML); bez HTML-a – czysty tekst.</summary>
    internal static MimeMessage BuildMessage(string senderEmail, OutgoingMail mail)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(SenderName, senderEmail));
        foreach (var address in mail.To) message.To.Add(MailboxAddress.Parse(address));
        message.Subject = mail.Subject;
        message.Body = mail.HtmlBody is null
            ? new TextPart("plain") { Text = mail.Body }
            : new BodyBuilder { TextBody = mail.Body, HtmlBody = mail.HtmlBody }.ToMessageBody();
        return message;
    }

    public async Task CloseAsync()
    {
        var client = _client;
        _client = null;
        if (client is null) return;
        try
        {
            if (client.IsConnected) await client.DisconnectAsync(true);
        }
        catch (Exception e) when (e is IOException or SocketException or MailKit.ProtocolException or ServiceNotConnectedException or OperationCanceledException)
        {
            // Połączenie i tak jest porzucane.
        }
        finally
        {
            client.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task<SmtpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true }) return _client;
        await CloseAsync();
        var client = new SmtpClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(Host, Port, SecureSocketOptions.SslOnConnect, cancellationToken);
            await client.AuthenticateAsync(settings.Email, settings.AppPassword, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new MailTransportException($"Nie można połączyć się lub zalogować do {Host}: {e.Message}", e);
        }
        _client = client;
        return client;
    }

    private static bool IsTransportFailure(Exception e) =>
        e is IOException or SocketException or MailKit.ProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException
            or AuthenticationException or SslHandshakeException or TimeoutException;
}
