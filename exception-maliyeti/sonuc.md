# C#'ta exception maliyeti: ölçüm sonuçları (2026-10-05)

BenchmarkDotNet ham çıktısı. Yorumu: https://huseyinbozdemir.com/blog/csharp-exception-performansi

## GecersizGirdi

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5700X 3.40GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method     | Mean         | Error      | StdDev     | Ratio  | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------- |-------------:|-----------:|-----------:|-------:|--------:|-------:|----------:|------------:|
| TryParse   |     8.288 ns |  0.1885 ns |  0.1671 ns |   1.00 |    0.03 |      - |         - |          NA |
| ParseCatch | 1,707.895 ns | 32.9452 ns | 33.8323 ns | 206.14 |    5.60 | 0.0286 |     480 B |          NA |

## HataOlunca

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5700X 3.40GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method       | Derinlik | Mean         | Error      | StdDev      | Ratio    | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------- |--------- |-------------:|-----------:|------------:|---------:|--------:|-------:|----------:|------------:|
| **SonucDon**     | **1**        |     **1.113 ns** |  **0.0347 ns** |   **0.0341 ns** |     **1.00** |    **0.04** |      **-** |         **-** |          **NA** |
| FirlatYakala | 1        | 1,491.144 ns | 27.8724 ns |  26.0719 ns | 1,340.86 |   44.79 | 0.0191 |     320 B |          NA |
|              |          |              |            |             |          |         |        |           |             |
| **SonucDon**     | **10**       |    **11.229 ns** |  **0.1845 ns** |   **0.1726 ns** |     **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FirlatYakala | 10       | 3,819.865 ns | 75.7081 ns | 101.0681 ns |   340.26 |   10.17 | 0.0763 |    1336 B |          NA |

## HataYokken

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5700X 3.40GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method      | Mean      | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------ |----------:|----------:|----------:|------:|--------:|----------:|------------:|
| TryCatchYok | 0.2390 ns | 0.0185 ns | 0.0173 ns |  1.01 |    0.10 |         - |          NA |
| TryCatchVar | 0.9137 ns | 0.0370 ns | 0.0328 ns |  3.84 |    0.31 |         - |          NA |

