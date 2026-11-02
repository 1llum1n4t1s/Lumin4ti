#if DEBUG
using System;
using Avalonia.Threading;
using CRDebugger.Avalonia;
using CRDebugger.Core;
using Lumin4ti.Core.Services;
using Microsoft.Extensions.Logging;
using DebugPanel = CRDebugger.Core.CRDebugger;

namespace Lumin4ti.UI.Services;

/// <summary>デスクトップ UI の寿命内だけデバッグ画面とログ転送を所有する。</summary>
public sealed class DebuggerService : IDisposable
{
    private readonly Func<ILoggerProvider, IDisposable> _attachProvider;
    private IDisposable? _logging;
    private bool _initialized;
    private bool _disposed;

    public DebuggerService() : this(LoggerBootstrap.AttachProvider) { }

    // プレビューではアプリのログ設定を変更せず、独立したログ出力を接続できる。
    internal DebuggerService(Func<ILoggerProvider, IDisposable> attachProvider) => _attachProvider = attachProvider;

    public void Initialize()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_initialized || _disposed) return;
        try
        {
            var options = new CRDebuggerOptions
            {
                MaxLogEntries = 2000,
                SystemInfoCollectionLevel = SystemInfoCollectionLevel.Minimal,
                RequireOptInAttribute = true,
                CaptureTraceOutput = false,
                CaptureUnhandledExceptions = false,
            };
            options.DisabledTabs.Add(CRTab.Options);
            options.DisabledTabs.Add(CRTab.BugReporter);
            options.UseAvalonia();
            DebugPanel.Initialize(options);
            _initialized = true;
            _logging = _attachProvider(DebugPanel.CreateLoggerProvider());
            if (LoggerBootstrap.InitializationFailure is { } failure)
                LoggerBootstrap.Log.Error($"ファイルログの初期化に失敗しているため、デバッグ画面で確認してください: {failure}");
        }
        catch (Exception ex)
        {
            Release();
            LoggerBootstrap.Log.Error("デバッグ画面の初期化に失敗しました", ex);
        }
    }

    public bool Show()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return false;
        Initialize();
        if (!_initialized) return false;
        try
        {
            DebugPanel.Show();
            return true;
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("デバッグ画面を開けませんでした", ex);
            return false;
        }
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private void Release()
    {
        try
        {
            _logging?.Dispose();
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("デバッグ画面へのログ転送の終了に失敗しました", ex);
        }
        finally
        {
            _logging = null;
            if (_initialized)
            {
                _initialized = false;
                try { DebugPanel.Shutdown(); }
                catch (Exception ex) { LoggerBootstrap.Log.Error("デバッグ画面の終了に失敗しました", ex); }
            }
        }
    }
}

#endif
