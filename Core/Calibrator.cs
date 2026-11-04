// ==================================================
// Calibrator.cs - ファン自動キャリブレーション (Duty と回転数の関係を段階的に測定する状態機械)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 手順:
///   1. 100% で 6 秒回して最大回転数を測る
///   2. 100% → 0% へ段階的に下げ、各段で回転数を測る（止まる Duty = StopDuty を見つける）
///   3. 0% から 5% ずつ上げ、回り始める Duty = StartDuty を見つける
/// 1 段ごとに 4 秒保持し、後半 2 秒の平均を測定値とする。FanEngine のスレッドから毎周期 Step() を呼ぶ。
/// </summary>
public sealed class Calibrator
{
    private static readonly double[] DownSteps = { 100, 90, 80, 70, 60, 50, 40, 35, 30, 25, 20, 15, 10, 5, 0 };
    private const double SpinThresholdRpm = 50;
    private static readonly TimeSpan SpinUpTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);
    private const double MaxStartSearchDuty = 60;

    private enum Phase { SpinUp, Down, Up, Done }

    private readonly List<CalibrationPoint> _points = new();
    private readonly List<double> _samples = new();
    private Phase _phase = Phase.SpinUp;
    private int _downIndex;
    private double _upDuty;
    private DateTime _stepStarted;
    private double _maxRpm;
    private double? _stopDuty;
    private double? _startDuty;

    public string ControlId { get; }
    public string FanName { get; }
    public bool IsDone => _phase == Phase.Done;
    public string? FailureReason { get; private set; }

    /// <summary>現在出力すべき Duty</summary>
    public double CurrentDuty => _phase switch
    {
        Phase.SpinUp => 100,
        Phase.Down => DownSteps[_downIndex],
        Phase.Up => _upDuty,
        _ => 100,
    };

    public double Progress => _phase switch
    {
        Phase.SpinUp => 0.02,
        Phase.Down => 0.05 + 0.7 * _downIndex / DownSteps.Length,
        Phase.Up => 0.75 + 0.25 * Math.Min(1, _upDuty / MaxStartSearchDuty),
        _ => 1,
    };

    public string StatusText => _phase switch
    {
        Phase.SpinUp => "最大回転数を測定中…",
        Phase.Down => $"回転を下げながら測定中… ({DownSteps[_downIndex]:F0}%)",
        Phase.Up => $"回り始める回転数を探しています… ({_upDuty:F0}%)",
        _ => "完了",
    };

    public Calibrator(string controlId, string fanName, DateTime now)
    {
        ControlId = controlId;
        FanName = fanName;
        _stepStarted = now;
    }

    /// <summary>1 周期分進める。rpm が取得できない場合は中止する</summary>
    public void Step(double? rpm, DateTime now)
    {
        if (IsDone) return;
        if (rpm is null)
        {
            Fail("回転数センサーが読めません。");
            return;
        }

        var elapsed = now - _stepStarted;
        if (elapsed >= SettleTime) _samples.Add(rpm.Value);

        switch (_phase)
        {
            case Phase.SpinUp when elapsed >= SpinUpTime:
                _maxRpm = Average();
                if (_maxRpm < SpinThresholdRpm)
                {
                    Fail("100% でも回転していません。配線を確認してください。");
                    return;
                }
                _points.Add(new CalibrationPoint { Duty = 100, Rpm = _maxRpm });
                NextStep(Phase.Down, now);
                _downIndex = 1;
                break;

            case Phase.Down when elapsed >= HoldTime:
            {
                double duty = DownSteps[_downIndex];
                double avg = Average();
                _points.Add(new CalibrationPoint { Duty = duty, Rpm = avg });
                if (avg >= SpinThresholdRpm) _stopDuty = duty; // まだ回っている最低の Duty を更新し続ける

                if (_downIndex < DownSteps.Length - 1)
                {
                    _downIndex++;
                    NextStep(Phase.Down, now);
                }
                else if (avg >= SpinThresholdRpm)
                {
                    // 0% でも回り続けるファン（PWM 非対応や最低回転保証付き）
                    _stopDuty = 0;
                    _startDuty = 0;
                    Finish();
                }
                else
                {
                    _upDuty = 5;
                    NextStep(Phase.Up, now);
                }
                break;
            }

            case Phase.Up when elapsed >= HoldTime:
                if (Average() >= SpinThresholdRpm)
                {
                    _startDuty = _upDuty;
                    Finish();
                }
                else if (_upDuty >= MaxStartSearchDuty)
                {
                    _startDuty = MaxStartSearchDuty;
                    Finish();
                }
                else
                {
                    _upDuty += 5;
                    NextStep(Phase.Up, now);
                }
                break;
        }
    }

    public CalibrationResult? BuildResult()
    {
        if (FailureReason is not null || !IsDone) return null;
        return new CalibrationResult
        {
            ControlId = ControlId,
            MaxRpm = Math.Round(_maxRpm),
            StopDuty = _stopDuty ?? 0,
            StartDuty = _startDuty ?? _stopDuty ?? 0,
            Points = _points.OrderBy(p => p.Duty).Select(p => new CalibrationPoint { Duty = p.Duty, Rpm = Math.Round(p.Rpm) }).ToList(),
            MeasuredAt = DateTime.Now,
        };
    }

    public void Fail(string reason)
    {
        FailureReason = reason;
        _phase = Phase.Done;
    }

    private void Finish() => _phase = Phase.Done;

    private void NextStep(Phase phase, DateTime now)
    {
        _phase = phase;
        _stepStarted = now;
        _samples.Clear();
    }

    private double Average() => _samples.Count == 0 ? 0 : _samples.Average();
}
