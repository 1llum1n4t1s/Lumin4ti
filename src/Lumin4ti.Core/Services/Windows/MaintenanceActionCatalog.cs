using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>
/// 全メンテナンス項目のカタログ。並び順が画面の表示順になる。
/// ON/OFF が定義できる設定は IMaintenanceToggle (ON = 最適化適用 / OFF = Windows 既定)、
/// 1 回実行型は IMaintenanceAction。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class MaintenanceActionCatalog
{
    public IReadOnlyList<IMaintenanceItem> Items { get; }

    public MaintenanceActionCatalog(ICommandExecutor executor)
    {
        Items =
        [
            // ═══ 更新・セキュリティ ═══
            new WingetUpgradeAction(executor),
            new DefenderSignatureUpdateAction(executor),
            new DefenderResetAction(executor),
            new SecurityAppResetAction(executor),

            // ═══ クリーンアップ・修復 ═══
            new ComponentStoreCleanupAction(executor),
            new ShellFolderRepairAction(executor),
            new TrayIconResetAction(),
            new BrokenStartupCleanupAction(),
            new DeadAssociationCleanupAction(),
            new EventLogClearAction(),
            new StoreCacheResetAction(executor),
            new TrimOptimizeAction(executor),

            // ═══ パフォーマンス ═══
            // MMAgent (メモリ管理) は機能ごとに個別トグル。ON = 機能有効 / OFF = 機能無効 (推奨値は各説明を参照)
            .. MmAgentFeatureToggle.CreateAll(executor),
            new PagingExecutiveResetAction(),
            new VirtualizationSecurityToggle(executor),
            new RegistryToggle(
                id: "svchost-split-threshold",
                label: "SvcHost プロセスの統合 (分割抑制)",
                description:
                    "Windows は既定で svchost.exe を機能ごとに細かく分割起動します (RAM 3.5GB 超の PC)。この設定を ON にすると分割の閾値を最大化し、グループ化可能なサービスを少数のプロセスに統合してプロセス数とメモリ使用量 (数十〜百数十 MB) を削減します。" +
                    "セキュリティ境界扱いのサービス (DiagTrack / Dnscache 等) は統合されません。OFF にすると閾値を削除して既定の分割動作に戻ります。",
                category: CommandCategory.Performance,
                specs: [new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", RegistryValueKind.DWord, 0xFFFFFFFF)],
                requiresReboot: true),
            new UwpBackgroundToggle(),
            new RegistryToggle(
                id: "edge-background-off",
                label: "Edge のバックグラウンド常駐を無効化 (メモリ節約 100〜300MB)",
                description:
                    "Microsoft Edge を閉じた後もバックグラウンドで動き続ける常駐機能と、サインイン時に先読み起動する Startup Boost をポリシーで無効化します。" +
                    "Edge を常用しない PC なら 100〜300MB 程度のメモリ節約になります。Edge の起動が少し遅くなる・拡張機能の通知が届かなくなるのがトレードオフです。OFF でポリシーを削除して Edge 既定に戻ります。",
                category: CommandCategory.Performance,
                specs:
                [
                    new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", RegistryValueKind.DWord, 0),
                    new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", RegistryValueKind.DWord, 0),
                ]),
            new RecallToggle(executor),
            new RegistryToggle(
                id: "power-throttling-off",
                label: "電力スロットリングを無効化",
                description:
                    "Windows がバックグラウンドのプロセスの CPU クロックを自動的に落とす省電力機能 (Power Throttling) を無効化します。" +
                    "録画・エンコード・ビルドなどのバックグラウンド処理が遅くなる現象を防げます。常時フル性能で動くためノート PC ではバッテリー消費が増えます。OFF で既定の省電力動作に戻ります。",
                category: CommandCategory.Performance,
                specs: [new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", RegistryValueKind.DWord, 1)],
                requiresReboot: true),
            new RegistryToggle(
                id: "mpo-off",
                label: "MPO (マルチプレーンオーバーレイ) を無効化",
                description:
                    "GPU が複数の描画面を合成するマルチプレーンオーバーレイ (MPO) を無効化します (OverlayTestMode=5)。" +
                    "MPO はドライバとの相性で画面のちらつき・一瞬のブラックアウト・動画再生時のスタッターの原因になることがあり、症状がある場合の定番対処です。症状が無いなら OFF (既定) のままで問題ありません。",
                category: CommandCategory.Performance,
                specs: [new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", RegistryValueKind.DWord, 5)],
                requiresReboot: true),
            new RegistryToggle(
                id: "audio-priority",
                label: "オーディオ処理の優先度を引き上げ",
                description:
                    "マルチメディアクラススケジューラの再生 (Playback) / 録音 (Capture) タスクの優先度を最高値 (6) に引き上げ、高負荷時の音声の途切れ・プチノイズを抑えます。" +
                    "OFF で優先度設定を削除して Windows 既定に戻ります。",
                category: CommandCategory.Performance,
                specs:
                [
                    new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Playback", "Priority", RegistryValueKind.DWord, 6),
                    new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Capture", "Priority", RegistryValueKind.DWord, 6),
                ]),
            new RegistryToggle(
                id: "rdp-framerate",
                label: "リモートデスクトップを 60FPS 化",
                description:
                    "リモートデスクトップ接続の描画フレーム間隔を短縮し (DWMFRAMEINTERVAL=15)、既定の約 30FPS から 60FPS 相当に引き上げます。" +
                    "この PC に「接続される側」として効く設定です。回線帯域の消費は増えます。OFF で既定のフレームレートに戻ります。",
                category: CommandCategory.Performance,
                specs: [new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations", "DWMFRAMEINTERVAL", RegistryValueKind.DWord, 15)],
                requiresReboot: true),
            new RegistryToggle(
                id: "startup-speedup",
                label: "スタートアップアプリの起動遅延を解除",
                description:
                    "ログオン後、Windows がデスクトップ描画を優先するためにスタートアップアプリの起動を数秒〜十数秒遅らせる仕組み (アイドル待ち + 遅延タイマー) を無効化します。" +
                    "常駐ツールをすぐ使い始めたい場合に有効です。ログオン直後の体感が少し重くなることがあります。OFF で既定の遅延動作に戻ります。",
                category: CommandCategory.Performance,
                specs:
                [
                    new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "WaitForIdleState", RegistryValueKind.DWord, 0),
                    new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", RegistryValueKind.DWord, 0),
                ]),
            new NvidiaMsiFixAction(),

            // ═══ システム設定 ═══
            new HibernateToggle(executor),
            new RegistryToggle(
                id: "mouse-accel-off",
                label: "マウス加速 (ポインターの精度を高める) を無効化",
                description:
                    "マウスを動かす速さでカーソルの移動距離が変わる「ポインターの精度を高める」(マウス加速) を無効化し、手の動きとカーソル移動を 1:1 に固定します。" +
                    "FPS などのゲームでエイムを安定させる定番設定です。OFF で Windows 既定 (加速あり) に戻ります。反映には再サインインが必要な場合があります。",
                category: CommandCategory.System,
                specs:
                [
                    new(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", RegistryValueKind.String, "0", "1"),
                    new(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", RegistryValueKind.String, "0", "6"),
                    new(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", RegistryValueKind.String, "0", "10"),
                ]),
            new RegistryToggle(
                id: "sticky-keys-off",
                label: "固定キーなどのアクセシビリティ ホットキーを無効化",
                description:
                    "Shift キー 5 連打で固定キーが有効になる等、アクセシビリティ機能が誤発動するホットキーを無効化します (固定キー / 切り替えキー / フィルターキー)。" +
                    "ゲーム中の Shift 連打でダイアログが出て中断される事故を防げます。OFF で Windows 既定 (ホットキー有効) に戻ります。",
                category: CommandCategory.System,
                specs:
                [
                    new(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", RegistryValueKind.String, "506", "510"),
                    new(RegistryHive.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", RegistryValueKind.String, "58", "62"),
                    new(RegistryHive.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", RegistryValueKind.String, "122", "126"),
                ]),
            new RegistryToggle(
                id: "menu-delay-zero",
                label: "メニュー表示の遅延を短縮",
                description:
                    "右クリックメニューやスタートメニューのサブメニューが開くまでの待ち時間を、既定の 400 ミリ秒から 0 に短縮して UI の体感を機敏にします。" +
                    "OFF で既定の 400 ミリ秒に戻ります。反映には再サインイン (またはエクスプローラー再起動) が必要です。",
                category: CommandCategory.System,
                specs: [new(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", RegistryValueKind.String, "0", "400")],
                affectsExplorer: true),
            new RegistryToggle(
                id: "folder-template-general",
                label: "フォルダ表示テンプレートを汎用に固定",
                description:
                    "エクスプローラーがフォルダの中身から表示形式 (写真 / 音楽 / ドキュメント等) を自動判別する機能を止め、全フォルダを汎用 (General Items) に統一します。" +
                    "大量のファイルがあるフォルダで自動判別による表示の遅さ (緑のプログレスバー) を解消できます。OFF で自動判別に戻ります。",
                category: CommandCategory.System,
                specs: [new(RegistryHive.CurrentUser, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", RegistryValueKind.String, "NotSpecified")],
                affectsExplorer: true),
            new RegistryToggle(
                id: "ceip-off",
                label: "CEIP / フィードバック通知を無効化",
                description:
                    "カスタマー エクスペリエンス向上プログラム (CEIP: 利用状況を Microsoft へ送信する仕組み) と、「Windows についてのフィードバックをお寄せください」通知を無効化します。" +
                    "送信処理のバックグラウンド負荷とプライバシーへの配慮の両面で定番の設定です。OFF でポリシーを削除して既定に戻ります。",
                category: CommandCategory.System,
                specs:
                [
                    new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", RegistryValueKind.DWord, 0),
                    new(RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", RegistryValueKind.DWord, 0),
                ]),
            new GameDvrResetAction(),
            new NvmeDriverRevertAction(),
            new NtpConfigAction(executor),

            // ═══ 整理・ソート ═══
            new EnvPathSortAction(),
        ];
    }
}
