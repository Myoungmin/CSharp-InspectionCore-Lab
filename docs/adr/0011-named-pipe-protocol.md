# ADR-0011: DTO 경계와 Named Pipe 프레임

- Date: 2026-09-27
- Status: Accepted

## Context

M3c의 Engine을 별도 콘솔 프로세스에서 제어해야 한다. Core 모델이나 예외를 직렬화하면 내부 변경이 프로토콜에 전파되고 Native 수명 책임이 Client로 새어 나간다. Pipe의 읽기 한 번은 메시지 한 개를 보장하지 않는다.

## Decision

Contracts에는 버전 1의 요청·응답 DTO, 제한값과 오류 코드를 둔다. Client는 Contracts만 참조하고 Host가 DTO와 Core를 변환한다. 공통 프레이밍 소스는 Host/Client에 링크하며 별도 프로젝트나 Contracts의 동작 구현은 추가하지 않는다.

Windows 동일 사용자·동일 권한 수준의 로컬 Named Pipe를 byte 모드로 사용한다. 4바이트 little-endian 길이와 최대 65,536바이트 UTF-8 JSON을 전송한다. 16개 비동기 서버 인스턴스, 인스턴스별 64KiB 입출력 버퍼와 프레임 왕복 10초 제한을 둔다. 첫 인스턴스 소유권으로 같은 이름의 두 번째 Host를 거절한다. 한 연결은 요청 처리 후 응답 쓰기를 직렬화하며 큰 파이프라인에는 스트림의 backpressure가 적용된다. Client 연결도 왕복을 직렬화한다.

필수 필드·null·알 수 없는 필드·중복 JSON 속성·버전·크기를 검사한다. 프레임 크기 오류·잘린 프레임·기한 초과는 해당 연결을 닫는다. 해석 불가능한 JSON envelope는 신뢰할 RequestId가 없으므로 응답 없이 닫는다. 해석 가능한 요청의 계약 오류는 해당 RequestId로 응답하고 연결을 유지한다. 프로세스 내부 예외·스택·파일 경로는 원격 응답에 싣지 않는다. Client는 응답 버전·RequestId를 검사하고 교환 실패 후 연결을 닫는다.

## Alternatives

Core 타입 직접 직렬화는 결합 때문에 배제했다. 줄 단위 JSON은 메시지 경계·크기 제한 학습 목표에 맞지 않는다. gRPC나 추가 transport 프로젝트는 로컬 실습 규모에 비해 의존성과 프로젝트 수를 늘리므로 보류한다. 이벤트 push는 아직 필요하지 않아 상태 폴링을 사용한다.

## Consequences

Host가 DTO 변환과 입력 제한을 소유한다. 프레임 크기, 연결 수와 대기 시간이 제한되며 장시간 idle 연결은 재접속해야 한다. 이는 서비스 계정·원격 호스트·다중 사용자 보안 설계가 아니다. 클라이언트 처리량 증가나 UI 구독이 필요해지면 프레이밍 프로젝트 분리와 이벤트 전달을 재검토한다.

## Validation

M4 필수 IPC 사례가 실제 프로세스, 부분/연속/손상 프레임, 버전·필드 오류, 잘못된 응답의 거절과 종료를 확인한다. 참조 규칙과 생성 SVG도 verify.ps1이 확인한다. 실제 전체 실행 증거는 작업 기록에서 구분한다.

## Links

- [M4 작업](../../tasks/M04-named-pipe-ipc.md)
- [IPC 계약](../ipc-contract.md)
- [현재 구조](../architecture.md)
- [공식 NamedPipeServerStream 문서](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream?view=net-9.0)
- [필수 JSON 생성자 인수](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/required-properties)
- [알 수 없는 JSON 멤버 검사](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/missing-members)
