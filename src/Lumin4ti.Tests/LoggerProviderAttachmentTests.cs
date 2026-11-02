using Lumin4ti.Core.Services;
using Microsoft.Extensions.Logging;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class LoggerProviderAttachmentTests
{
    [TestMethod]
    public void 既存ログを維持してデバッグ出力だけを解除できる()
    {
        var original = new RecordingProvider();
        var debugger = new RecordingProvider();
        using (var factory = LoggerFactory.Create(_ => { }))
        {
            factory.AddProvider(original);
            var log = factory.CreateLogger("Lumin4ti.Test");
            log.LogInformation("before");
            var subscription = LoggerBootstrap.AttachProvider(factory, debugger);
            log.LogInformation("during");
            subscription.Dispose();
            log.LogInformation("after");
            CollectionAssert.AreEqual(new[] { "before", "during", "after" }, original.Messages);
            CollectionAssert.AreEqual(new[] { "during" }, debugger.Messages);
            Assert.AreEqual(1, debugger.DisposeCount);
        }
        Assert.AreEqual(1, debugger.DisposeCount);
        Assert.AreEqual(1, original.DisposeCount);
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public int DisposeCount { get; private set; }
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);
        public void Dispose() => DisposeCount++;

        private sealed class RecordingLogger(RecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Messages.Add(formatter(state, exception));
        }
    }
}
