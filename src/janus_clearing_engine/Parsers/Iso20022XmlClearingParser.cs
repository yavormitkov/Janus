using Janus.Core.Shared.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Janus.ClearingEngine.Parsers;

public sealed class Iso20022XmlClearingParser : IClearingParser
{
    public List<ClearingDataRow> ParseStreamAndValidateFooter(StreamReader reader, string currentTraceId)
    {
        var records = new List<ClearingDataRow>();
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int runningCount = 0;
        decimal runningAmount = 0.0m;
        int fileRowCounter = 0;
        int? expectedCount = null;
        decimal? expectedAmount = null;

        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit
        };

        try
        {
            using var xmlReader = XmlReader.Create(reader, settings);
            while (xmlReader.Read())
            {
                if (xmlReader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (xmlReader.Name == "Transaction")
                {
                    fileRowCounter++;

                    string egn = xmlReader.GetAttribute("Egn")
                        ?? throw new FormatException($"Missing Egn attribute on transaction row {fileRowCounter}.");

                    string amountRaw = xmlReader.GetAttribute("Amount")
                        ?? throw new FormatException($"Missing Amount attribute on transaction row {fileRowCounter}.");

                    string reference = xmlReader.GetAttribute("Reference")
                        ?? throw new FormatException($"Missing Reference attribute on transaction row {fileRowCounter}.");

                    if (!seenReferences.Add(reference))
                    {
                        throw new FormatException($"Intra-file duplicate reference '{reference}' detected on transaction row {fileRowCounter}.");
                    }

                    string? clientName = xmlReader.GetAttribute("ClientName");

                    if (!decimal.TryParse(amountRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount) || amount <= 0.0m)
                    {
                        throw new FormatException($"Invalid amount '{amountRaw}' on transaction row {fileRowCounter}.");
                    }

                    records.Add(new ClearingDataRow(egn, amount, reference, clientName, fileRowCounter, currentTraceId));
                    runningCount++;
                    runningAmount += amount;
                }
                else if (xmlReader.Name == "BatchControl")
                {
                    string countRaw = xmlReader.GetAttribute("RecordCount")
                        ?? throw new FormatException("Missing RecordCount attribute in BatchControl footer.");

                    string totalRaw = xmlReader.GetAttribute("TotalAmount")
                        ?? throw new FormatException("Missing TotalAmount attribute in BatchControl footer.");

                    if (!int.TryParse(countRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount) ||
                        !decimal.TryParse(totalRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsedAmount))
                    {
                        throw new FormatException("Malformed control totals in BatchControl footer.");
                    }

                    expectedCount = parsedCount;
                    expectedAmount = parsedAmount;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FormatException($"Structural XML layout breach: {ex.Message}", ex);
        }

        if (expectedCount is null || expectedAmount is null)
        {
            throw new FormatException("Missing BatchControl footer element.");
        }

        if (runningCount != expectedCount.Value || runningAmount != expectedAmount.Value)
        {
            throw new FormatException($"Mathematical control sum mismatch in XML footer. Expected {expectedCount} rows / {expectedAmount}, got {runningCount} rows / {runningAmount}.");
        }

        return records;
    }
}
