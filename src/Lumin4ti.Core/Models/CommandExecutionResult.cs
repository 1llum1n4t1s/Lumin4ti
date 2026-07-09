namespace Lumin4ti.Core.Models;

public sealed record CommandExecutionResult(
    bool Success,
    string CommandLine,
    int ExitCode,
    string StandardOutput,
    string StandardError);
