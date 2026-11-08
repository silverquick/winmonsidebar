using System.Threading;

namespace Monitor.App.Settings;

/// <summary>
/// 設定の書き込みを debounce し、バックグラウンド（ThreadPool のタイマー）で行う。
/// 受け取るのは呼び出し元でシリアライズ済みの JSON 文字列だけで、<see cref="AppSettings"/> 自体は
/// スレッド間で共有しない。短時間に連続した要求は最後の内容だけが 1 回書き込まれる。
/// </summary>
internal sealed class DebouncedSettingsWriter : IDisposable
{
    // _gate は保留中の内容とタイマーの状態を守る。_writeGate は「取り出し→書き込み」を直列化し、
    // 古い内容の書き込みが新しい内容の書き込みより後に終わって上書きしてしまうことを防ぐ。
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Action<string> _write;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private string? _pendingJson;
    private bool _disposed;

    /// <param name="write">JSON を永続化する処理。例外を投げても握りつぶす。</param>
    /// <param name="delay">最後の要求からこの時間だけ新しい要求が来なければ書き込む。</param>
    public DebouncedSettingsWriter(Action<string> write, TimeSpan delay)
    {
        _write = write;
        _delay = delay;
        _timer = new Timer(static state => ((DebouncedSettingsWriter)state!).Flush(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>書き込みを予約する。既に保留中の内容があれば置き換え、待ち時間を延長する。</summary>
    public void Schedule(string json)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingJson = json;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>保留中の予約を破棄して、指定の内容を呼び出し元スレッドで即座に書き込む。</summary>
    public void WriteNow(string json)
    {
        lock (_writeGate)
        {
            lock (_gate)
            {
                _pendingJson = null;
                if (!_disposed)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }

            WriteSafe(json);
        }
    }

    /// <summary>保留中の内容があれば呼び出し元スレッドで同期的に書き込む。無ければ何もしない。</summary>
    public void Flush()
    {
        lock (_writeGate)
        {
            string? json;
            lock (_gate)
            {
                json = _pendingJson;
                _pendingJson = null;
                if (!_disposed)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }

            if (json is not null)
            {
                WriteSafe(json);
            }
        }
    }

    /// <summary>保留中の内容を書き出してからタイマーを止める。</summary>
    public void Dispose()
    {
        Flush();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }
    }

    private void WriteSafe(string json)
    {
        try
        {
            _write(json);
        }
        catch
        {
            // 保存失敗はアプリの継続動作を妨げない。
        }
    }
}
