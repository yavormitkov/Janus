using System;
using System.Threading.Tasks;
using Janus.Core.Shared.DTOs;
using Janus.Core.Shared.Enums;
using Janus.Ledger.Api.Queries;
using Janus.Ledger.Api.Repositories;

namespace Janus.Ledger.Api.QueryHandlers;

public class GetSuspenseTransactionByIdHandler
{
    private readonly IDapperSuspenseTransactionRepository _repository;

    public GetSuspenseTransactionByIdHandler(IDapperSuspenseTransactionRepository repository)
    {
        _repository = repository;
    }

    public async Task<SuspenseTransactionItemDto?> HandleAsync(GetSuspenseTransactionByIdQuery query)
    {
        var t = await _repository.GetByIdAsync(query.TransactionId);
        if (t == null)
        {
            return null;
        }

        bool isNameMismatch = !string.IsNullOrWhiteSpace(t.CorrectedClientName) &&
                              string.Compare(t.InboundClientName ?? string.Empty, t.CorrectedClientName, StringComparison.OrdinalIgnoreCase) != 0;

        return new SuspenseTransactionItemDto(
            t.TransactionId,
            t.FileReference,
            t.InboundEGN,
            t.InboundClientName,
            t.CorrectedEGN,
            t.Amount,
            t.ReferenceNumber,
            (SuspenseStatus)t.SuspenseStatus,
            isNameMismatch,
            t.MakerUserId,
            t.CheckerUserId,
            t.CreatedAt,
            t.UpdatedAt ?? t.CreatedAt
        );
    }
}
