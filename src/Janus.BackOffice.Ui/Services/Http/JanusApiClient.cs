using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Janus.Core.Shared.DTOs;
using Janus.Core.Shared.Enums;

namespace Janus.BackOffice.Ui.Services.Http;

public class JanusApiClient
{
    private readonly HttpClient _httpClient;

    public JanusApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<SuspenseTransactionItemDto>> GetUnresolvedTransactionsAsync(SuspenseStatus status)
    {
        var response = await _httpClient.GetFromJsonAsync<List<SuspenseTransactionItemDto>>($"api/suspense/unresolved/{(int)status}");
        return response ?? new List<SuspenseTransactionItemDto>();
    }

    public async Task<HttpResponseMessage> ProposeCorrectionAsync(SubmitCorrectionDto dto)
    {
        return await _httpClient.PostAsJsonAsync("api/suspense/correct", dto);
    }

    public async Task<HttpResponseMessage> ProcessApprovalAsync(ProcessCorrectionApprovalDto dto)
    {
        return await _httpClient.PostAsJsonAsync("api/suspense/approve", dto);
    }

    public async Task<HttpResponseMessage> ProcessRejectionAsync(ProcessCorrectionApprovalDto dto)
    {
        return await _httpClient.PostAsJsonAsync("api/suspense/reject", dto);
    }

    public async Task<HttpResponseMessage> ProcessReturnToProviderAsync(ProcessCorrectionApprovalDto dto)
    {
        return await _httpClient.PostAsJsonAsync("api/suspense/return-to-provider", dto);
    }
}
