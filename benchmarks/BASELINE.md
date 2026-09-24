# Benchmark baseline

The SDK's per-request cost, as last measured. Each re-measurement replaces the tables, and the
notes under each table say what moved since the previous baseline and why.

## What this measures, and what it does not

The goal is **client-side overhead**, not Google's network latency. These numbers say whether
deserialization, request construction, and the pagination loop stay cheap as the contract grows.
They are a regression tripwire, not a marketing claim.

## Environment

```text
date        2026-09-24
commit      7e9672c
runtime     .NET 10.0.12, win-arm64
sdk         10.0.401
tool        BenchmarkDotNet 0.15.8
config      --job short (BenchmarkDotNet ShortRun); the 10,000-item page on the default job
machine     developer laptop (Snapdragon X, X1E80100), not an isolated CI runner
```

> `--job short` trades accuracy for wall-clock time. Treat single-digit percentage differences as
> noise, and re-measure with the default job before acting on anything. A developer laptop also
> has background load a CI runner would not.

Times taken on different days on this machine differ by more than most of the changes noted
below. The notes compare with the previous baseline by allocation, which does not vary from run to
run. Where a change's effect on time mattered, it was measured before and after in one process,
and those numbers are in the pull request that made the change.

## Serialization

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Profile deserialize | 728 ns | 944 B |
| DataPoint page deserialize (1,000 items) | 488 µs | 657 KB |
| Large heart-rate response (10,000 items) | 18.2 ms | 8.5 MB |
| Request serialize, write contract | 682 ns | 688 B |
| Request serialize with output-only stripping | 155 ns | 264 B |

The 10,000-item row comes from a default-job run of its own, with a standard deviation of 0.7 ms.
It was measured the same day at `f379138`, which differs from `7e9672c` only in how requests are
written, so nothing this row reads changed in between. The previous baseline's figure for this
row came from the short job and was not usable: a 4.4 ms deviation against a 15.4 ms mean.

**Reading allocates only the object graph.** That comes to about 0.9 KB per data point on the
large page. Each `DataPoint` carries a nested measurement plus the physical-time/UTC-offset pair.
Every int64, timestamp and duration value used to be read into a string that was parsed and
dropped. Measured before and after that change, the strings cost 32 bytes a point on the
1,000-item page, which has one such value per point, and 136 bytes on the 10,000-item page, which
has three. That is why both pages allocate less than in the previous baseline.

**Writing checks every measurement.** The write-contract row takes longer than in the first
baseline, at the same allocation. Since 0.2.0-alpha, a data point is checked for carrying more than
one measurement before it is written, which reads each of its forty-two measurement properties.
Measured on its own, the check takes about 125 ns, most of the difference, and it allocates
nothing.

## Request construction

| Benchmark | Mean | Allocated |
|---|---:|---:|
| URI: path only | 97 ns | 816 B |
| URI: path with custom-method suffix | 97 ns | 712 B |
| URI: path and four query parameters | 340 ns | 1,920 B |
| URI: escaping a reserved character | 273 ns | 1,256 B |
| GoogleTimestamp parse | 38.5 ns | 0 B |
| GoogleTimestamp format | 137 ns | 72 B |
| GoogleDuration parse | 11.8 ns | 0 B |
| GoogleDuration format, fractional | 33.7 ns | 88 B |
| Open enum: read / construct / compare | below measurement floor | 0 B |

**Open enums cost nothing.** All three operations were indistinguishable from an empty method and
allocated zero bytes, which is what ADR-0005 needed to be true: tolerating unknown values must not
be paid for on every access.

**URI construction allocates 0.7–1.9 KB per request.** A resource name that needs no escaping,
which is almost every name, is now appended as it is. It used to be split on `/`, each segment
escaped, and the segments joined again, whatever the name contained. That is the 320 bytes gone
from the custom-method and query rows. A name with a reserved character still goes through the
split, because each segment has to be escaped on its own, so that row did not move. What remains
is the builder itself: its parameter dictionary and query list, the variable names cut out of the
template, and the `StringBuilder` that assembles the result.

**A timestamp parses in about a quarter of the time it did.** `GoogleTimestamp` reads RFC 3339
with a parser of its own instead of `DateTimeOffset.TryParse`. The change was made so that it stops
accepting forms RFC 3339 does not allow, and the speed came with it.

`GoogleDuration` parse reads slower than in the previous baseline. Against the 0.5.0-alpha build in
one process, the current one was 1.6 ns slower for `"-14400s"` and no different for `"1.5s"`. The
rest of the gap is the machine on the day.

## Pagination

| Benchmark | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Enumerate every item across pages (10 × 100) | 545 µs | 1.00 | 777.5 KB |
| Raw list loop, driving the token by hand | 521 µs | 0.96 | 777.6 KB |
| Single page, no enumeration | 50.3 µs | 0.09 | 77.4 KB |

**The convenience layer is free.** Across ten pages, enumerating allocates no more than driving
the page token by hand (0.1 KB less in this run), and the timing difference sits inside the error
bars. Callers do not pay for `EnumerateAsync` over the raw list call, which is what keeping the raw
call primary and enumeration additive assumes. Both allocate less than in the previous baseline,
for the same reason as the pages above: an item's count is no longer read into a string first.

## Running these

```bash
# everything, default job (slow, accurate)
dotnet run --project benchmarks/Kkdev92.HealthData.Benchmarks -c Release -- --filter '*'

# one group, quick indicative reading
dotnet run --project benchmarks/Kkdev92.HealthData.Benchmarks -c Release -- --filter '*Pagination*' --job short
```

Benchmarks are deliberately not run in CI. They need a quiet machine to mean anything, and a noisy
number that fails a build teaches people to ignore the build.
