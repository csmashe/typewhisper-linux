namespace TypeWhisper.Linux.Services;

internal sealed class StreamingSampleRateConverter
{
    private const int FilterRadiusMultiplier = 24;

    private readonly List<float> _history = [];
    private readonly int _targetSampleRate;

    private double[] _coefficients = [];
    private int _filterRadius;
    private float _firstSample;
    private long _historyStartIndex;
    private float _lastSample;
    private long _nextOutputSampleIndex;
    private int? _sourceSampleRate;
    private double _sourceToTargetRatio;
    private long _totalInputSampleCount;

    public StreamingSampleRateConverter(int targetSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
        _targetSampleRate = targetSampleRate;
    }

    public float[] Append(float[] samples, int sourceSampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);

        if (samples.Length == 0)
        {
            return [];
        }

        var output = new List<float>();
        if (_sourceSampleRate is { } currentSourceSampleRate
            && currentSourceSampleRate != sourceSampleRate)
        {
            // A capture-rate boundary is also a signal boundary. Complete the old
            // segment with its own right endpoint, then discard its filter and history
            // so neither samples nor fractional phase can bleed into the new rate.
            CompleteSegment(output);
        }

        if (_sourceSampleRate is null)
        {
            StartSegment(sourceSampleRate);
        }

        if (_totalInputSampleCount == 0)
        {
            _firstSample = samples[0];
        }

        _history.AddRange(samples);
        _lastSample = samples[^1];
        _totalInputSampleCount += samples.Length;

        EmitAvailableSamples(output, completing: false);
        return [.. output];
    }

    public float[] Complete()
    {
        if (_sourceSampleRate is null)
        {
            return [];
        }

        var output = new List<float>();
        CompleteSegment(output);
        return [.. output];
    }

    public void Reset() => ClearSegment();

    private void StartSegment(int sourceSampleRate)
    {
        _sourceSampleRate = sourceSampleRate;
        _sourceToTargetRatio = (double)sourceSampleRate / _targetSampleRate;

        if (sourceSampleRate <= _targetSampleRate)
        {
            return;
        }

        _filterRadius = (int)Math.Ceiling(FilterRadiusMultiplier * _sourceToTargetRatio);
        _coefficients = new double[_filterRadius + 1];
        CreateDownsamplingFilter(
            _coefficients,
            _filterRadius,
            sourceSampleRate,
            _targetSampleRate
        );
    }

    private void CompleteSegment(List<float> output)
    {
        EmitAvailableSamples(output, completing: true);
        ClearSegment();
    }

    private void EmitAvailableSamples(List<float> output, bool completing)
    {
        if (_sourceSampleRate is not { } sourceSampleRate || _totalInputSampleCount == 0)
        {
            return;
        }

        var completedOutputLength = completing
            ? Math.Max(
                1L,
                (long)Math.Round(
                    _totalInputSampleCount * (double)_targetSampleRate / sourceSampleRate
                )
            )
            : long.MaxValue;

        while (_nextOutputSampleIndex < completedOutputLength)
        {
            var sourceIndex = _nextOutputSampleIndex * _sourceToTargetRatio;
            var leftIndex = (long)Math.Floor(sourceIndex);
            var fraction = (float)(sourceIndex - leftIndex);

            if (!completing && !HasRequiredRightContext(leftIndex, fraction))
            {
                break;
            }

            output.Add(EvaluateSample(leftIndex, fraction));
            _nextOutputSampleIndex++;
        }

        TrimUnusedHistory();
    }

    private bool HasRequiredRightContext(long leftIndex, float fraction)
    {
        var rightIndex = fraction == 0f ? leftIndex : leftIndex + 1;
        return rightIndex + _filterRadius < _totalInputSampleCount;
    }

    private float EvaluateSample(long leftIndex, float fraction)
    {
        var finalIndex = _totalInputSampleCount - 1;
        var rightIndex = Math.Min(leftIndex + 1, finalIndex);

        if (_coefficients.Length > 0)
        {
            var leftSample = EvaluateFirAtIndex(leftIndex);
            if (rightIndex != leftIndex && fraction != 0f)
            {
                var rightSample = EvaluateFirAtIndex(rightIndex);
                leftSample += (rightSample - leftSample) * fraction;
            }

            return (float)leftSample;
        }

        var left = GetSample(leftIndex);
        var right = GetSample(rightIndex);
        return left + (right - left) * fraction;
    }

    private double EvaluateFirAtIndex(long index)
    {
        var result = _coefficients[0] * GetSample(index);
        for (var offset = 1; offset < _coefficients.Length; offset++)
        {
            result += _coefficients[offset]
                      * (GetSample(index - offset) + GetSample(index + offset));
        }

        return result;
    }

    private float GetSample(long index)
    {
        // The batch converter clamps both sides to the real endpoints. Keep that
        // contract in streaming mode: the first real sample supplies left history,
        // and Complete supplies the final real sample for the delayed right tail.
        if (index < 0)
        {
            return _firstSample;
        }

        if (index >= _totalInputSampleCount)
        {
            return _lastSample;
        }

        return _history[checked((int)(index - _historyStartIndex))];
    }

    private void TrimUnusedHistory()
    {
        if (_history.Count == 0)
        {
            return;
        }

        var nextSourceIndex = _nextOutputSampleIndex * _sourceToTargetRatio;
        var nextLeftIndex = (long)Math.Floor(nextSourceIndex);
        var firstRequiredIndex = Math.Max(0, nextLeftIndex - _filterRadius);
        var removeCount = Math.Min(
            _history.Count,
            checked((int)Math.Max(0, firstRequiredIndex - _historyStartIndex))
        );

        if (removeCount == 0)
        {
            return;
        }

        _history.RemoveRange(0, removeCount);
        _historyStartIndex += removeCount;
    }

    private void ClearSegment()
    {
        _history.Clear();
        _coefficients = [];
        _filterRadius = 0;
        _firstSample = 0;
        _historyStartIndex = 0;
        _lastSample = 0;
        _nextOutputSampleIndex = 0;
        _sourceSampleRate = null;
        _sourceToTargetRatio = 0;
        _totalInputSampleCount = 0;
    }

    private static void CreateDownsamplingFilter(
        Span<double> coefficients,
        int filterRadius,
        int sourceSampleRate,
        int targetSampleRate)
    {
        var normalizedCutoff = 0.45 * targetSampleRate / sourceSampleRate;
        double coefficientSum = 0;

        for (var offset = 0; offset <= filterRadius; offset++)
        {
            var sincArgument = 2 * normalizedCutoff * offset;
            var sinc = offset == 0
                ? 1
                : Math.Sin(Math.PI * sincArgument) / (Math.PI * sincArgument);
            var ideal = 2 * normalizedCutoff * sinc;
            var window = 0.42
                         + 0.50 * Math.Cos(Math.PI * offset / filterRadius)
                         + 0.08 * Math.Cos(2 * Math.PI * offset / filterRadius);
            var coefficient = ideal * window;
            coefficients[offset] = coefficient;
            coefficientSum += offset == 0 ? coefficient : 2 * coefficient;
        }

        for (var offset = 0; offset < coefficients.Length; offset++)
        {
            coefficients[offset] /= coefficientSum;
        }
    }
}
