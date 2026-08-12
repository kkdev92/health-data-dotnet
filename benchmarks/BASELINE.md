# Benchmark baseline

First recorded baseline for the SDK's per-request cost targets.

## What this measures, and what it does not

The goal is **client-side overhead**, not Google's network latency. These numbers say whether
deserialization, request construction, and the pagination loop stay cheap as the contract grows.
They are a regression tripwire, not a marketing claim.

## Environment

```text
date        2026-08-10
runtime     .NET 10.0.10, win-arm64
sdk         10.0.302
config      --job short (BenchmarkDotNet ShortRun)
machine     developer laptop, not an isolated CI runner
```

> `--job short` trades accuracy for wall-clock time. Treat single-digit percentage differences as
> noise, and re-measure with the default job before acting on anything. A developer laptop also
> has background load a CI runner would not.

## Serialization

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Profile deserialize | 776 ns | 944 B |
| DataPoint page deserialize (1,000 items) | 485 µs | 681 KB |
| Large heart-rate response (10,000 items) | 15.4 ms ⚠️ | 9.7 MB |
| Request serialize, write contract | 491 ns | 688 B |
| Request serialize with output-only stripping | 151 ns | 264 B |

⚠️ The 10,000-item figure had a standard deviation of 4.4 ms against a 15.4 ms mean, and triggered
Gen2 collections. It is not a usable number yet; it needs the default job and a dedicated run. It
is recorded only so the order of magnitude is on file.

Roughly **1 KB allocated per data point** on the large page. That is dominated by the object graph
itself: each `DataPoint` carries a nested measurement plus the physical-time/UTC-offset pair.

## Request construction

| Benchmark | Mean | Allocated |
|---|---:|---:|
| URI: path with custom-method suffix | 154 ns | 1,032 B |
| URI: path and four query parameters | 314 ns | 2,240 B |
| URI: escaping a reserved character | 207 ns | 1,256 B |
| GoogleTimestamp parse | 141 ns | 0 B |
| GoogleTimestamp format | 241 ns | 72 B |
| GoogleDuration parse | 7.6 ns | 0 B |
| GoogleDuration format, fractional | 29.9 ns | 88 B |
| Open enum: read / construct / compare | below measurement floor | 0 B |

**Open enums cost nothing.** All three operations were indistinguishable from an empty method and
allocated zero bytes, which is what ADR-0005 needed to be true: tolerating unknown values must not
be paid for on every access.

**URI construction allocates 1–2 KB per request**, which is the largest fixed per-call cost here.
It comes from `UriTemplate` splitting the resource name on `/` and rejoining it, plus the builder's
intermediate strings. Holding down URI construction allocation is an explicit goal, so this is
the clearest optimization target on the list. It is recorded, not fixed: correctness
was pinned first, and the escaping rules now have golden tests to optimize against.

## Pagination

| Benchmark | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Enumerate every item across pages (10 × 100) | 501 µs | 1.00 | 802.6 KB |
| Raw list loop, driving the token by hand | 480 µs | 0.96 | 801.9 KB |
| Single page, no enumeration | 46.7 µs | 0.09 | 79.8 KB |

**The convenience layer is free.** Enumerating costs 0.7 KB more than driving the page token by
hand across ten pages, about 0.1%, and the timing difference sits inside the error bars. Callers
do not pay for `EnumerateAsync` over the raw list call, which is what keeping the raw call
primary and enumeration additive assumes.

## Running these

```bash
# everything, default job (slow, accurate)
dotnet run --project benchmarks/Kkdev92.HealthData.Benchmarks -c Release -- --filter '*'

# one group, quick indicative reading
dotnet run --project benchmarks/Kkdev92.HealthData.Benchmarks -c Release -- --filter '*Pagination*' --job short
```

Benchmarks are deliberately not run in CI. They need a quiet machine to mean anything, and a noisy
number that fails a build teaches people to ignore the build.
