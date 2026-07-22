# Search benchmarks

The benchmark project exercises the dense branching path used by the local Windows advisor:

- `DenseBeamSearch`: Beam Width 64, at most 12 actions, random discard, barrage, and a 128-card summon pool.
- `FullLocalAdvisor`: deterministic lethal search followed by the remaining portion of the 250 ms local budget.

Run it from a release build on the target Windows machine:

```powershell
dotnet run -c Release --project .\benchmarks\DiscardAdvisor.Search.Benchmarks -- --filter "*"
```

BenchmarkDotNet writes detailed timing, allocation, runtime, and CPU information under `BenchmarkDotNet.Artifacts`. The automated test suite separately enforces the 300 ms budget envelope and fixed-seed output hash.

## Reference baseline

The following reference was captured on 2026-07-22 in the repository's Linux CI container with .NET 8.0.29. It is useful for regression trends only; acceptance measurements should be repeated on the target Windows/HDT machine.

| Benchmark | Mean | Allocated |
| --- | ---: | ---: |
| `DenseBeamSearch` | 206.1 ms | 358.56 MB |
| `FullLocalAdvisor` | 205.4 ms | 370.20 MB |
