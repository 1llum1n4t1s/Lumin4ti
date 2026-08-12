using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>
    /// <see cref="Current"/> の可変コレクション (除外リスト等) を読み書きする間に取る排他ロック。
    /// 保存側もシリアライズ中はこのロックを取るため、チェックの連続操作と保存が重なっても
    /// 「列挙中に変更された」で保存が落ちたり、書きかけの内容が出力されたりしない。
    /// </summary>
    object SyncRoot { get; }

    Task SaveAsync(CancellationToken ct = default);

    /// <summary>この時点までに要求された保存がすべて完了するまで待機する。</summary>
    Task FlushAsync(CancellationToken ct = default);
}
