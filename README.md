# 7th-semester coursework

Laboratory projects, editable diagrams, reports, and verification evidence, grouped by subject.

| Subject | Published work | Entry point |
| --- | --- | --- |
| BOITC | Lab1 business and registration documents | [Documents](BOITC/lab1/) |
| CIS | Labs1–2: 1C databases; lab3: editable algorithm diagrams | [Final lab3 diagrams](CIS/lab3/lab3-final.drawio) |
| IOAD | Labs1–8: data processing, machine learning, and image preprocessing | [Guide](IOAD/readme.md) |
| OUIS | Labs4–5 and scoring presentation | [Labs](OUIS/lab4-5/) |
| PIS | Labs2–6: editable project and UML models | [UML guide](PIS/UML-README.md) |
| PMP | Labs1–8: Dart and separate Flutter applications | [Lab4–5](PMP/lab4-5/README.md), [lab6](PMP/lab6/README.md), [lab7](PMP/lab7/README.md), [lab8](PMP/lab8/README.md) |
| PWS | REST services/gateways, JSON-RPC, and cross-language gRPC | [REST](PWS/lab1/README.md), [JSON-RPC](PWS/lab2/README.md), [gRPC](PWS/lab2-grpc/README.md) |
| RIOT | Burmalda labs1–8: services, concurrency, schemas, replication, Pub/Sub, Raft, Docker | [Project guide](RIOT/burmalda/README.md) |
| UITP | Project charter, baseline, assignments8–9 and10–16 | [Project files](UITP/) |

Each application has its own dependencies and launch commands. PWS uses .NET10; Burmalda uses its pinned .NET9 SDK. Flutter applications are independent projects.

## Inputs and generated files

IOAD labs1–3 require a local `data.csv` beside each script. Labs4–7 use the hh.ru dataset and preparation steps in [the dataset guide](IOAD/hh-data/readme.md). Downloaded source data and the generated IT-vacancy dataset remain local; result tables and the lab8 demonstration images are included.

Build outputs, APKs, dependency caches, local environment files, and temporary 1C files are excluded. Primary 1C databases, editable Office/Draw.io/ProjectLibre deliverables, tests, dependency lockfiles, and selected verification records are included.

## Verification

See [the November11 validation report](docs/VALIDATION.md) for checks run on the published source, the lab6 failing battery test, and the existing Docker fresh-install acceptance limitation.

For PMP lab7, restore both packages before analysis:

```powershell
cd PMP/lab7
flutter pub get
Push-Location tools/tests
flutter pub get
flutter test
Pop-Location
flutter analyze
```

Burmalda history is split by lab milestone, using recovered checkpoints for earlier implementations. Its solution references use relative paths so builds do not depend on this checkout's original drive or directory.
