namespace janus_ledger_api.Commands
{
    public sealed record ReturnSuspenseToProviderCommand(
    Guid TransactionId,
    Guid OperatorUserId
);
}
