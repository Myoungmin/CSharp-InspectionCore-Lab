# M3b 구현과 검증 기록

구현일: 2026-09-27. 기준 커밋: 08bec59. [작업과 인수 조건](../tasks/M03b-native-lifetime.md), [Native 계약](native-interop.md), [Engine 계약](engine-contract.md)에 연결한다. 구현 검증이며 사용자 학습 완료 기록이 아니다.

## 구현 내용

Native ABI v2에 Start/RequestStop/Wait와 진행 콜백을 추가했다. Start는 입력을 복사하고 Wait는 작업·콜백을 join한다. SafeHandle 추가 참조와 GCHandle을 함께 유지하며 종료 확인 후 해제한다. Host는 Native 진행 통지를 출력하고 Engine, 어댑터 순서로 종료한다.

종료 실패는 일반 검사 오류와 구분한다. RequestStop 실패 후 join 성공은 TerminationConfirmed=true, Wait 실패는 false이며 자원을 보존한다. 두 경우 모두 엔진의 새 접수를 차단한다. false에서 완료 Task는 관리 측 장애 판정 완료를 뜻한다.

## 실제 검증

전체 verify `20260927T051132Z-db2b34ba`에서 Core 52개와 통합 35개, 총 87개가 통과했다. 실패·skip은 0이며 콘솔·JSON·Native 배포·ADR 9건·다이어그램 7개·링크도 통과했다. 소스 해시와 이후 카운터 격리 보완의 재검증은 작업 기록에 구분했다. 대기 순서는 콜백 진입·반환 신호로 제어하고 Native 실행 중 시간 초과는 수동 타이머로 발생시킨다.

## 직접 확인할 학습 항목

- RequestStop 성공만으로 핸들을 해제할 수 없는 이유를 설명한다.
- 입력 pin과 GCHandle 문맥의 수명이 서로 다른 이유를 설명한다.
- Wait 성공과 검사 결과 성공을 구분한다.
- 종료 미확인 상태에서 Completion, TerminationConfirmed, EngineState를 함께 읽는다.
- 자신을 기다리는 진행 관찰자가 교착을 만드는 이유를 설명한다.

이번 코드는 Codex가 구현했다. 위 항목에 대한 사용자의 설명·수정·재현은 아직 확인하지 않았다. 다음 구현은 M3c 순차 자동 반복이다.
