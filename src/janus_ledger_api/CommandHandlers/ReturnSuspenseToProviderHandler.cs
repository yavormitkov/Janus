using Janus.Core.Shared.Enums;
using Janus.Ledger.Api.Repositories;
using janus_ledger_api.Commands;

namespace janus_ledger_api.CommandHandlers
{
    public sealed class ReturnSuspenseToProviderHandler
    {
        private readonly IDapperSuspenseTransactionRepository _repository;

        public ReturnSuspenseToProviderHandler(IDapperSuspenseTransactionRepository repository)
        {
            _repository = repository;
        }

        public async Task<bool> HandleAsync(ReturnSuspenseToProviderCommand command)
        {
            var transaction = await _repository.GetByIdAsync(command.TransactionId);
            if (transaction is null)
            {
                return false;
            }

            if (transaction.SuspenseStatus != (int)SuspenseStatus.Duplicate)
            {
                return false;
            }

            return await _repository.ReturnToProviderAsync(
                command.TransactionId,
                command.OperatorUserId
            );
        }
    }
}
