using System.Net.Http.Json;

namespace PDVMottainai.Api.Services;

public sealed record PosApprovalRecipient(long CompanyId, long UserId);

public sealed record PosCashMovementNotification(
    Guid EventId,
    int StoreId,
    int EmployeeId,
    int MovementId,
    string MovementType,
    decimal Amount,
    string Reason,
    DateTimeOffset MovementDate,
    IReadOnlyList<PosApprovalRecipient> Recipients);

public sealed class PosApprovalCloudClient(HttpClient http, IConfiguration configuration)
{
    private readonly string? _url = configuration["Firebase:PosApprovalFunctionUrl"];
    private readonly string? _secret = configuration["Firebase:PosApprovalApiSecret"];

    public bool IsConfigured => Uri.TryCreate(_url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(_secret);

    public async Task DispatchCashMovementAsync(PosCashMovementNotification notification, CancellationToken ct)
    {
        EnsureConfigured();
        using var message = CreateMessage(new
        {
            operation = "cash_movement",
            eventId = notification.EventId,
            storeID = notification.StoreId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            employeeId = notification.EmployeeId,
            movementId = notification.MovementId,
            movementType = notification.MovementType,
            amount = notification.Amount,
            reason = notification.Reason,
            movementDate = notification.MovementDate,
            recipients = notification.Recipients.Select(recipient => new
            {
                companyId = recipient.CompanyId,
                userId = recipient.UserId
            })
        });
        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateMessage(object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = JsonContent.Create(body)
        };
        message.Headers.TryAddWithoutValidation("X-Mottainai-Pos-Secret", _secret);
        return message;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Firebase POS notification bridge is not configured with an HTTPS URL and secret.");
    }
}
