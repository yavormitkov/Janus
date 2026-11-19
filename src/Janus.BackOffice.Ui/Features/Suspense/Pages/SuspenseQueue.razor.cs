using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Janus.BackOffice.Ui.Services.Http;
using Janus.BackOffice.Ui.Services.Identity;
using Janus.Core.Shared.Enums;
using Janus.Core.Shared.DTOs;

namespace Janus.BackOffice.Ui.Features.Suspense.Pages;

public partial class SuspenseQueue : IDisposable
{
    [Inject]
    private JanusApiClient ApiClient { get; set; } = null!;

    [Inject]
    private SimulationIdentityService IdentityService { get; set; } = null!;

    [Inject]
    private IConfiguration Configuration { get; set; } = null!;

    private List<SuspenseTransactionItemDto>? _transactions;
    private string _currentRole = "Maker";

    private SuspenseStatus _filterStatus 
    {
        get
        { 
            return _currentRole == "Maker" ? SuspenseStatus.Unresolved : SuspenseStatus.PendingApproval; 
        }
    }
    private SuspenseTransactionItemDto? _selectedTransaction;
    private bool _isModalOpen;
    private decimal _maxManualCorrectionLimit;

    protected override async Task OnInitializedAsync()
    {
        _currentRole = IdentityService.CurrentRole;
        IdentityService.OnIdentityChanged += HandleIdentityChanged;

        var maxLimitSetting = Configuration["SuspensePolicy:MaxManualCorrectionLimit"];

        if (!decimal.TryParse(maxLimitSetting, out _maxManualCorrectionLimit) || _maxManualCorrectionLimit <= 0)
        {
            throw new InvalidOperationException("Critical configuration missing: SuspensePolicy:MaxManualCorrectionLimit must be configured as a positive decimal.");
        }

        await LoadQueueAsync();
    }

    private async Task LoadQueueAsync()
    {
        _transactions = null;
        StateHasChanged();

        try
        {
            _transactions = await ApiClient.GetUnresolvedTransactionsAsync(_filterStatus);
        }
        catch(Exception ex)
        {
            _transactions = new List<SuspenseTransactionItemDto>();
        }

        StateHasChanged();
    }

    private async void HandleIdentityChanged()
    {
        _currentRole = IdentityService.CurrentRole;
        await InvokeAsync(LoadQueueAsync);
    }

    private string GetStatusBadgeClass(SuspenseStatus status) => status switch
    {
        SuspenseStatus.Unresolved => "bg-danger",
        SuspenseStatus.PendingApproval => "bg-warning text-dark",
        SuspenseStatus.Cleared => "bg-success",
        SuspenseStatus.Rejected => "bg-secondary",
        SuspenseStatus.Refunded => "bg-info text-dark",
        _ => "bg-dark"
    };

    private bool IsTransactionOverLimit(SuspenseTransactionItemDto tx)
    {
        return tx.Amount > _maxManualCorrectionLimit;
    }

    private async Task HandleApproveAsync(Guid transactionId)
    {
        var result = await ApiClient.ProcessApprovalAsync(new ProcessCorrectionApprovalDto(transactionId));
        if (result.IsSuccessStatusCode)
        {
            await LoadQueueAsync();
        }
    }

    private async Task HandleRejectAsync(Guid transactionId)
    {
        var result = await ApiClient.ProcessRejectionAsync(new ProcessCorrectionApprovalDto(transactionId));
        if (result.IsSuccessStatusCode)
        {
            await LoadQueueAsync();
        }
    }

    private void OpenCorrectionModal(SuspenseTransactionItemDto tx)
    {
        _selectedTransaction = tx;
        _isModalOpen = true;
    }

    private void CloseCorrectionModal()
    {
        _selectedTransaction = null;
        _isModalOpen = false;
    }

    private async Task HandleCorrectionConfirmAsync(SubmitCorrectionDto dto)
    {
        var result = await ApiClient.ProposeCorrectionAsync(dto);
        if (result.IsSuccessStatusCode)
        {
            CloseCorrectionModal();
            await LoadQueueAsync();
        }
    }

    public void Dispose()
    {
        IdentityService.OnIdentityChanged -= HandleIdentityChanged;
    }
}
