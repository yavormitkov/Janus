using Janus.Core.Shared.Enums;

namespace Janus.Ledger.Api.Queries;

public record GetUnresolvedSuspenseTransactionsQuery(SuspenseStatus Status);
