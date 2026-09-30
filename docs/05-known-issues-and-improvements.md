# 05. 알려진 문제 및 개선 로드맵 (Known Issues & Roadmap)

## 1. 해결된 재현 테스트 (10건)

`BclToolApp.Tests/BugReproTests.cs`에 실제 스팀 라이브러리와 모딩 환경에서 모은 엣지 케이스 10건이 테스트로 들어 있고, 모두 해결되어 통과합니다. 전체 테스트는 99개이며 모두 통과합니다(2026-09-30, 도너 자동 탐색 및 자동 준비 테스트 21개 추가).

| 번호 | 테스트 명칭 | 원래 현상 | 해결 방식 |
|:---:|---|---|---|
| **1** | `MonoTypeLoadTokenFormatForStrippedBclTypeIsTransplantCandidate` | Mono 특유의 토큰 형식(`with token 01000015 from typeref`) 파싱 누락 | `TypeLoadRegex`에 typeref 토큰 패턴 추가 |
| **2** | `ApiMissingFromGameProfileIsNotTreatedAsStripping` | .NET 2.0 게임(Cuphead 등)에서 .NET 4.6+ 전용 API(`Array.Empty`) 요구 시 BCL 스트리핑으로 오진 | 게임 mscorlib 버전이 4 미만이면 프로필 미지원으로 판정. 현재는 `Array.Empty`만 판별 |
| **3** | `BepInEx5FatalLineIsLoaderBootstrap` | BepInEx 5 특유의 로그 헤더(`[Fatal  :   BepInEx]`) 미인식 | `LoaderBootstrapRegex`에 `\[Fatal\s*:\s*BepInEx\]` 패턴 추가 |
| **4** | `MissingNuGetPackageShippedByModIsNotTransplantCandidate` | 모드가 번들링해야 할 NuGet 라이브러리(`System.Runtime.CompilerServices.Unsafe` 등)를 BCL 누락으로 오진 | NuGet 전용 어셈블리 목록과 Version 5~9 어셈블리는 이식 후보에서 제외(아래 3장 13번 참고) |
| **5** | `Il2cppEngineIcallIsNotLabelledGameCodeByNameGuess` | `VideoPlayer::set_url` 같은 엔진 icall에 "Player" 단어가 포함되어 `GameCodeStripping`으로 오진 | `in the il2cpp runtime` 문구는 네이티브 문제로 분류하고, `VideoPlayer`·`UnityEngine`·`::` 등은 게임 코드 판정에서 제외 |
| **6** | `ReferenceAssemblyDonorIsRejected` | 컴파일용 메타데이터 전용 Reference Assembly가 Donor로 지정될 때 통과됨 | `ReferenceAssemblyAttribute`가 있으면 쓰기 전에 이식 전체 중단 |
| **7** | `RestoreDoesNotRevertFilesChangedAfterTransplant` | 이식 완료 후 스팀 업데이트 등으로 갱신된 파일이 이전 백업으로 무조건 덮어써짐 | 매니페스트에 `ReplacedFiles`를 기록하고 그 파일만 복원. 단, 추가만 한 이식은 아래 3장 7번 참고 |
| **8** | `RollbackAfterUnbackedSecondTransplantDoesNotLeaveMixedState` | 백업 없이 2차 이식을 진행한 뒤 이전 백업으로 복원할 때 일부 파일만 롤백되어 불완전 상태 발생 | 백업 없이 이식하면 이전 백업의 복원 버튼을 비활성화 |
| **9** | `JunctionedGameFolderIsNotSilentlyDropped` | Junction(심볼릭 링크)으로 연결된 게임 폴더가 경고도 없이 검색 목록에서 누락 | 건너뛰되 경고 목록에 기록 |
| **10** | `PlayerLogIsFoundInLocalLow` | 게임 폴더 외부에 있는 `%USERPROFILE%\AppData\LocalLow` 경로의 `Player.log` 미탐색 | `*_Data/app.info`의 회사명/제품명으로 LocalLow 로그 연결 |

---

## 2. 2026-09-24 점검에서 고친 것

| 영역 | 원래 현상 | 수정 |
|---|---|---|
| 스트리핑 감지기 | 14개 표본이 게임도 쓰는 API라 스트리핑에서 살아남아, 실제로 모드로더가 실패하는 게임을 "통과"로 판정. 설치된 게임 중 `GetPEKind`가 없는 26개 중 15개를 놓침. 이미 이식한 Neon Abyss·In Falsus Demo의 원본(`.orig`)도 "통과" | `Module.GetPEKind`, `TypeInfo.GetDeclaredMethod`, `TypeInfo.get_DeclaredMethods` 표본 추가(17개). `TypeInfo`는 mscorlib 4.x에만 적용. 재검사 결과 놓친 게임 0개, 새 오탐 0개([STRIPPING_RESULTS.md](../STRIPPING_RESULTS.md)) |
| 로그 분석기 | MelonLoader의 실제 크래시 `TypeLoadException: VTable setup of type System.Reflection.DelegatingTypeInfo failed`를 "알 수 없음 / 이식 대상 아님"으로 판정 | `VTable setup of type ... failed` 패턴 추가. `System.*` 타입이면 이식 후보로 분류하고 감지 탭에서 `GetPEKind`·`TypeInfo`를 확인하도록 안내. 타사 타입은 판단 보류 |

---

## 3. 2026-09-30 자동화 작업에서 고친 것

실사용(`Saga of Yurina`, Unity 6000.3.14f1)에서 "게임 폴더를 넣었는데 아무 일도 안 일어난다"는 문제를 추적하며 함께 고친 항목입니다.

| 영역 | 원래 현상 | 수정 |
|---|---|---|
| 경로 입력 | 게임 설치 폴더를 붙여넣으면 아무 반응이 없었습니다. 필드가 `..._Data\Managed`만 받았기 때문입니다 | `GameDiscoveryService.ResolveBclDirectory`가 게임 폴더·`*_Data`·`Managed`·실행 파일·따옴표 경로를 모두 Managed 폴더로 바꿉니다. IL2CPP·Managed 후보 다중은 이유를 표시합니다 |
| 비교 탭 | "전체 누락 DLL 이식 선택"이 도너 게임의 서드파티 DLL(`ZLinq.dll` 등)까지 골랐고, 정작 스트리핑된 파일은 "내용 차이"라 선택되지 않았습니다 | `IsRecommended`(도너 쪽이 더 큰 BCL) 기준의 **[권장 BCL 자동 선택]** 추가. 누락 토글은 BCL 이름에만 적용 |
| 비교·이식 | 도너의 `Assembly-CSharp.dll`, `UnityEngine.*.dll`도 체크하면 이식됐습니다 | `BclAssemblyCatalog.IsGameOrEngineCode`에 해당하면 `CanTransplant`가 false |
| 비교 탭 | 파일 크기를 표시하지 않아 스트리핑 여부를 눈으로 알 수 없었고, 도너 게임 DLL 수백 개에 BCL이 묻혔습니다 | 크기 변화(`2,729,472B → 4,632,064B`) 표시, "BCL 어셈블리만 표시" 기본 켜기, 권장·핵심 BCL 우선 정렬 |
| 분류 | 핵심 BCL 목록에 `Mono.Security.dll`, `System.Configuration.dll`, `System.Xml.Linq.dll`이 없었고, `System.Memory.dll` 같은 NuGet 패키지를 BCL로 취급했습니다 | `BclAssemblyCatalog`로 분류를 한곳에 모으고 허용 목록 방식으로 변경(NuGet·게임 코드 제외) |

---

## 4. 확인 필요 (알려진 문제, 미수정)

| 번호 | 영역 | 문제 | 위치 |
|:---:|---|---|---|
| **1** | 도너 선택 | 도너 자동 탐색은 mscorlib만 검사합니다. 도너의 `System.Core.dll` 등 나머지 BCL이 잘려 있어도 후보로 올라오며, 엔진(`mono-2.0-bdwgc.dll`) 호환성은 검증하지 않습니다(같은 LTS 줄이라는 근거만 씁니다). "비교용 Donor로 지정" 버튼은 여전히 아무 검사 없이 경로만 넘깁니다 | `DonorSearchService`, `GameDiscoveryViewModel.UseAsDonor` |
| **2** | 자동 선택 | 권장 기준이 "도너 쪽 파일이 더 크다"입니다. 크기가 같거나 작으면서 특정 API만 잘린 경우는 놓칩니다. 게임에 없는 핵심 BCL은 목록에만 알리고 자동 선택하지 않습니다 | `BclAssemblyInspectorService.CompareDirectories` |
| **3** | 감지·비교 | 수동 이식 때 남긴 `.dll.orig` 원본을 읽지 않아, 이미 이식한 게임의 원래 상태를 앱에서 확인할 수 없습니다 | `BclStrippingDetectorService`, `BclAssemblyInspectorService` |
| **4** | 복원 | 파일을 추가만 한 이식은 `ReplacedFiles`가 비어 있어, 복원 시 `Files`의 모든 최상위 파일을 백업 시점으로 되돌립니다. 이식 후 스팀 업데이트로 바뀐 파일까지 덮어씁니다. 공개 `CreateBackup`으로 만든 전체 백업과 구분하지 못하는 것이 원인입니다 | `BclTransplantService.RestoreBackup` |
| **5** | 게임 탐색 | `*_Data` 폴더만 있어도 게임으로 인식해, 삭제된 게임의 잔여 폴더가 목록에 뜹니다. In Falsus Demo 잔여 폴더가 "BCL 파일 누락"으로 표시됩니다 | `GameDiscoveryService.InspectGame` |
| **6** | 진단 범위 | 게임에 번들된 서드파티 DLL(예: `ZString.dll`)이 잘린 경우를 진단하지 않습니다. 감지기는 mscorlib·System.Core만 보고, 로그 분석기는 `System.*`이 아닌 VTable 오류를 판단 보류로 둡니다. `Invalid type ... for instance field` 형식은 인식하지 않습니다 | `BclStrippingDetectorService`, `CrashLogAnalyzerService` |
| **7** | 로그 분석 | 로그 전체에서 처음 일치하는 줄 하나만 봅니다. 앞쪽의 무해한 예외가 진짜 원인을 가릴 수 있습니다 | `CrashLogAnalyzerService.Analyze` |
| **8** | 성능 | 로그 꼬리 읽기가 파일 전체를 읽으며 매번 앞부분을 지웁니다. 수백 MB 로그에서 느립니다 | `GameDiscoveryService.ReadLogTail` |
| **9** | 화면 | 헤더 문구가 "Unity / IL2CPP 모드로더 BCL 스트리핑 진단"이지만 BCL 검사는 Mono 전용입니다 | `MainWindow.axaml` |
| **10** | 로그 분석 | .NET 5+ 어셈블리 제외 규칙이 `Version=[5-9]\.` 정규식이라 `Version=10.0.0.0` 이상은 걸러지지 않습니다. .NET 10으로 빌드한 모드의 `System.Runtime, Version=10.0.0.0` 오류가 BCL 이식 후보로 잘못 분류될 수 있습니다 | `CrashLogAnalyzerService.HasBclEvidence` |

---

## 5. 향후 아키텍처 및 UI/UX 개선 로드맵

### 🔎 도너 자동 탐색 (구현됨 · `DonorSearchService`)
* 같은 Unity LTS 줄에서 패치가 가까운 게임과 Unity 에디터 Mono 프로파일을 후보로 모아, `Module.GetPEKind`가 살아 있고 mscorlib 메서드가 가장 많은 것을 우선 제시합니다. `mono-2.0-bdwgc.dll` 크기 일치는 같은 엔진을 뜻하지 않으므로 기준으로 쓰지 않습니다.
* Unity 버전은 `UnityPlayer.dll` 버전 리소스 → `*_Data/globalgamemanagers`에 박힌 문자열 순으로 읽습니다. 다른 LTS 줄·버전 미확인 후보는 경로에 자동으로 채우지 않습니다.
* 남은 과제: 선택한 도너와 대상의 `Managed` 폴더를 크기로 비교해 **교체할 파일 목록까지** 제안하기(현재는 도너 폴더만 정하고, 파일 선택은 비교 탭에서 수동).

### 🚀 비동기 I/O 전환 (UI 프리징 해결)
* `AssemblyDiffViewModel.ScanCommand`와 `TransplantViewModel.ExecuteTransplantCommand`를 `[RelayCommand]`에서 `AsyncRelayCommand`(`Task.Run`)로 전환.
* 파일 수가 많은 대용량 게임 처리 시 UI가 응답 없음 상태에 빠지지 않도록 프로그레스 바(ProgressBar) 및 취소 토큰(`CancellationToken`) 연동.

### 📁 폴더 선택 대화상자 (FolderPicker) 도입
* Avalonia의 `TopLevel.GetTopLevel(this).StorageProvider.OpenFolderPickerAsync()`를 바인딩하여 경로 직접 입력의 번거로움 해소.
* 최근 사용한 Donor BCL 경로 및 게임 경로 즐겨찾기(History) 저장 기능.

### 🛡️ 다중 백업 관리자 (Backup History Manager)
* 현재는 마지막 백업 경로만 앱 메모리에 보관합니다. 상위 디렉터리의 `.bcl-backup.json`들을 스캔하여 백업 생성 일시, 복사된 파일 수, 대상 경로를 테이블 형태로 시각화.
* 원하는 시점의 백업본을 선택하여 롤백할 수 있는 스냅샷 복원 UI 제공.
