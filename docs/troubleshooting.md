# Native 종료 장애 대응

실제 x64 DLL의 결함 주입 모드로 재현한 M3b 경로다. 이 모드는 통합 테스트 전용이며 Host CLI에는 노출하지 않는다. 장애 판정은 [ADR-0009](adr/0009-termination-failure-quarantine.md)를 따른다.

| 증상 | 판별·원인 | 처리·회귀 검증 |
| --- | --- | --- |
| 취소 후에도 CancelRequested/Busy | 작업 또는 진행 관찰자가 아직 반환하지 않음 | 완료·해제를 앞당기지 않는다. `EngineStop_DrainsNativeCallbackBeforeCompletion`에서 신호 해제 후 종료 확인 |
| Faulted, TerminationConfirmed=true, RequestStop 오류 | Native 정지 요청이 실패했으나 Wait로 종료 확인 | 종료된 핸들은 해제 가능. 같은 엔진·어댑터 재사용은 거절하며 Host 재시작. `StopFailure_DrainsWorkThenFaultsEngineAndBlocksAdmission` |
| Faulted, TerminationConfirmed=false, Wait 오류 | 종료 확인 실패, 작업·콜백 사용 여부 미확인 | Dispose·GC로 강제 해제하지 않는다. 접수를 중지하고 프로세스를 재시작한다. `WaitFailure_QuarantinesHandleAndContextEvenAfterDisposeAndGc` |
| Inspect 단계 Faulted, Native progress observer failed | 진행 관찰자 예외 또는 자신의 Dispose 재진입 | 관찰자에서 자신의 완료를 기다리지 않도록 수정. 예외는 Native 밖으로 전파하지 않고 join 후 반환. `CallbackFailure_IsContainedAndJoinedBeforeReturning` |

Engine.Error와 실행 Error/StopReason/TerminationConfirmed를 함께 보관한다. Host의 Faulted 로그에는 RunId·단계·계산된 점수·종료 확인 여부가 있다. Wait 미확인 장애는 관리 측 판정 완료 시각을 Native 종료 시각으로 해석하지 않는다. 콜백이 영원히 반환하지 않거나 Native가 Wait 내부에서 멈추면 프로세스 외부의 운영 제한 시간이 필요하다. 통합 테스트에서는 30초 hang 제한과 verify의 프로세스 제한으로 검증 자체가 영원히 멈추지 않게 한다.

자세한 재현과 검증 증거는 [M3b 작업](../tasks/M03b-native-lifetime.md), [검증 대응표](verification-map.md)에 연결한다. M5의 구조화 로그·진단 확장에서 실제 운영 정보를 추가한다.
