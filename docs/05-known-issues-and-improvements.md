# 05. 알려진 문제 및 개선 로드맵 (Known Issues & Roadmap)

## 1. 해결 대상 과제 (10대 재현 테스트 현황)

`BclToolApp.Tests/BugReproTests.cs`에는 실제 스팀 라이브러리와 모딩 환경에서 수집된 엣지 케이스 10건이 테스트로 구성되어 있습니다. 현재 이 테스트들을 통과시키기 위한 로직 보완이 필요합니다:

| 번호 | 테스트 명칭 | 현재 현상 및 원인 | 해결 방안 |
|:---:|---|---|---|
| **1** | `MonoTypeLoadTokenFormatForStrippedBclTypeIsTransplantCandidate` | Mono 특유의 토큰 형식(`with token 01000015 from typeref`) 파싱 누락 | `CrashLogAnalyzerService`의 `TypeLoadRegex` 정규식에 typeref 토큰 패턴 추가 |
| **2** | `ApiMissingFromGameProfileIsNotTreatedAsStripping` | .NET 2.0 게임(Cuphead 등)에서 .NET 4.6+ 전용 API(`Array.Empty`) 요구 시 BCL 스트리핑으로 오진 | 게임의 TargetFramework / mscorlib 버전을 대조하여 프로파일 자체 미지원 API는 후보에서 제외 |
| **3** | `BepInEx5FatalLineIsLoaderBootstrap` | BepInEx 5 특유의 로그 헤더(`[Fatal  :   BepInEx]`) 미인식 | `LoaderBootstrapRegex`에 `\[Fatal\s*:\s*BepInEx\]` 패턴 추가 |
| **4** | `MissingNuGetPackageShippedByModIsNotTransplantCandidate` | 모드가 번들링해야 할 NuGet 라이브러리(`System.Runtime.CompilerServices.Unsafe` 등)를 BCL 누락으로 오진 | 순수 Mono BCL 목록과 분리하여 NuGet 전용 패키지는 BCL 이식 대상에서 제외 |
| **5** | `Il2cppEngineIcallIsNotLabelledGameCodeByNameGuess` | `VideoPlayer::set_url` 같은 엔진 icall에 "Player" 단어가 포함되어 `GameCodeStripping`으로 오진 | 단순 단어 매칭 대신 네임스페이스 및 Unity Engine 모듈 구조 기반으로 판정 정밀화 |
| **6** | `ReferenceAssemblyDonorIsRejected` | 컴파일용 메타데이터 전용 Reference Assembly가 Donor로 지정될 때 통과됨 | 어셈블리 검증 시 `ReferenceAssemblyAttribute` 존재 여부를 확인하고 에러 반환 |
| **7** | `RestoreDoesNotRevertFilesChangedAfterTransplant` | 이식 완료 후 스팀 업데이트 등으로 갱신된 파일이 이전 백업으로 무조건 덮어써짐 | 백업 매니페스트와 비교하여 이번 이식 대상이 아니었던 파일의 사후 변경 사항 보존 |
| **8** | `RollbackAfterUnbackedSecondTransplantDoesNotLeaveMixedState` | 백업 없이 2차 이식을 진행한 뒤 이전 백업으로 복원할 때 일부 파일만 롤백되어 불완전 상태 발생 | 다중 이식 감지 시 안전 가드레일 작동 또는 이전 백업 복원 시 불일치 상태 경고 |
| **9** | `JunctionedGameFolderIsNotSilentlyDropped` | Junction(심볼릭 링크)으로 연결된 게임 폴더가 경고도 없이 검색 목록에서 누락 | ReparsePoint 탐색 시 조용히 넘기지 않고 Warning 목록에 명시적 알림 기록 |
| **10** | `PlayerLogIsFoundInLocalLow` | 게임 폴더 외부에 있는 `%USERPROFILE%\AppData\LocalLow` 경로의 `Player.log` 미탐색 | 게임 `*_Data/app.info` 파일의 회사명/제품명을 파싱하여 LocalLow 로그 자동 연결 |

---

## 2. 향후 아키텍처 및 UI/UX 개선 로드맵

### 🚀 1단계: 비동기 I/O 전환 (UI 프리징 해결)
* `AssemblyDiffViewModel.ScanCommand`와 `TransplantViewModel.ExecuteTransplantCommand`를 `[RelayCommand]`에서 `AsyncRelayCommand`(`Task.Run`)로 전환.
* 파일 수가 많은 대용량 게임 처리 시 UI가 응답 없음 상태에 빠지지 않도록 프로그레스 바(ProgressBar) 및 취소 토큰(`CancellationToken`) 연동.

### 📁 2단계: 폴더 선택 대화상자 (FolderPicker) 도입
* Avalonia 11의 `TopLevel.GetTopLevel(this).StorageProvider.OpenFolderPickerAsync()`를 바인딩하여 경로 직접 입력의 번거로움 해소.
* 최근 사용한 Donor BCL 경로 및 게임 경로 즐겨찾기(History) 저장 기능.

### 🛡️ 3단계: 다중 백업 관리자 (Backup History Manager)
* 현재 1회성 메모리 보관 방식에서 벗어나, 상위 디렉터리의 `.bcl-backup.json`들을 스캔하여 백업 생성 일시, 복사된 파일 수, 대상 경로를 테이블 형태로 시각화.
* 원하는 시점의 백업본을 선택하여 롤백할 수 있는 스냅샷 복원 UI 제공.
