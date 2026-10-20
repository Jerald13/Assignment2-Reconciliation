using System.Globalization;
using Reconciliation.Csv;
using Reconciliation.Model;

namespace Reconciliation.Processing;

/// <summary>
/// Spills validated records to disk, split into N partitions by a hash of the reference.
/// Matching references from both lists always land in the same partition number, so each
/// partition pair can be reconciled independently with only one partition in memory at a time
/// (a "Grace hash join"). This bounds memory regardless of input size.
/// </summary>
public sealed class PartitionStore : IDisposable
{
    public const string SideA = "a";
    public const string SideB = "b";

    private readonly string _directory;

    public PartitionStore(string tempRoot, int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        PartitionCount = partitionCount;
        _directory = Path.Combine(tempRoot, $"reconciliation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public int PartitionCount { get; }

    public PartitionWriter CreateWriter(string side) => new(this, side);

    public IEnumerable<TransactionRecord> Read(string side, int partition)
    {
        var path = PathFor(side, partition);
        if (!File.Exists(path))
        {
            yield break;
        }

        // Status and masked card values repeat across millions of rows; sharing one instance of each
        // instead of allocating a new string per row noticeably reduces the in-memory partition size.
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Shared(string value) => value.Length == 0 ? string.Empty : pool.TryAdd(value, value) ? value : pool[value];

        using var reader = CsvReader.Open(path);
        while (reader.TryReadRecord(out var f))
        {
            yield return new TransactionRecord(
                Reference: f[0],
                SourceLine: long.Parse(f[1], CultureInfo.InvariantCulture),
                RowNumber: f[2],
                TransactionDate: DateOnly.ParseExact(f[3], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Amount: decimal.Parse(f[4], CultureInfo.InvariantCulture),
                Fees1: decimal.Parse(f[5], CultureInfo.InvariantCulture),
                Fees2: decimal.Parse(f[6], CultureInfo.InvariantCulture),
                NetTotal: decimal.Parse(f[7], CultureInfo.InvariantCulture),
                Status: Shared(f[8]),
                CardNumber: f[9].Length == 0 ? null : Shared(f[9]));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp files are best-effort clean-up; never mask the real outcome of the run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal string PathFor(string side, int partition) => Path.Combine(_directory, $"{side}-{partition:D4}.csv");

    /// <summary>FNV-1a: stable across processes and platforms (unlike string.GetHashCode).</summary>
    internal int PartitionOf(string reference)
    {
        var hash = 2166136261u;
        foreach (var c in reference)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % (uint)PartitionCount);
    }

    public sealed class PartitionWriter : IDisposable
    {
        private readonly PartitionStore _store;
        private readonly string _side;
        private readonly CsvWriter?[] _writers;

        internal PartitionWriter(PartitionStore store, string side)
        {
            _store = store;
            _side = side;
            _writers = new CsvWriter?[store.PartitionCount];
        }

        public void Write(TransactionRecord record)
        {
            var partition = _store.PartitionOf(record.Reference);
            var writer = _writers[partition] ??= CsvWriter.Create(
                _store.PathFor(_side, partition),
                bufferSize: _store.PartitionCount > 64 ? 8 * 1024 : 64 * 1024,
                neutraliseFormulas: false);

            writer.WriteRow(
                record.Reference,
                record.SourceLine.ToString(CultureInfo.InvariantCulture),
                record.RowNumber,
                record.TransactionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                record.Amount.ToString(CultureInfo.InvariantCulture),
                record.Fees1.ToString(CultureInfo.InvariantCulture),
                record.Fees2.ToString(CultureInfo.InvariantCulture),
                record.NetTotal.ToString(CultureInfo.InvariantCulture),
                record.Status,
                record.CardNumber);
        }

        public void Dispose()
        {
            foreach (var writer in _writers)
            {
                writer?.Dispose();
            }
        }
    }
}
