# bcl tool app — 인수인계 문서 (AGENTS.md)

> 작성일 2026-11-15 · 최종 갱신 2026-11-20. 폴더의 코드·프로젝트 파일·기존 문서와 실제 게임 설치 폴더(Steam `.acf`, `UnityPlayer.dll`, `MelonLoader.dll`)를 읽어 정리했다.
> 이 문서 작성 시점에 인게임 재검증은 하지 않았다. **"실측"** 표시는 디스크에서 직접 읽은 값, **"미확인"**은 다음 세션에서 확인할 항목이다.

## 0. 프로젝트 개요

- **무엇인가:** BCL 이식 & 모드 크래시 도우미(Avalonia, net8). 스트리핑된 Mono BCL을 탐지하고, 도너 게임에서 BCL을 찾아 이식하며, MelonLoader 크래시 로그를 분석. 테스트 `BclToolApp.Tests` (105개 통과, 경고 0개), 문서 `docs/01~`, `STRIPPING_RESULTS.md`.
- **종류:** 게임 모드가 아닌 앱/도구
- **최근 수정일(파일 기준):** 2026-11-20
- **git 원격:** `origin` (`https://github.com/magjangin/bcl-tool-app.git`), `local` (`H:\local-repos\bcl tool app.git`)
- **최근 커밋:**
  - `4428f3c Add GitHub Sponsors funding configuration`
  - `84d5bbc feat: find the donor BCL automatically and preselect what to transplant`
  - `6192327 docs: align docs with code and record verified donor findings`
  - `c76aece fix: detect the BCL gaps that actually crash mod loaders`
  - `71fafba feat: customize app icon with modern BCL tool design`
- **먼저 읽을 기존 문서:**
  - [README.md](README.md)
  - [REVIEW.md](REVIEW.md)
  - [STRIPPING_RESULTS.md](STRIPPING_RESULTS.md)
  - [docs\01-tool-overview-and-architecture.md](docs/01-tool-overview-and-architecture.md)
  - [docs\02-melonloader-bcl-stripping-guide.md](docs/02-melonloader-bcl-stripping-guide.md)
  - [docs\03-crash-diagnosis-and-patterns.md](docs/03-crash-diagnosis-and-patterns.md)
  - [docs\04-safety-backup-and-integrity.md](docs/04-safety-backup-and-integrity.md)
  - [docs\05-known-issues-and-improvements.md](docs/05-known-issues-and-improvements.md)
  - [docs\README.md](docs/README.md)

## 1. 게임 버전 및 고정 여부

- 게임 모드가 아니므로 해당 없음.

## 2. 런타임 백엔드 및 디컴파일 경로

- 게임 모드가 아님. 런타임/언어: .NET (`net8.0`)
- 모드/앱 프로젝트 `BclToolApp.csproj` 타깃 프레임워크: `net8.0` (SDK 스타일)
- 모드/앱 프로젝트 `BclToolApp.Tests\BclToolApp.Tests.csproj` 타깃 프레임워크: `net8.0` (SDK 스타일)

## 3. 설계 의도와 우회 사유 (Rationale)

코드 주석과 기존 문서에서 작성자가 남긴 의도·주의 문장을 그대로 옮겼다(파일:줄).

- `README.md:23` — 🛠️ 핵심 기능 (4대 모듈)
- `REVIEW.md:34` — 런타임/프로파일 사전 검사 (높음)**: 지금은 관리형 DLL인지 검증할 뿐, Mono/IL2CPP, Unity 버전, AssemblyName/PublicKeyToken, 참조 의존성, API 시그니처 및 실행 아키텍처의 호환성을 보장하지 않는다. 이식 계획 화면에서 대상/기증 런타임, 변경 목록, 의존성 결손과 핵심 DLL 위험을 먼저 보여주는 기능이 필요하다. 특히 mscorlib는 별도
- `STRIPPING_RESULTS.md:14` — mscorlib 2.x 게임 111개는 모두 `GetPEKind`가 있어, 새 표본 때문에 생긴 오탐은 없다.
- `STRIPPING_RESULTS.md:54` — Neon Abyss와 Zombie Rollerz의 **현재 DLL은 17개 표본 API를 모두 통과**한다. 이미 이식된 상태이기 때문이다.
- `docs\02-melonloader-bcl-stripping-guide.md:56` — Donor BCL 선택 요령 및 주의사항
- `docs\02-melonloader-bcl-stripping-guide.md:73` — PE 파일은 정렬 단위로 크기가 맞춰져 다른 버전도 크기가 같을 수 있습니다. 이식이 성공한 이유는 같은 LTS 줄 안에서 Mono 런타임과 BCL 사이의 인터페이스가 유지되었기 때문으로 보입니다. 반대로 엔진 해시까지 같은 게임만 찾으면 Zombie Rollerz는 도너가 0개가 됩니다.
- `docs\02-melonloader-bcl-stripping-guide.md:78` — 그래서 자동 탐색은 **온전성을 패치 거리보다 먼저** 봅니다: 같은 줄 후보 중 메서드 수가 최대치의 99%에 못 미치면 순위를 내리고 이유를 표시합니다. 2026-09-30 실측(설치된 Mono 게임 671개)에서 Neon Abyss(2018.4.21)의 1순위는 같은 버전의 Mask of Mists가 아니라 Deep Sky Derelicts(2018.4.20, 26,114개)였고 Mas
- `docs\02-melonloader-bcl-stripping-guide.md:84` — [권장 BCL 자동 선택]이 이 비교를 대신합니다: **BCL 이름이면서 도너 쪽 파일이 더 큰 것**만 체크합니다. 실측(Saga of Yurina, Unity 6000.3.14f1 → 도너 Cozy Marbles)에서 `mscorlib`(2,729,472→4,632,064), `System`, `System.Core`, `System.Xml`, `System.Configuration`, 
- `Services\DonorSearchService.cs:22` — The whole window is inspected even after enough candidates pass, because the method counts of the

## 4. 핵심 훅 시그니처와 주입 파이프라인

- **주요 소스 파일(큰 순):**
  - `Services\CrashLogAnalyzerService.cs`
  - `Services\DonorSearchService.cs`
  - `BclToolApp.Tests\BugReproTests.cs`
  - `BclToolApp.Tests\DonorSearchTests.cs`
  - `Services\BclTransplantService.cs`
  - `Services\GameDiscoveryService.cs`
  - `BclToolApp.Tests\BclStrippingDetectorTests.cs`
  - `ViewModels\AssemblyDiffViewModel.cs`
  - `BclToolApp.Tests\PortingTests.cs`
  - `Services\BclAssemblyInspectorService.cs`
  - `Services\BclStrippingDetectorService.cs`
  - `ViewModels\GameDiscoveryViewModel.cs`

## 5. 시도했다가 실패한 것들 (Dead Ends)

코드 주석과 기존 문서에서 실패·크래시·금지 관련 문장을 그대로 옮겼다(파일:줄). 같은 방법을 다시 시도하기 전에 해당 위치를 읽을 것.

- `README.md:5` — BCL Porting & Mod Crash Assistant (BCL Tool App)
- `README.md:11` — Unity 게임 모딩(MelonLoader, BepInEx 등) 시 Unity의 **관리 코드 스트리핑(Managed Code Stripping)**으로 인해 발생하는 BCL(Base Class Library: `mscorlib.dll`, `System.Core.dll` 등) 결손 및 모드로더 부트스트랩 실패/크래시를 오프라인에서 안전하게 탐지, 진단, 복구(이식)하는 크로스플랫폼 GUI 데
- `README.md:18` — SHA-256 무결성 및 자동 롤백**: 파일 이식 시 원본 파일 해시 기반 백업(`.bcl-backup.json`)을 자동 생성하며, 복사 중 오류나 검증 실패가 나면 바꾼 파일을 되돌립니다. 앱이 실행 중인 동안의 오류에 한하며, 강제 종료나 전원 차단까지 보장하는 트랜잭션은 아닙니다.
- `README.md:25` — 🔍 스트리핑 게임 자동 감지 (`Game Discovery & Stripping Detector`)
- `README.md:27` — `mscorlib.dll`과 `System.Core.dll`의 **17개 표본 API** 존재 여부를 Cecil로 정적 검사하여 스트리핑 의심 게임을 선별합니다. 모드로더가 실제로 실패하는 `Module.GetPEKind`·`TypeInfo` 가상 메서드와 일반 BCL API(Activator, Reflection, DynamicMethod, ILGenerator, LINQ, Expressi
- `README.md:30` — 🩺 크래시 로그 진단기 (`Crash Log Analyzer`)
- `README.md:32` — BCL 결손(`MissingMethodException`, `TypeLoadException`, MelonLoader의 `VTable setup of type ... failed`, `FileNotFoundException`), 엔진 내부 icall, 모드로더 부트스트랩 실패, 런타임 프로파일(.NET 2.0 vs .NET 4.6+) 불일치를 자동 분류하고 맞춤형 해결 가이드를 제공합니다.
- `README.md:44` — 이식 과정 중 오류 발생 시 즉시 이전 상태로 자동 롤백하며, 원클릭으로 원본 복원이 가능합니다.
- `Program.cs:8` — Initialization code. Don't use any Avalonia, third-party APIs or any
- `Program.cs:15` — Avalonia configuration, don't remove; also used by visual designer.
- `BclToolApp.Tests\BclStrippingDetectorTests.cs:81` — Shape of Neon Abyss's original mscorlib: every common API kept, GetPEKind and most of TypeInfo stripped.
- `BclToolApp.Tests\BclStrippingDetectorTests.cs:93` — mscorlib 2.0 never had TypeInfo (.NET 4.5); that is the profile, not stripping.
- `BclToolApp.Tests\BugReproTests.cs:22` — Each test asserts the correct behaviour, so a failure reproduces a known bug.
- `BclToolApp.Tests\BugReproTests.cs:51` — ---------- Crash log analyzer ----------
- `BclToolApp.Tests\BugReproTests.cs:77` — Real MelonLoader failure on a stripped mscorlib.
- `BclToolApp.Tests\BugReproTests.cs:93` — Shape of the In Falsus Demo case: a stripped third-party ZString.dll, not the BCL.
- `BclToolApp.Tests\DonorSearchTests.cs:275` — Stripped BCL in the game, intact in the donor, plus files that must never be transplanted.
- `Services\BclAssemblyInspectorService.cs:106` — A stripped BCL is smaller than the intact one; that is the signal the transplant reverses.

## 6. 빌드 명령 및 배포 경로

- 솔루션: `BclToolApp.sln`
- `BclToolApp.csproj`:
  ```powershell
  dotnet build "BclToolApp.csproj" -c Release
  ```
- `BclToolApp.Tests\BclToolApp.Tests.csproj`:
  ```powershell
  dotnet build "BclToolApp.Tests\BclToolApp.Tests.csproj" -c Release
  ```

## 7. 다음 작업자에게

- 작업 전 기존 문서(0절)부터 읽는다.
- 인게임 동작을 확인하면 3·5절의 **미확인** 항목을 실제 내용으로 바꾼다.
