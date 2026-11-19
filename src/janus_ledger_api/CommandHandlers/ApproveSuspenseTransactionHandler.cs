namespace Janus.Ledger.Api.CommandHandlers;

using Janus.Ledger.Api.Commands;
using Janus.Ledger.Api.Repositories;

public class ApproveSuspenseTransactionHandler
{
    private readonly IDapperSuspenseTransactionRepository _repository;

    public ApproveSuspenseTransactionHandler(IDapperSuspenseTransactionRepository repository)
    {
        _repository = repository;
    }

    public async Task<bool> HandleAsync(ApproveSuspenseTransactionCommand command)
    {
        return await _repository.ApproveAsync(command.TransactionId, command.CheckerUserId);
    }
}
