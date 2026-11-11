# Validation — November11, 2026

Checks were run from an isolated checkout. Application code was preserved; this publication changes repository organization, documentation, and Burmalda's absolute solution references.

## Checks run

| Area | Result |
| --- | --- |
| Burmalda final solution | Build passed: 0 warnings, 0 errors |
| Burmalda unit tests | 135 passed: 56 Raft, 50 Domain, 29 LibraryGame; 0 failed/skipped |
| Burmalda recovered milestones | Initial project, concurrency, database transactions, real replication, Pub/Sub, and pre-Docker Raft builds all passed |
| Project references across the commit series | All `.slnx` project entries and `.csproj` project references resolve within each staged tree |
| PWS REST solution | Build passed: 0 warnings, 0 errors |
| PWS JSON-RPC | 80 live HTTP integration checks passed; Go vet, client build, and demonstration passed |
| PWS gRPC | All four C#/Go → ASP.NET/Node combinations passed: 92/92 live checks |
| PMP lab4–5 | Offline locked dependency restore and analyzer passed; no host test directory |
| PMP lab6 | Locked dependency restore and analyzer passed; host tests: 11 passed, 1 failed |
| PMP lab7 | Root and nested test-package restore passed; analyzer passed after restoring the nested package; 3 host storage tests passed |
| PMP lab8 | Locked dependency restore and analyzer passed; 19 host tests passed |
| Coursework structure | 34 Python files parsed, 9 Draw.io XML files parsed, 22 Office archives passed CRC checks, 125 standard JSON files parsed |
| Publication credential scan | 1,134 distinct candidate blobs checked for high-confidence GitHub/AWS/OpenAI/Google token patterns and private-key markers; no matches |

The schema-document commit changes deliverables without changing the preceding transaction implementation; it does not require a separate application build. The recovered milestone build records were matched against the exact Burmalda subtree in the final commit series.

## Known limitations

- **PMP lab6:** `test/device_test.dart` → `battery rejects unavailable levels and propagates platform errors` fails: the test expects an `UNAVAILABLE` `PlatformException`, while the current implementation returns a `BatteryReading`. The implementation and test were retained.
- **Burmalda Docker:** the saved [fresh-install integration record](../RIOT/burmalda/lab-results/docker-verification/fresh/integration.json) reports failure on the assertion requiring at least one HTTP429 response. Fresh-volume Docker acceptance was not rerun during publication.
- The source contains 87 whitespace findings reported by `git diff --check` for code files. Formatting was preserved.
- Full IOAD data/model runs, Android/iOS device tests, browser/gameplay acceptance, 1C runtime checks, and ProjectLibre runtime checks were not rerun. Included historical reports retain their original dates and coverage.

## Reproduce the host checks

```powershell
# Burmalda, from RIOT/burmalda
dotnet build Burmalda.slnx
dotnet test Burmalda.slnx --no-build --no-restore

# PWS, from the repository root; ports avoid the regular demonstration ports
dotnet build PWS/lab1/PWS-Lab1.sln
./PWS/lab2/Test-Lab.ps1 -Port 28200
./PWS/lab2-grpc/Test-Lab.ps1 -DotnetPort 28301 -NodePort 28302

# Each Flutter application: restore, analyze, then run available host tests
flutter pub get --offline --enforce-lockfile
flutter analyze --no-pub
flutter test --no-pub
```

For lab7, restore and test `PMP/lab7/tools/tests` separately before running the root analyzer, as shown in [the repository index](../README.md).
