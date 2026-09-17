using System.Diagnostics;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Chuyển nhật ký của thư viện thành tiến trình tổng thể có ETA, dựa trên chuỗi giai đoạn
/// và trọng số trong <see cref="PhaseCatalog"/>.
/// </summary>
public sealed class ProgressTracker
{
    private const int SampleWindow = 16;

    private readonly Stopwatch _stopwatch;
    private readonly IReadOnlyList<BuildPhase> _sequence;
    private readonly double _totalWeight;
    private readonly object _gate = new();
    private readonly Queue<(double Seconds, double Overall)> _samples = new();

    private readonly InnerDataEstimator _innerData = new();

    private int _index = -1;
    private double _phasePercent;
    private bool _complete;
    private double _smoothedRate;
    private string? _throughput;

    public ProgressTracker(Stopwatch stopwatch, IReadOnlyList<BuildPhase> sequence)
    {
        _stopwatch = stopwatch;
        _sequence = sequence;
        _totalWeight = sequence.Sum(p => p.Weight);
    }

    public BuildProgress Current
    {
        get
        {
            lock (_gate)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>Cập nhật từ một dòng nhật ký của thư viện; trả về true nếu tiến trình thay đổi.</summary>
    public bool TryUpdate(string message, out BuildProgress snapshot)
    {
        var stripped = PhaseCatalog.StripTimestamp(message).Trim();
        bool estimated;
        double estimate;
        lock (_gate)
        {
            estimated = _innerData.TryObserve(stripped, out estimate);
        }

        if (!PhaseCatalog.TryParse(message, out var phase, out var percent))
        {
            if (!estimated)
            {
                snapshot = null!;
                return false;
            }

            phase = PhaseCatalog.InnerData;
            percent = estimate;
        }

        lock (_gate)
        {
            Apply(phase, percent);
            var rate = PhaseCatalog.ParseThroughput(message);
            if (rate != null)
            {
                _throughput = rate;
            }

            snapshot = Snapshot();
        }

        return true;
    }

    /// <summary>Chuyển sang một giai đoạn do ứng dụng điều khiển (kiểm tra gói, SHA-256).</summary>
    public BuildProgress EnterPhase(BuildPhase phase)
    {
        lock (_gate)
        {
            Apply(phase, 0);
            return Snapshot();
        }
    }

    /// <summary>Giai đoạn do ứng dụng điều khiển kèm phần trăm và chữ phụ (vd. "đã ghi 1,2 GB" của SDK Sony).</summary>
    public BuildProgress Report(BuildPhase phase, double percent, string? detail)
    {
        lock (_gate)
        {
            Apply(phase, percent);
            if (detail != null && IndexOf(phase) == _index)
            {
                _throughput = detail;
            }

            return Snapshot();
        }
    }

    public BuildProgress UpdatePhasePercent(double percent)
    {
        lock (_gate)
        {
            SetPercent(Math.Clamp(percent, 0, 100));
            return Snapshot();
        }
    }

    public BuildProgress Complete()
    {
        lock (_gate)
        {
            _complete = true;
            _index = _sequence.Count - 1;
            _phasePercent = 100;
            return Snapshot();
        }
    }

    private void Apply(BuildPhase phase, double? percent)
    {
        var target = IndexOf(phase);
        if (target < 0)
        {
            return;
        }

        if (target > _index)
        {
            _index = target;
            _phasePercent = 0;
            _throughput = null;
        }
        else if (target < _index)
        {
            // Dòng của giai đoạn cũ tới muộn — bỏ qua để thanh tiến trình không lùi.
            return;
        }

        if (percent is { } value)
        {
            SetPercent(Math.Max(_phasePercent, Math.Clamp(value, 0, 100)));
        }
    }

    private int IndexOf(BuildPhase phase)
    {
        for (var i = 0; i < _sequence.Count; i++)
        {
            if (_sequence[i].Key == phase.Key)
            {
                return i;
            }
        }

        return -1;
    }

    private void SetPercent(double percent)
    {
        _phasePercent = percent;
        var now = _stopwatch.Elapsed.TotalSeconds;
        var overall = OverallPercent();
        _samples.Enqueue((now, overall));
        while (_samples.Count > SampleWindow)
        {
            _samples.Dequeue();
        }

        if (_samples.Count >= 2)
        {
            var first = _samples.Peek();
            var seconds = now - first.Seconds;
            if (seconds >= 0.5 && overall > first.Overall)
            {
                var rate = (overall - first.Overall) / seconds;
                _smoothedRate = _smoothedRate <= 0 ? rate : _smoothedRate * 0.6 + rate * 0.4;
            }
        }
    }

    private double OverallPercent()
    {
        if (_complete)
        {
            return 100;
        }

        if (_index < 0 || _totalWeight <= 0)
        {
            return 0;
        }

        double done = 0;
        for (var i = 0; i < _index; i++)
        {
            done += _sequence[i].Weight;
        }

        done += _sequence[_index].Weight * _phasePercent / 100.0;
        return Math.Clamp(done * 100.0 / _totalWeight, 0, 100);
    }

    private TimeSpan? EstimateRemaining(double overall)
    {
        if (_complete)
        {
            return TimeSpan.Zero;
        }

        if (_smoothedRate <= 0 || overall <= 0)
        {
            return null;
        }

        var seconds = (100 - overall) / _smoothedRate;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.FromDays(7).TotalSeconds));
    }

    private BuildProgress Snapshot()
    {
        var overall = OverallPercent();
        var phase = _complete ? Localization.Loc.T("Phase.Done") : _index < 0 ? Localization.Loc.T("Phase.Preparing") : _sequence[_index].Name;
        return new BuildProgress(
            phase,
            _complete ? 100 : _phasePercent,
            overall,
            _stopwatch.Elapsed,
            EstimateRemaining(overall),
            Math.Max(0, _index + 1),
            _complete,
            _complete ? null : _throughput);
    }
}
