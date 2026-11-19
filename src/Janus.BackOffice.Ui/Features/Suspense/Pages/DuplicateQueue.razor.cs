using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Janus.BackOffice.Ui.Services.Http;
using Janus.BackOffice.Ui.Services.Identity;
using Janus.Core.Shared.Enums;
using Janus.Core.Shared.DTOs;

namespace Janus.BackOffice.Ui.Features.Suspense.Pages;

public partial class DuplicateQueue : IDisposable
{
    [Inject]
    private JanusApiClient ApiClient { get; set; } = null!;

    [Inject]
    private SimulationIdentityService IdentityService { get; set; } = null!;

    private List<SuspenseTransactionItemDto>? _transactions;
    private string _currentRole = "Maker";
    private bool _isProcessing;
    private string? _errorMessage;

    protected override async Task OnInitializedAsync()
    {
        _currentRole = IdentityService.CurrentRole;
        IdentityService.OnIdentityChanged += HandleIdentityChanged;

        await LoadQueueAsync();
    }

    private async Task LoadQueueAsync()
    {
        _transactions = null;
        StateHasChanged();

        try
        {
            var settledDuplicatesTask = ApiClient.GetUnresolvedTransactionsAsync(SuspenseStatus.Duplicate);
            var suspenseDuplicatesTask = ApiClient.GetUnresolvedTransactionsAsync(SuspenseStatus.SuspenseDuplicate);

            await Task.WhenAll(settledDuplicatesTask, suspenseDuplicatesTask);

            _transactions = settledDuplicatesTask.Result
                .Concat(suspenseDuplicatesTask.Result)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            _errorMessage = $"Failed to load duplicate queue: {ex.Message}";
            _transactions = new List<SuspenseTransactionItemDto>();
        }

        StateHasChanged();
    }

    private void HandleIdentityChanged()
    {
        _currentRole = IdentityService.CurrentRole;
        InvokeAsync(StateHasChanged);
    }

    private async Task HandleRejectAsync(Guid transactionId)
    {
        await ExecuteActionAsync(() => ApiClient.ProcessRejectionAsync(new ProcessCorrectionApprovalDto(transactionId)));
    }

    private async Task HandleReturnToProviderAsync(Guid transactionId)
    {
        await ExecuteActionAsync(() => ApiClient.ProcessReturnToProviderAsync(new ProcessCorrectionApprovalDto(transactionId)));
    }

    private async Task ExecuteActionAsync(Func<Task<HttpResponseMessage>> action)
    {
        if (_isProcessing) return;

        _isProcessing = true;
        _errorMessage = null;
        StateHasChanged();

        try
        {
            var result = await action();
            if (result.IsSuccessStatusCode)
            {
                await LoadQueueAsync();
            }
            else
            {
                _errorMessage = $"Operation failed with status code {(int)result.StatusCode}.";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error executing operation: {ex.Message}";
        }
        finally
        {
            _isProcessing = false;
            StateHasChanged();
        }
    }

    public void Dispose()
    {
        IdentityService.OnIdentityChanged -= HandleIdentityChanged;
    }
}
