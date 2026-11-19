using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Janus.Core.Shared.DTOs;

namespace Janus.BackOffice.Ui.Features.Suspense.Pages;

public partial class CorrectTransactionModal
{
    [Inject]
    private IConfiguration Configuration { get; set; } = null!;

    [Parameter]
    public SuspenseTransactionItemDto Transaction { get; set; } = null!;

    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public EventCallback<SubmitCorrectionDto> OnConfirm { get; set; }

    [Parameter]
    public EventCallback OnCancel { get; set; }

    private string _correctedEgn = string.Empty;
    private string _errorMessage = string.Empty;
    private int _expectedEgnLength;

    protected override void OnInitialized()
    {
        var egnLengthSetting = Configuration["IdentityLimits:EgnLength"];

        if (!int.TryParse(egnLengthSetting, out _expectedEgnLength) || _expectedEgnLength <= 0)
        {
            throw new InvalidOperationException("Critical configuration missing: IdentityLimits:EgnLength must be configured as a positive integer.");
        }
    }

    protected override void OnParametersSet()
    {
        if (IsOpen)
        {
            _correctedEgn = Transaction.CorrectedEGN ?? string.Empty;
            _errorMessage = string.Empty;
        }
    }

    private async Task Submit()
    {
        _errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_correctedEgn) || _correctedEgn.Length != _expectedEgnLength || !_correctedEgn.All(char.IsDigit))
        {
            _errorMessage = $"The identifier must be exactly {_expectedEgnLength} numeric characters.";
            return;
        }

        var dto = new SubmitCorrectionDto(Transaction.TransactionId, _correctedEgn);
        await OnConfirm.InvokeAsync(dto);
    }

    private async Task Cancel()
    {
        _correctedEgn = string.Empty;
        _errorMessage = string.Empty;
        await OnCancel.InvokeAsync();
    }
}
