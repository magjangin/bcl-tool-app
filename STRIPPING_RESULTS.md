# 설치 게임 BCL 스트리핑 검사 결과

2026-09-09, H:\steam 읽기 검사. Unity 후보 757개를 약 44초에 검사했다. 디스크 캐시와 하드웨어에 따라 시간이 달라진다.

- 스트리핑 의심 10개 / BCL 파일 누락 1개 / 표본 API 통과 591개 / 판정 보류 34개 / 대상 외 121개
- 14개 표본 API의 타입 및 메서드 시그니처를 검사했다. 누락은 실제 확인했으나 제거 원인이 반드시 스트리핑이라는 확정 판정은 아니다.
- 실제 게임 파일을 수정하거나 실행하지 않았다.

| 게임 | 누락 표본 API | 확인된 근거 |
|---|---:|---|
| Röki | 4/14 | [메서드 누락] mscorlib.dll → System.Reflection.Emit.DynamicMethod.GetILGenerator()<br>[메서드 누락] System.Core.dll → System.Linq.Enumerable.ToList(System.Collections.Generic.IEnumerable`1<!!0>)<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| Geometry Arena | 3/14 | [메서드 누락] mscorlib.dll → System.Reflection.Emit.DynamicMethod.GetILGenerator()<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| GRIME | 2/14 | [메서드 누락] System.Core.dll → System.Linq.Expressions.Expression.Constant(System.Object)<br>[메서드 누락] System.Core.dll → System.Linq.Expressions.Expression`1.Compile() |
| Insurmountable | 2/14 | [타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| Lovux | 2/14 | [타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| MiniMetro | 2/14 | [타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| Quadrata | 2/14 | [타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| Slingbot Survivors | 2/14 | [타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| Voxelgram | 2/14 | [타입 누락] System.Core.dll → System.Linq.Expressions.Expression<br>[타입 누락] System.Core.dll → System.Linq.Expressions.Expression`1 |
| Citizens | 1/14 | [메서드 누락] mscorlib.dll → System.Reflection.Emit.DynamicMethod.GetILGenerator() |

In Falsus Demo는 mscorlib.dll 및 System.Core.dll 파일이 없어 별도 파일 누락으로 분류했다.

Neon Abyss와 Zombie Rollerz의 **현재 DLL은 14개 표본 API를 모두 통과**했다. 사용자가 설명한 최초 상태의 스트리핑 이력과 현재 파일 검사는 구분해야 한다. 이미 이식된 파일만으로 원래 파일의 삭제 내역을 알아낼 수 없다.

판정 보류에는 System.Core 2.0.5.0 같은 지원 범위 밖 프로파일이 포함된다. 표본 검사이므로 그 외 API가 삭제되었거나 메서드 본문만 변형된 경우는 놓칠 수 있다.

전체 파일별 근거: [JSON 결과](stripping-scan-results.json)
