using System.Net.Http.Json;
using System.Text.Json;

namespace PDVMottainai.Api.Services;

public sealed record PosApprovalNotification(
    Guid RequestId,
    int StoreId,
    int EmployeeId,
    string RequestType,
    int SaleId,
    int? SaleItemId,
    string Reason,
    decimal? RequestedAmount,
    decimal? DiscountPercent,
    IReadOnlyList<PosApprovalRecipient> Recipients);

public sealed record PosApprovalRecipient(long CompanyId, long UserId);

public sealed record PosApprovalDecision(
    Guid RequestId,
    string Status,
    string DecisionUid,
    string? Observation,
    DateTimeOffset DecidedAt);

public sealed class PosApprovalCloudClient(HttpClient http, IConfiguration configuration)
{
    private readonly string? _url = configuration["Firebase:PosApprovalFunctionUrl"];
    private readonly string? _secret = configuration["Firebase:PosApprovalApiSecret"];

    public bool IsConfigured => Uri.TryCreate(_url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(_secret);

    public async Task DispatchAsync(PosApprovalNotification request, CancellationToken ct)
    {
        EnsureConfigured();
        using var message = CreateMessage(new
        {
            operation = "dispatch",
            requestId = request.RequestId,
            storeID = request.StoreId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            employeeId = request.EmployeeId,
            requestType = request.RequestType,
            saleId = request.SaleId,
            saleItemId = request.SaleItemId,
            reason = request.Reason,
            requestedAmount = request.RequestedAmount,
            discountPercent = request.DiscountPercent,
            recipients = request.Recipients.Select(recipient => new
            {
                companyId = recipient.CompanyId,
                userId = recipient.UserId
            })
        });
        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<PosApprovalDecision>> GetDecisionsAsync(
        int storeId,
        IReadOnlyCollection<Guid> requestIds,
        CancellationToken ct)
    {
        EnsureConfigured();
        if (requestIds.Count == 0) return [];
        using var message = CreateMessage(new
        {
            operation = "decisions",
            storeID = storeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            requestIds
        });
        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("decisions", out var decisions)
            || decisions.ValueKind != JsonValueKind.Array)
            return [];
        return decisions.Deserialize<List<PosApprovalDecision>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
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
            throw new InvalidOperationException("Firebase POS approval bridge is not configured with an HTTPS URL and secret.");
    }
}
