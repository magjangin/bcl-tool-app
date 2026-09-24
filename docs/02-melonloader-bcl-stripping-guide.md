# 02. 멜론로더 BCL 스트리핑 가이드 (MelonLoader BCL Stripping Guide)

## 1. Unity Managed Code Stripping의 배경
Unity 엔진은 빌드 용량을 최적화하고 배포 크기를 줄이기 위해 **관리형 코드 스트리핑(Managed Code Stripping)** 기술을 기본적으로 적용합니다.
* 빌드 타임에 게임 코드(`Assembly-CSharp.dll`)가 직접 참조하지 않는 BCL 클래스, 메서드, 프로퍼티를 `mscorlib.dll`, `System.Core.dll` 등에서 완전히 삭제합니다.
* 순수 바닐라 게임 입장에서는 아무 문제 없이 작동하지만, **런타임에 외부 모드를 주입하는 MelonLoader 환경에서는 치명적인 문제**가 됩니다.

---

## 2. MelonLoader 관점에서의 스트리핑 문제점

MelonLoader는 게임이 실행될 때 주입(Injection)되어 동작하며 다음과 같은 고유한 특성을 가집니다:

1. **부트스트랩 단계의 리플렉션 결손**
   - 실제로 확인된 증상은 LINQ보다 리플렉션 쪽입니다. 게임이 쓰지 않는 `Module.GetPEKind`와 `System.Reflection.TypeInfo`의 가상 메서드가 mscorlib에서 잘려 나갑니다.
   - MelonLoader: `System.TypeLoadException: VTable setup of type System.Reflection.DelegatingTypeInfo failed` (0.6.6 / 0.7.3 동일). `TypeInfo`를 상속한 타입의 VTable을 만들지 못해 발생합니다. 스트리핑된 Neon Abyss 원본 mscorlib의 `TypeInfo`에는 public 메서드가 17개 중 3개만 남아 있었습니다.
   - BepInEx 6: `System.MissingMethodException: void System.Reflection.Module.GetPEKind(...)`.
   - 두 경우 모두 원인이 mscorlib에 있으므로 `System.Core.dll` 같은 부속 DLL만 바꿔서는 해결되지 않습니다.

2. **커스텀 모드 및 하모니(HarmonyX) 런타임 오류**
   - 리듬게임 커스텀 차트 로더(BMS 파서, 오디오 스트리밍)나 모딩 도구는 광범위한 제네릭 컬렉션, XML/JSON 파서, 정규식을 사용합니다.
   - 게임에 필요한 메서드만 남겨진 BCL 환경에서는 모드가 동작하는 순간 `MissingMethodException`이나 `TypeLoadException`이 발생합니다.

---

## 3. 17개 표본 API 검출 방식 (`BclStrippingDetectorService`)

본 도구는 전체 API 목록이 아닌 **표본 API 시그니처 17개**를 오프라인으로 검사합니다. 앞의 3개는 모드로더가 실제로 실패하는 지점이고, 나머지 14개는 일반적인 BCL API입니다.

| 어셈블리 | 검사 대상 타입 및 메서드 | 목적 |
|---|---|---|
| `mscorlib` | `System.Reflection.Module.GetPEKind(PortableExecutableKinds&, ImageFileMachine&)` | BepInEx 6 프리로더 실패 지점 |
| `mscorlib` | `System.Reflection.TypeInfo.GetDeclaredMethod(String)` | MelonLoader VTable setup 실패 지점 (.NET 4.x 전용) |
| `mscorlib` | `System.Reflection.TypeInfo.get_DeclaredMethods()` | 위와 같음 (.NET 4.x 전용) |
| `mscorlib` | `System.Activator.CreateInstance(Type)` | 동적 인스턴스 생성 검사 |
| `mscorlib` | `System.Type.GetMethod(String)` | 리플렉션 메서드 탐색 검사 |
| `mscorlib` | `System.Reflection.Assembly.GetTypes()` | 모드 어셈블리 탐색용 |
| `mscorlib` | `System.AppDomain.GetAssemblies()` | 도메인 어셈블리 조회용 |
| `mscorlib` | `System.Reflection.Emit.DynamicMethod.GetILGenerator()` | 런타임 훅/패치(Harmony) 필수 API |
| `mscorlib` | `System.Reflection.Emit.ILGenerator.Emit(OpCode)` | 동적 IL 방출 검사 |
| `mscorlib` | `System.Reflection.Emit.ILGenerator.Emit(OpCode, MethodInfo)` | 동적 IL 방출(오버로드) 검사 |
| `mscorlib` | ``System.Collections.Generic.List`1.Add(!0)`` | 제네릭 컬렉션 기본 연산 |
| `System.Core` | `System.Linq.Enumerable.Select` | LINQ 프로젝션 (제네릭 아리티 2) |
| `System.Core` | `System.Linq.Enumerable.Where` | LINQ 조건 필터 (제네릭 아리티 1) |
| `System.Core` | `System.Linq.Enumerable.ToList` | LINQ 컬렉션 변환 |
| `System.Core` | `System.Linq.Enumerable.Any` | LINQ 요소 검사 |
| `System.Core` | `System.Linq.Expressions.Expression.Constant` | 식 트리(Expression Tree) 구성 |
| `System.Core` | ``System.Linq.Expressions.Expression`1.Compile()`` | 동적 식 컴파일 |

* **`TypeInfo` 표본은 mscorlib 4.x에만 적용합니다.** `TypeInfo`는 .NET 4.5에서 추가된 API라 mscorlib 2.x(.NET 2.0/3.5 프로필)에는 원래 없습니다. 2.x에서는 이 두 개를 건너뛰고 15개만 셉니다.
* **표본을 추가한 이유 (2026-09-24 실측):** 기존 14개는 게임도 흔히 쓰는 API라 스트리핑에서 살아남는 경우가 많았습니다. 설치된 mscorlib 4.x 게임 565개 중 `GetPEKind`가 없는 게임 26개 가운데 15개를 기존 14개 표본은 "통과"로 판정했습니다. mscorlib 2.x 게임 111개는 모두 `GetPEKind`를 갖고 있어 새 표본으로 인한 오탐은 없었습니다. 자세한 결과는 [STRIPPING_RESULTS.md](../STRIPPING_RESULTS.md)에 있습니다.
* 표본 검사이므로 통과가 전체 무결성을 뜻하지는 않습니다.

---

## 4. Donor BCL 선택 요령 및 주의사항

스트리핑된 게임에 이식할 정상 BCL("Donor")을 선택할 때는 아래 원칙을 지켜야 합니다.

> 앱에는 아직 도너를 자동으로 찾는 기능이 없습니다. [스트리핑 게임 감지] 탭의 "비교용 Donor로 지정"은 선택한 게임의 `Managed` 폴더를 그대로 지정할 뿐이므로, 아래 기준은 사람이 직접 확인해야 합니다.

### 1) 같은 Unity LTS 줄의 가까운 패치 버전
* Unity 버전은 게임 폴더의 `UnityPlayer.dll` 파일 버전(속성 → 자세히 → 제품 버전)으로 확인합니다.
* **Mono 2.0 / 3.5 프로파일 게임**: 구버전 Unity(예: Unity 5, 2017 등)의 경우 `mscorlib 2.0.0.0` 기반이므로, 같은 Mono 2.0/3.5 Donor를 사용해야 합니다. (.NET 4.6+ 어셈블리를 가져오면 런타임 부팅 불가)
* **Mono 4.x 게임 (Unity 2018+)**: `mscorlib 4.0.0.0` 기반입니다. 같은 LTS 줄(예: 2018.4.x, 2019.4.x)에서 패치 버전이 가장 가까운 게임을 고릅니다. 같은 버전 Unity Editor의 `Editor/Data/MonoBleedingEdge/lib/mono/` 아래 프로필 폴더(예: `unityjit`, 버전에 따라 이름이 다름)도 후보가 될 수 있지만, 이 방식으로 성공한 실측 사례는 아직 없습니다.
* **`mono-2.0-bdwgc.dll` 크기가 같다고 같은 엔진은 아닙니다.** 성공 사례 두 건 모두 엔진 파일 크기는 같았지만 바이너리는 달랐습니다.

| 대상 게임 | 도너 | 엔진 파일 크기 | 실제 차이 |
|---|---|---|---|
| Zombie Rollerz (Unity 2019.4.27) | Deadly Days (2019.4.26) | 4,971,912 동일 | 약 150만 바이트 다름 |
| Neon Abyss (Unity 2018.4.21) | One Step From Eden (2018.4.15) | 4,946,376 동일 | 약 350만 바이트 다름 |

  PE 파일은 정렬 단위로 크기가 맞춰져 다른 버전도 크기가 같을 수 있습니다. 이식이 성공한 이유는 같은 LTS 줄 안에서 Mono 런타임과 BCL 사이의 인터페이스가 유지되었기 때문으로 보입니다. 반대로 엔진 해시까지 같은 게임만 찾으면 Zombie Rollerz는 도너가 0개가 됩니다.

### 2) 도너 mscorlib이 온전한지 확인
* 먼저 `Module.GetPEKind`가 있는지 봅니다. 없으면 도너도 스트리핑된 것입니다.
* **`GetPEKind`가 있어도 온전하다는 보장은 없습니다.** Neon Abyss와 엔진 바이너리가 완전히 같은 Mask of Mists의 mscorlib(3,901,440바이트)은 `GetPEKind`는 있지만 One Step From Eden의 mscorlib(4,069,888바이트)보다 메서드가 926개 적습니다. 약하게 스트리핑된 빌드로 보입니다. 후보가 여럿이면 **파일이 가장 크고 메서드 수가 가장 많은 mscorlib**을 고릅니다.

### 3) mscorlib를 포함해 Managed 폴더 전체를 비교
* 모드로더 크래시의 원인은 대부분 mscorlib에 있으므로(2장 참고), mscorlib 교체가 핵심입니다. `System.Core.dll`만 바꾸면 해결되지 않고, 온전한 System.Core가 잘린 mscorlib의 API를 찾지 못해 다른 오류가 날 수도 있습니다.
* 그만큼 **mscorlib는 엔진과 맞는 도너에서만** 가져와야 합니다. `System.Object`, `System.String` 등 런타임 ABI의 근간이라 엔진이 맞지 않으면 게임이 켜지지 않을 수 있습니다.
* 기본 6종(`mscorlib`, `System`, `System.Core`, `System.Xml`, `System.Configuration`, `Mono.Security`)만 보고 끝내지 말고 `Managed` 폴더 전체를 도너와 파일 크기로 비교합니다. Neon Abyss는 `System.Data`, `System.Xml.Linq`, `System.Numerics`까지 잘려 있어 9개를 교체했습니다.
* 도너 게임의 `Assembly-CSharp.dll`, `UnityEngine.*.dll`, 게임 전용 플러그인은 절대 가져오지 않습니다. 비교 탭은 이 파일들도 체크할 수 있으니 주의하세요.

### 4) 참조 어셈블리(Reference Assembly) 절대 금지
* Visual Studio SDK 폴더(예: `C:\Program Files (x86)\Reference Assemblies\...`)의 DLL은 컴파일 타임용 메타데이터만 들어있고 실제 메서드 바디(IL 코드)가 비어있습니다.
* 이를 게임 폴더에 복사하면 런타임에 즉시 크래시가 발생하므로 반드시 **실제 런타임 실행 바이너리(Unity 배포판 등)**에서 가져와야 합니다.
