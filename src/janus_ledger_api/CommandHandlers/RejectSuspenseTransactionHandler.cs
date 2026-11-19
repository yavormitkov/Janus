namespace Janus.Ledger.Api.Handlers;

using Janus.Ledger.Api.Commands;
using Janus.Ledger.Api.Repositories;

public class RejectSuspenseTransactionHandler
{
    private readonly IDapperSuspenseTransactionRepository _repository;

    public RejectSuspenseTransactionHandler(IDapperSuspenseTransactionRepository repository)
    {
        _repository = repository;
    }

    public async Task<bool> HandleAsync(RejectSuspenseTransactionCommand command)
    {
        return await _repository.RejectAsync(command.TransactionId, command.CheckerUserId);
    }
}
