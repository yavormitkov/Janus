using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Janus.Ledger.Api.Commands;
using Janus.Ledger.Api.Repositories;

namespace Janus.Ledger.Api.CommandHandlers;

public class CorrectSuspenseTransactionHandler
{
    private readonly IDapperSuspenseTransactionRepository    _repository;
    private readonly IConfiguration _configuration;

    public CorrectSuspenseTransactionHandler(IDapperSuspenseTransactionRepository repository, IConfiguration configuration)
    {
        _repository = repository;
        _configuration = configuration;
    }

    public async Task<bool> HandleAsync(CorrectSuspenseTransactionCommand command)
    {
        var egnLengthSetting = _configuration["IdentityLimits:EgnLength"];
        var expectedLength = int.TryParse(egnLengthSetting, out var len) ? len : 10;

        if (string.IsNullOrWhiteSpace(command.CorrectedEGN) || command.CorrectedEGN.Length != expectedLength)
        {
            return false;
        }

        var t = await _repository.GetByIdAsync(command.TransactionId);
        if (t == null)
        {
            return false;
        }

        var maxLimitSetting = _configuration["SuspensePolicy:MaxManualCorrectionLimit"];
        if (decimal.TryParse(maxLimitSetting, out var maxLimit) && t.Amount > maxLimit)
        {
            return false;
        }

        return await _repository.UpdateToPendingApprovalAsync(
            command.TransactionId,
            command.MakerUserId,
            command.CorrectedEGN,
            command.CorrectedClientName
        );
    }
}
