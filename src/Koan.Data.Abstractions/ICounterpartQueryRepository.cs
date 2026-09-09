using Koan.Data.Abstractions.Filtering;

namespace Koan.Data.Abstractions;

/// <summary>Optional native counterpart binding. Called under the target Data scope before candidate reads.</summary>
public interface ICounterpartQueryRepository
{
    /// <summary>Capture the current physical target without reading candidates. Reject unsupported identity or storage shapes.</summary>
    CounterpartQueryTarget BindCounterpartTarget();
}
