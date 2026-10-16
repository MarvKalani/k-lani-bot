using System.Diagnostics;

namespace KLaniBot.Visibility;

// The local step window shows measured work in its retained history, independently of unavailable model-client usage.
public sealed record StepEfficiencySnapshot(int Entries, int Completed, int Failed, int Uncertain, int MeasuredEntries,
    long? ArgumentBytes, long? ResultBytes, long? ElapsedMilliseconds);

// Each identity contributes its latest retained state; planned steps never contribute measurements.
public static class StepEfficiency
{
    public static StepEfficiencySnapshot Measure(StepList steps)
    {
        var identified = new Dictionary<string, StepView>(StringComparer.Ordinal);
        var anonymous = new List<StepView>();
        var counts = new Accumulator();
        IEnumerable<StepView> observed = steps.Before;
        if (steps.Now != null)
        {
            observed = observed.Append(steps.Now);
        }
        foreach (StepView step in observed)
        {
            if (step.OperationId.Length == 0)
            {
                anonymous.Add(step);
            }
            else
            {
                identified[step.OperationId] = step;
            }
        }
        foreach (StepView step in anonymous.Concat(identified.Values))
        {
            counts.Add(step);
        }

        return counts.Snapshot();
    }

    // One snapshot owns these counters; invalid or overflowing diagnostics cannot fabricate a small successful total.
    private sealed class Accumulator
    {
        private int _entries;
        private int _completed;
        private int _failed;
        private int _uncertain;
        private int _measured;
        private long? _arguments = 0;
        private long? _results = 0;
        private long? _milliseconds = 0;

        public void Add(StepView step)
        {
            _entries++;
            AddStatus(step.Status);
            StepDiagnostics? details = step.Diagnostics;
            if (details == null || details.ArgumentJsonBytes < 0 || details.ResultJsonBytes < 0 || details.ElapsedMilliseconds < 0)
            {
                return;
            }
            _measured++;
            _arguments = AddKnown(_arguments, details.ArgumentJsonBytes);
            _results = AddKnown(_results, details.ResultJsonBytes);
            _milliseconds = AddKnown(_milliseconds, details.ElapsedMilliseconds);
        }

        private void AddStatus(StepStatus status)
        {
            switch (status)
            {
                case StepStatus.Completed:
                    _completed++;
                    break;
                case StepStatus.Failed:
                    _failed++;
                    break;
                case StepStatus.Uncertain:
                    _uncertain++;
                    break;
                case StepStatus.Running:
                case StepStatus.Paused:
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        public StepEfficiencySnapshot Snapshot()
        {
            return new(_entries, _completed, _failed, _uncertain, _measured,
                _measured == 0 ? null : _arguments, _measured == 0 ? null : _results, _measured == 0 ? null : _milliseconds);
        }

        private static long? AddKnown(long? current, long value)
        {
            return current.HasValue && value <= long.MaxValue - current.Value ? current.Value + value : null;
        }
    }
}
