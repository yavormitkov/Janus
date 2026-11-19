using System;
using System.ComponentModel.DataAnnotations;

namespace Janus.Core.Shared.DTOs;

public sealed record ProcessedFileDto(
    Guid FileId,
    string TraceId,
    string FileName,
    DateTime ProcessedAt
);
