# 未接続 PnP デバイスの削除

[security.md](security.md) から分割した詳細。

[WindowsDisconnectedDeviceService](../../src/Lumin4ti.Core/Services/Windows/WindowsDisconnectedDeviceService.cs) は SetupAPI で接続中 ID とインストール済み ID の差を列挙し、ソフトウェア／仮想デバイスを含む未接続デバイスを UI へ返す。利用者が個別または全選択した項目だけを対象にし、削除直前にも再接続を確認してから `DiUninstallDevice` を呼ぶ。接続中デバイスと空の Instance ID は対象にしない。

専用 UI は [DeviceCleanupViewModel](../../src/Lumin4ti.UI/ViewModels/DeviceCleanupViewModel.cs) が個別／全選択、二段階確認、削除結果の集計を担当し、`MaintenanceOperationCoordinator` の排他制御へ参加する。検証は [DisconnectedDeviceTests](../../src/Lumin4ti.Tests/DisconnectedDeviceTests.cs) とモックサービスで行い、実機デバイスを削除して確認しない。
