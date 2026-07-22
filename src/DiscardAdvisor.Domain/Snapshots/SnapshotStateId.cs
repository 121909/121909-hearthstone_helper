using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DiscardAdvisor.Domain.Snapshots;

public static class SnapshotStateId
{
    public static string Calculate(GameSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        var canonical = new StringBuilder();
        AppendValue(canonical, snapshot, new HashSet<object>(ReferenceEqualityComparer.Instance));
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
        var hex = string.Concat(hash.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        return $"turn-{snapshot.TurnNumber}:{hex}";
    }

    private static void AppendValue(StringBuilder output, object? value, ISet<object> ancestors)
    {
        if (value is null)
        {
            output.Append("null;");
            return;
        }

        if (value is string text)
        {
            output.Append('s').Append(text.Length).Append(':').Append(text).Append(';');
            return;
        }

        if (value is bool boolean)
        {
            output.Append(boolean ? "b1;" : "b0;");
            return;
        }

        if (value is Guid guid)
        {
            output.Append('g').Append(guid.ToString("D")).Append(';');
            return;
        }

        if (value is IFormattable formattable && value.GetType().IsValueType)
        {
            output.Append('v').Append(formattable.ToString(null, CultureInfo.InvariantCulture)).Append(';');
            return;
        }

        if (value is IEnumerable sequence)
        {
            output.Append('[');
            foreach (var item in sequence)
                AppendValue(output, item, ancestors);
            output.Append("];");
            return;
        }

        if (!ancestors.Add(value))
            throw new InvalidOperationException("Snapshot graph contains a cycle.");

        output.Append('{').Append(value.GetType().FullName).Append(':');
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanRead && property.Name != nameof(GameSnapshot.StateId))
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            output.Append(property.Name).Append('=');
            AppendValue(output, property.GetValue(value), ancestors);
        }
        output.Append("};");
        ancestors.Remove(value);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
