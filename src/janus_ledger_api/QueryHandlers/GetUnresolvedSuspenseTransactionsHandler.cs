using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Janus.Core.Shared.DTOs;
using Janus.Core.Shared.Enums;
using Janus.Ledger.Api.Queries;
using Janus.Ledger.Api.Repositories;

namespace Janus.Ledger.Api.QueryHandlers;

public class GetUnresolvedSuspenseTransactionsHandler
{
    private readonly IDapperSuspenseTransactionRepository _repository;

    public GetUnresolvedSuspenseTransactionsHandler(IDapperSuspenseTransactionRepository repository)
    {
        _repository = repository;
    }

    public async Task<IEnumerable<SuspenseTransactionItemDto>> HandleAsync(GetUnresolvedSuspenseTransactionsQuery query)
    {
        var transactions = await _repository.GetUnresolvedAsync(query.Status);

        return transactions.Select(t =>
        {
            bool isNameMismatch = !string.IsNullOrWhiteSpace(t.CorrectedClientName) &&
                                  string.Compare(t.InboundClientName, t.CorrectedClientName, StringComparison.OrdinalIgnoreCase) != 0;

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
                t.UpdatedAt ?? t.CreatedAt,
                t.OriginalReferenceNumber,
                t.OriginalFileName,
                t.OriginalEGN,
                t.OriginalClientName,
                t.OriginalAmount,
                t.OriginalCreatedAt
            );
        });
    }
}
