using System.Collections.Generic;
using System.IO;
using Janus.Core.Shared.Models;

namespace Janus.ClearingEngine.Parsers;

public interface IClearingParser
{
    List<ClearingDataRow> ParseStreamAndValidateFooter(StreamReader reader, string traceParent);
}
