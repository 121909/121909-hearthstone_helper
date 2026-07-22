using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DiscardAdvisor.Domain.Snapshots;

internal static class SnapshotCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));
        return new ReadOnlyCollection<T>(values.ToArray());
    }
}

