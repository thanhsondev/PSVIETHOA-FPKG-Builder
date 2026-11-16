using Avalonia.Controls;
using Avalonia.Threading;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>
/// Nhịp chung cho mọi lớp Giáng sinh: MỘT bộ hẹn giờ 25 nhịp/giây (12,5 khi app ở nền). Mỗi lớp chạy ở một ước số của nhịp này (tuyết
/// và xe tuần lộc mỗi nhịp, dây đèn / quả châu 1/2, làng tuyết / cảnh trong khung 1/3) nên các lớp đổi hình CÙNG một khung vẽ — Avalonia
/// chỉ dựng lại cửa sổ 25 lần/giây thay vì cộng dồn nhịp lệch nhau của từng lớp (đỡ CPU đáng kể vì tuyết phủ cả cửa sổ).
/// Không còn ai đăng ký thì tự dừng.
/// </summary>
internal static class XmasTicker
{
    public const int Rate = 25;

    private sealed class Entry
    {
        public required Action Tick;
        public required int Divisor;
    }

    private static readonly List<Entry> entries = [];
    private static DispatcherTimer? timer;
    private static Window? owner;
    private static long frame;

    /// <summary>Cửa sổ chính: theo dõi đang dùng / ở nền để đổi nhịp.</summary>
    public static void Attach(Window window)
    {
        if (ReferenceEquals(owner, window))
        {
            return;
        }

        owner = window;
        window.Activated += (_, _) => UpdateInterval();
        window.Deactivated += (_, _) => UpdateInterval();
        UpdateInterval();
    }

    /// <summary>Số nhịp chung giữa hai lần cập nhật cho một lớp muốn chạy <paramref name="fps"/> hình/giây.</summary>
    public static int DivisorFor(int fps) => Math.Max(1, (int)Math.Round(Rate / (double)Math.Max(1, fps)));

    public static IDisposable Subscribe(Action tick, int divisor)
    {
        var entry = new Entry { Tick = tick, Divisor = Math.Max(1, divisor) };
        entries.Add(entry);
        EnsureRunning();
        return new Subscription(entry);
    }

    private sealed class Subscription(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;

        public void Dispose()
        {
            if (_entry == null)
            {
                return;
            }

            entries.Remove(_entry);
            _entry = null;
            if (entries.Count == 0)
            {
                timer?.Stop();
            }
        }
    }

    private static void EnsureRunning()
    {
        if (timer == null)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Tick += (_, _) => Step();
            UpdateInterval();
        }

        if (!timer.IsEnabled && entries.Count > 0)
        {
            timer.Start();
        }
    }

    private static void UpdateInterval()
    {
        if (timer == null)
        {
            return;
        }

        bool active = owner?.IsActive ?? true;
        var interval = TimeSpan.FromMilliseconds(1000.0 / (active ? Rate : Rate / 2.0));
        if (timer.Interval != interval)
        {
            timer.Interval = interval;
        }
    }

    private static void Step()
    {
        frame++;
        // Chép ra mảng: một lớp có thể huỷ đăng ký ngay trong lúc chạy.
        foreach (var e in entries.ToArray())
        {
            if (frame % e.Divisor == 0)
            {
                e.Tick();
            }
        }
    }
}
