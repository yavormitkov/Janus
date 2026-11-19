using Janus.Core.Shared.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Janus.ClearingEngine.Parsers;

public sealed class EasyPayClearingParser : IClearingParser
{
    public List<ClearingDataRow> ParseStreamAndValidateFooter(StreamReader reader, string currentTraceId)
    {
        var records = new List<ClearingDataRow>();
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int runningCount = 0;
        decimal runningAmount = 0.0m;
        int fileRowCounter = 0;

        string? currentLine;
        while ((currentLine = reader.ReadLine()) != null)
        {
            fileRowCounter++;
            ReadOnlySpan<char> lineSpan = currentLine.AsSpan().Trim();
            if (lineSpan.IsEmpty)
            {
                continue;
            }

            int firstSpace = lineSpan.IndexOf(' ');
            if (firstSpace == -1)
            {
                throw new FormatException($"Structural layout breach detected on row {fileRowCounter}.");
            }

            ReadOnlySpan<char> remaining = lineSpan.Slice(firstSpace + 1).TrimStart();
            int secondSpace = remaining.IndexOf(' ');

            if (secondSpace != -1)
            {
                string egn = lineSpan.Slice(0, firstSpace).ToString();
                if (!decimal.TryParse(remaining.Slice(0, secondSpace), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount))
                {
                    throw new FormatException($"Invalid amount format on row {fileRowCounter}.");
                }

                ReadOnlySpan<char> postAmount = remaining.Slice(secondSpace + 1).TrimStart();
                int thirdSpace = postAmount.IndexOf(' ');
                if (thirdSpace == -1)
                {
                    throw new FormatException($"Missing client name or reference delimiter on row {fileRowCounter}.");
                }

                string reference = postAmount.Slice(0, thirdSpace).ToString();
                if (!seenReferences.Add(reference))
                {
                    throw new FormatException($"Intra-file duplicate reference '{reference}' detected on row {fileRowCounter}.");
                }

                string clientName = postAmount.Slice(thirdSpace).Trim().ToString();

                records.Add(new ClearingDataRow(egn, amount, reference, clientName, fileRowCounter, currentTraceId));
                runningCount++;
                runningAmount += amount;
            }
            else
            {
                string? trailingLine;
                while ((trailingLine = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(trailingLine))
                    {
                        throw new FormatException($"Malformed transaction row or unexpected data after footer on row {fileRowCounter}.");
                    }
                }

                if (!int.TryParse(lineSpan.Slice(0, firstSpace), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileCount) ||
                    !decimal.TryParse(remaining, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal fileAmount))
                {
                    throw new FormatException($"Invalid footer control values on row {fileRowCounter}.");
                }

                if (runningCount != fileCount || runningAmount != fileAmount)
                {
                    throw new FormatException($"Mathematical control sum mismatch at footer line {fileRowCounter}.");
                }

                return records;
            }
        }

        throw new FormatException("Missing control footer layout line.");
    }
}
