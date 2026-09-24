# 설치 게임 BCL 스트리핑 검사 결과

2026-09-24, H:\steam 읽기 검사. Unity 후보 813개를 약 77초에 검사했다. 디스크 캐시와 하드웨어에 따라 시간이 달라진다.

- 스트리핑 의심 26개 / BCL 파일 누락 1개 / 표본 API 통과 607개 / 판정 보류 37개 / 대상 외 142개
- 17개 표본 API의 타입 및 메서드 시그니처를 검사했다. `TypeInfo` 표본 2개는 .NET 4.5 이후 API라 mscorlib 2.x에서는 검사하지 않는다. 누락은 실제 확인했으나 제거 원인이 반드시 스트리핑이라는 확정 판정은 아니다.
- 실제 게임 파일을 수정하거나 실행하지 않았다.

## 이전 검사(2026-09-09, 표본 14개)와의 차이

이전 14개 표본(`List.Add`, LINQ, `Activator` 등)은 게임도 흔히 쓰는 API라 스트리핑에서 살아남는 경우가 많았다. 모드로더가 실제로 실패하는 지점인 `Module.GetPEKind`(BepInEx 6의 `MissingMethodException`)와 `TypeInfo` 가상 메서드(MelonLoader의 `VTable setup of type System.Reflection.DelegatingTypeInfo failed`)를 표본에 추가했다.

- 현재 설치된 mscorlib 4.x 게임 565개 중 `GetPEKind`가 없는 게임은 26개이고, 아래 의심 목록과 정확히 같다. 이 중 15개는 이전 14개 표본만으로는 "통과"였다.
- mscorlib 2.x 게임 111개는 모두 `GetPEKind`가 있어, 새 표본 때문에 생긴 오탐은 없다.
- 이미 이식을 마친 게임의 `.dll.orig` 원본을 따로 검사한 결과: Neon Abyss 3/17, Zombie Rollerz 5/17, In Falsus Demo 3/17로 모두 의심 판정이다. 이전 표본으로는 Neon Abyss와 In Falsus Demo가 "통과"였다. 앱은 `.orig` 파일을 읽지 않으므로 이 검사는 앱 밖에서 같은 감지기로 수행했다.

## 스트리핑 의심 게임

"로더 실패 지점"은 `Module.GetPEKind`, `TypeInfo.GetDeclaredMethod`, `TypeInfo.get_DeclaredMethods` 중 누락된 것이다.

| 게임 | 누락 표본 API | 로더 실패 지점 | 그 외 누락 |
|---|---:|---|---|
| Röki | 7/17 | 3개 모두 | `DynamicMethod.GetILGenerator`, `Enumerable.ToList`, `Expression`, ``Expression`1`` |
| Geometry Arena | 6/17 | 3개 모두 | `DynamicMethod.GetILGenerator`, `Expression`, ``Expression`1`` |
| Do You even Forklift | 5/17 | 3개 모두 | `Expression`, ``Expression`1`` |
| GRIME | 5/17 | 3개 모두 | `Expression.Constant`, ``Expression`1.Compile`` |
| Insurmountable | 5/17 | 3개 모두 | `Expression`, ``Expression`1`` |
| Lovux | 5/17 | 3개 모두 | `Expression`, ``Expression`1`` |
| MiniMetro | 5/17 | 3개 모두 | `Expression`, ``Expression`1`` |
| Quadrata | 5/17 | 3개 모두 | `Expression`, ``Expression`1`` |
| Slingbot Survivors | 5/17 | 3개 모두 | `Expression`, ``Expression`1`` |
| Voxelgram | 5/17 | 3개 모두 | `Expression`, ``Expression`1`` |
| Citizens | 4/17 | 3개 모두 | `DynamicMethod.GetILGenerator` |
| Bridge Constructor Portal | 3/17 | 3개 모두 | - |
| Chef Knight | 3/17 | 3개 모두 | - |
| Dorfromantik | 3/17 | 3개 모두 | - |
| Hell Clock | 3/17 | 3개 모두 | - |
| MateEngine | 3/17 | 3개 모두 | - |
| Meow's Meow | 3/17 | 3개 모두 | - |
| MOUSE | 3/17 | 3개 모두 | - |
| NEEDY GIRL OVERDOSE | 3/17 | 3개 모두 | - |
| Saga of Yurina | 3/17 | 3개 모두 | - |
| Skul | 3/17 | 3개 모두 | - |
| The Past Within | 3/17 | 3개 모두 | - |
| Vectronom | 3/17 | 3개 모두 | - |
| X Invader | 3/17 | 3개 모두 | - |
| 子産み島 | 2/17 | `GetPEKind`, `TypeInfo.GetDeclaredMethod` | - |
| NejiSimTma03 | 2/17 | `GetPEKind`, `TypeInfo.GetDeclaredMethod` | - |

## 기타

In Falsus Demo는 "BCL 파일 누락"으로 분류됐다. 실제로는 데모가 삭제된 뒤 남은 폴더로, 실행 파일과 `UnityPlayer.dll`은 없다. `if-app_Data/Managed`에는 `.dll.orig` 원본 7개와 `System.Drawing.dll`만, 게임 폴더에는 MelonLoader 관련 파일만 남아 있다. 게임 탐색이 `*_Data` 폴더만 있어도 게임으로 인식하기 때문이다([알려진 문제](docs/05-known-issues-and-improvements.md)).

Neon Abyss와 Zombie Rollerz의 **현재 DLL은 17개 표본 API를 모두 통과**한다. 이미 이식된 상태이기 때문이다.

판정 보류에는 System.Core 2.0.5.0 같은 지원 범위 밖 프로파일이 포함된다. 표본 검사이므로 그 외 API가 삭제되었거나 메서드 본문만 변형된 경우는 놓칠 수 있다.

전체 파일별 근거: [JSON 결과](stripping-scan-results.json)
