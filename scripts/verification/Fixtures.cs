using System.Text;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;

namespace Lumin4ti.Verification;

internal sealed class MemoryBackups : IRegistryBackupStorage
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ThrowAfterNextSharedWrite { get; set; }
    public bool ThrowBeforeNextSharedWrite { get; set; }
    public bool FailNextUserDelete { get; set; }
    public bool ThrowAfterNextNewUserWrite { get; set; }
    public Action? AfterNewWrite { get; set; }
    public bool FileExists(string path) => Files.ContainsKey(path);
    public string ReadAllText(string path) => Files[path];
    public void Delete(string path)
    {
        if (FailNextUserDelete && path.Split(Path.DirectorySeparatorChar).Length == 3)
        { FailNextUserDelete = false; throw new IOException("RERE_CANARY user journal deletion denied"); }
        Files.Remove(path);
    }
    public void WriteNewAtomically(string path, Action<Stream> write)
    {
        if (FileExists(path)) throw new IOException("既存原本を上書きできません");
        WriteAtomically(path, write);
        AfterNewWrite?.Invoke();
        if (ThrowAfterNextNewUserWrite && path.Split(Path.DirectorySeparatorChar).Length == 3)
        {
            ThrowAfterNextNewUserWrite = false;
            throw new IOException("RERE_CANARY new user original saved but validation failed");
        }
    }
    public void WriteAtomically(string path, Action<Stream> write)
    {
        if (ThrowBeforeNextSharedWrite && path.Split(Path.DirectorySeparatorChar).Length == 2)
        { ThrowBeforeNextSharedWrite = false; throw new IOException("RERE_CANARY shared write not reached"); }
        using var stream = new MemoryStream();
        write(stream);
        Files[path] = Encoding.UTF8.GetString(stream.ToArray());
        if (ThrowAfterNextSharedWrite && path.Split(Path.DirectorySeparatorChar).Length == 2)
        {
            ThrowAfterNextSharedWrite = false;
            throw new IOException("RERE_CANARY atomic replace succeeded but validation failed");
        }
    }
    public IReadOnlyList<string> EnumerateUserBackupPaths(string id) => Files.Keys
        .Where(path => path.Split(Path.DirectorySeparatorChar).Length == 3 && Path.GetFileName(path) == id + ".json").ToArray();
}

internal sealed class MemoryRegistry(Dictionary<string, RegistryValueSnapshot> values, string sid) : IRegistryValueAccessor
{
    public bool FailNextWrite { get; set; }
    public HashSet<int> FailAtWrite { get; } = [];
    public int WriteAttempts { get; private set; }
    private string Location(RegistryToggleSpec spec) => $"{(spec.Hive == RegistryHive.CurrentUser ? sid : "machine")}/{spec.KeyPath}/{spec.Name}";
    public RegistryValueSnapshot Read(RegistryToggleSpec spec) => values.GetValueOrDefault(Location(spec)) ?? RegistryValueSnapshot.Missing();
    public void Write(RegistryToggleSpec spec, RegistryValueSnapshot value)
    {
        WriteAttempts++;
        if (FailAtWrite.Remove(WriteAttempts)) throw new IOException("GOGO_CANARY injected write failure");
        if (FailNextWrite) { FailNextWrite = false; throw new IOException("RERE_CANARY restore failure"); }
        values[Location(spec)] = value;
    }
}

internal sealed class MemoryUwpSettings : IUwpBackgroundSettingsStore
{
    public Dictionary<string, UwpBackgroundValues> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int FailPairs { get; set; }
    public bool FailBeforeFirstValue { get; set; }
    public UwpBackgroundValues? ExternalOnFailure { get; set; }
    public int WriteCalls { get; private set; }
    public IReadOnlyDictionary<string, UwpBackgroundValues> ReadMany(IReadOnlyList<string> families, CancellationToken ct) =>
        families.ToDictionary(family => family, family => Values.GetValueOrDefault(family), StringComparer.OrdinalIgnoreCase);
    public void WriteMany(IReadOnlyList<KeyValuePair<string, UwpBackgroundValues>> values, CancellationToken ct)
    {
        foreach (var (family, target) in values)
        {
            WriteCalls++;
            if (FailPairs > 0 && FailBeforeFirstValue) { FailPairs--; throw new IOException("GOGO_CANARY compensation denied"); }
            Values[family] = Values.GetValueOrDefault(family) with { Disabled = target.Disabled };
            if (FailPairs > 0)
            {
                FailPairs--;
                if (ExternalOnFailure is { } external) Values[family] = external;
                // 複数回注入では最初の部分更新の後、補償は一値目より前で失敗させる。
                if (FailPairs > 0) FailBeforeFirstValue = true;
                throw new IOException("GOGO_CANARY second DWORD write denied");
            }
            Values[family] = target;
        }
    }
}

internal sealed class MemoryLogProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new MemoryLogger(Messages);
    public void Dispose() { }
    private sealed class MemoryLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NoopLease();
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}

internal sealed class MockExecutor(Func<string, string, CancellationToken, CommandExecutionResult> execute) : ICommandExecutor
{
    public List<(string File, string Arguments, bool Cancelable)> Calls { get; } = [];
    public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default,
        IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
    {
        Calls.Add((fileName, arguments, ct.CanBeCanceled));
        return Task.FromResult(execute(fileName, arguments, ct));
    }
    public static CommandExecutionResult Result(bool success = true, string stdout = "", string stderr = "") =>
        new(success, "mock OS boundary", success ? 0 : 5, stdout, stderr);
}

internal sealed class MockDevices : IDisconnectedDeviceService
{
    public bool FailEnumeration { get; set; } = true;
    public int EnumerationCalls { get; private set; }
    public Task<IReadOnlyList<DisconnectedDevice>> GetDisconnectedDevicesAsync(CancellationToken ct = default)
    {
        EnumerationCalls++;
        if (FailEnumeration) throw new IOException("RERE_CANARY enumeration failure");
        return Task.FromResult<IReadOnlyList<DisconnectedDevice>>([]);
    }
    public Task<DisconnectedDeviceRemovalResult> RemoveDisconnectedDeviceAsync(string instanceId, CancellationToken ct = default) =>
        throw new InvalidOperationException("このフローで削除を呼んではいけません");
}

internal sealed class AsyncMockExecutor(Func<int, Task<CommandExecutionResult>> execute) : ICommandExecutor
{
    public int Calls { get; private set; }
    public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default,
        IProgress<string>? onOutputLine = null, TimeSpan? timeout = null) => execute(++Calls);
}

internal sealed class NoopLease : IDisposable
{
    public void Dispose() { }
}

internal sealed class MemorySettings : ISettingsService
{
    public AppSettings Current { get; } = new();
    public object SyncRoot { get; } = new();
    public int SaveCount { get; private set; }
    public bool FailSave { get; set; }
    public Task SaveAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return FailSave ? Task.FromException(new IOException("OPOP_CANARY settings write denied")) : Task.CompletedTask;
    }
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class MockHttpHandler(bool blocked, bool ignoreCancellation = false) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = blocked ? new StreamContent(new BlockedBody(ignoreCancellation)) : new ByteArrayContent(Encoding.UTF8.GetBytes("RERE_CANARY")) });
}

internal sealed class BlockedBody(bool ignoreCancellation = false) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("同期読み込みは禁止");
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.InfiniteTimeSpan, ignoreCancellation ? CancellationToken.None : cancellationToken); return 0; }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { await Task.Delay(Timeout.InfiniteTimeSpan, ignoreCancellation ? CancellationToken.None : cancellationToken); return 0; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
