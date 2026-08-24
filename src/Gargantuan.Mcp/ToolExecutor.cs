using System.Text.Json;
using Gargantuan.Mcp.Contracts;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Gargantuan.Mcp.Server;

public sealed record ToolError(
    string Code,
    string Message,
    ScriptConflictDetails? Details = null,
    ScriptCommitState? CommitState = null);
public sealed record ToolResponse<T>(bool Success, T? Result, ToolError? Error)
{
    public static ToolResponse<T> Ok(T Result) => new(true, Result, null);
    public static ToolResponse<T> Fail(
        GargantuanErrorCode Code,
        string Message,
        ScriptConflictDetails? Details = null,
        ScriptCommitState? CommitState = null) =>
        new(false, default, new(Code.ToString(), Message, Details, CommitState));
}

public sealed class ToolExecutor(ILogger<ToolExecutor> Logger)
{
    private readonly SemaphoreSlim Concurrency = new(McpLimits.MaximumConcurrentRequests, McpLimits.MaximumConcurrentRequests);

    public async Task<ToolResponse<T>> ExecuteAsync<T>(Func<CancellationToken, Task<T>> Operation, CancellationToken CancellationToken)
    {
        await Concurrency.WaitAsync(CancellationToken).ConfigureAwait(false);
        try
        {
            T Result = await Operation(CancellationToken).ConfigureAwait(false);
            int Bytes = JsonSerializer.SerializeToUtf8Bytes(Result).Length;
            return Bytes > McpLimits.MaximumResponseBytes
                ? ToolResponse<T>.Fail(GargantuanErrorCode.ResourceLimit, "The bounded response size was exceeded.")
                : ToolResponse<T>.Ok(Result);
        }
        catch (GargantuanAdapterException Exception)
        {
            return ToolResponse<T>.Fail(
                Exception.Code,
                Bound(Exception.SafeMessage),
                Exception.ConflictDetails,
                Exception.CommitState);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception Exception)
        {
            Logger.LogError(Exception, "[MCP:ToolBoundary] Adapter operation failed");
            return ToolResponse<T>.Fail(GargantuanErrorCode.InternalError, "The adapter operation failed.");
        }
        finally
        {
            Concurrency.Release();
        }
    }

    private static string Bound(string Message) => Message.Length <= 512 ? Message : Message[..512];

    public static CallToolResult ToMcpResult<T>(ToolResponse<T> Response)
    {
        JsonElement StructuredContent = JsonSerializer.SerializeToElement(Response);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = StructuredContent.GetRawText() }],
            StructuredContent = StructuredContent,
            IsError = !Response.Success,
        };
    }
}
