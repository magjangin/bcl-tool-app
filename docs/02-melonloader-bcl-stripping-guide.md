# 02. 멜론로더 BCL 스트리핑 가이드 (MelonLoader BCL Stripping Guide)

## 1. Unity Managed Code Stripping의 배경
Unity 엔진은 빌드 용량을 최적화하고 배포 크기를 줄이기 위해 **관리형 코드 스트리핑(Managed Code Stripping)** 기술을 기본적으로 적용합니다.
* 빌드 타임에 게임 코드(`Assembly-CSharp.dll`)가 직접 참조하지 않는 BCL 클래스, 메서드, 프로퍼티를 `mscorlib.dll`, `System.Core.dll` 등에서 완전히 삭제합니다.
* 순수 바닐라 게임 입장에서는 아무 문제 없이 작동하지만, **런타임에 외부 모드를 주입하는 MelonLoader 환경에서는 치명적인 문제**가 됩니다.

---

## 2. MelonLoader 관점에서의 스트리핑 문제점

MelonLoader는 게임이 실행될 때 주입(Injection)되어 동작하며 다음과 같은 고유한 특성을 가집니다:

1. **부트스트랩 필수 API 결손**
   - MelonLoader 코어 라이브러리는 리플렉션(`System.Type.GetMethod`), 동적 IL 생성(`DynamicMethod`, `ILGenerator`), LINQ 연산자(`Enumerable.Select`, `Where`, `ToList`)를 필수적으로 사용합니다.
   - 게임의 `System.Core.dll`에서 LINQ 메서드가 스트리핑되어 있으면 MelonLoader가 게임 부팅 단계에서 즉시 크래시(`FATAL: Bootstrap failed`)를 일으킵니다.

2. **커스텀 모드 및 하모니(HarmonyX) 런타임 오류**
   - 리듬게임 커스텀 차트 로더(BMS 파서, 오디오 스트리밍)나 모딩 도구는 광범위한 제네릭 컬렉션, XML/JSON 파서, 정규식을 사용합니다.
   - 게임에 필요한 메서드만 남겨진 BCL 환경에서는 모드가 동작하는 순간 `MissingMethodException`이나 `TypeLoadException`이 발생합니다.

---

## 3. 14개 표본 API 검출 방식 (`BclStrippingDetectorService`)

본 도구는 스트리핑 여부를 확인하기 위해 Mono BCL에서 가장 빈번하게 누락되는 **14가지 핵심 API 시그니처**를 샘플링하여 오프라인으로 검사합니다:

| 어셈블리 | 검사 대상 타입 및 메서드 | 목적 |
|---|---|---|
| `mscorlib` | `System.Activator.CreateInstance(Type)` | 동적 인스턴스 생성 검사 |
| `mscorlib` | `System.Type.GetMethod(String)` | 리플렉션 메서드 탐색 검사 |
| `mscorlib` | `System.Reflection.Assembly.GetTypes()` | 모드 어셈블리 탐색용 |
| `mscorlib` | `System.AppDomain.GetAssemblies()` | 도메인 어셈블리 조회용 |
| `mscorlib` | `System.Reflection.Emit.DynamicMethod.GetILGenerator()` | 런타임 훅/패치(Harmony) 필수 API |
| `mscorlib` | `System.Reflection.Emit.ILGenerator.Emit(OpCode)` | 동적 IL 방출 검사 |
| `mscorlib` | `System.Reflection.Emit.ILGenerator.Emit(OpCode, MethodInfo)` | 동적 IL 방출(오버로드) 검사 |
| `mscorlib` | `System.Collections.Generic.List`1.Add(!0)` | 제네릭 컬렉션 기본 연산 |
| `System.Core` | `System.Linq.Enumerable.Select` | LINQ 프로젝션 (제네릭 아리티 2) |
| `System.Core` | `System.Linq.Enumerable.Where` | LINQ 조건 필터 (제네릭 아리티 1) |
| `System.Core` | `System.Linq.Enumerable.ToList` | LINQ 컬렉션 변환 |
| `System.Core` | `System.Linq.Enumerable.Any` | LINQ 요소 검사 |
| `System.Core` | `System.Linq.Expressions.Expression.Constant` | 식 트리(Expression Tree) 구성 |
| `System.Core` | `System.Linq.Expressions.Expression`1.Compile()` | 동적 식 컴파일 |

---

## 4. Donor BCL 선택 요령 및 주의사항

손상되거나 스트리핑된 게임에 이식할 정상 BCL("Donor")을 선택할 때는 아래 원칙을 반드시 준수해야 합니다:

### 1) Unity 런타임 버전 및 .NET 프로파일 일치
* **Mono 2.0 / 3.5 프로파일 게임**: 구버전 Unity(예: Unity 5, 2017 등)의 경우 `mscorlib 2.0.0.0` 기반이므로, 같은 Mono 2.0/3.5 Donor를 사용해야 합니다. (.NET 4.6+ 어셈블리를 가져오면 런타임 부팅 불가)
* **Mono 4.x / .NET Standard 2.0 게임**: Unity 2018+ 이후 게임은 `mscorlib 4.0.0.0` 기반 Donor(또는 완전한 Unity Editor 설치 폴더 내 `PlaybackEngines/MonoBleedingEdge`)에서 추출하는 것이 안전합니다.

### 2) `mscorlib.dll` 이식의 위험성 (최후의 수단)
* `System.Core.dll`, `System.Xml.dll`, `System.Data.dll` 등의 부속 라이브러리는 교체 위험도가 낮습니다.
* 그러나 **`mscorlib.dll`은 `System.Object`, `System.String` 등 런타임 ABI의 근간**이므로, 버전이 조금만 달라도 모노 런타임과 즉각적인 충돌(Crash in libmono)을 일으킬 수 있습니다. 가급적 부속 BCL만 교체하는 것을 우선 권장합니다.

### 3) 참조 어셈블리(Reference Assembly) 절대 금지
* Visual Studio SDK 폴더(예: `C:\Program Files (x86)\Reference Assemblies\...`)의 DLL은 컴파일 타임용 메타데이터만 들어있고 실제 메서드 바디(IL 코드)가 비어있습니다.
* 이를 게임 폴더에 복사하면 런타임에 즉시 크래시가 발생하므로 반드시 **실제 런타임 실행 바이너리(Unity 배포판 등)**에서 가져와야 합니다.
