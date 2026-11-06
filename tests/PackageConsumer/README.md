# 패키지 소비자 검증

새로 만든 `.nupkg`만 참조하는 격리 소비자에서 공개 API를 실행한다. 라이브러리 소스의
`ProjectReference`는 사용하지 않는다. 실행마다 `artifacts/package-consumer/<GUID>` 아래에
별도 NuGet 피드·캐시·프로젝트를 생성하며, 검증 산출물을 남긴다.

```powershell
./scripts/verify-package-consumer.ps1 -PackageDirectory artifacts/nupkg
```

입력 폴더에는 동일 버전의 `KcpCSharpR1`, `KcpCSharpR1.Transport` 패키지가 각각 하나 있어야 한다.
패키지 버전은 nuspec에서 읽는다. Source Mapping으로 이 두 패키지를 로컬 피드에 고정하고,
복원된 nupkg의 SHA-256이 입력과 같은지 확인한다. 과거에 게시한 같은 버전이나 전역 캐시가
검증을 대신할 수 없다. 외부 의존성은 `System.Threading.Channels`만 허용한다.

기본 의존성 피드는 nuget.org다. 오프라인 실행 시 Channels 패키지를 보관한 로컬 NuGet 피드를
`-DependencySource <경로>`로 지정할 수 있다. 이 검증은 패키지 소비 기능을 검증하며 NuGet
취약점 감사는 실행하지 않는다.

- **net8.0**: Transport만 직접 PackageReference한 콘솔 소비자를 복원·빌드·실행한다.
- **netstandard2.1**: 같은 검증 코드를 .NET Standard 라이브러리로 컴파일해 그 타깃의
  의존성 그래프를 검증한다. 별도의 .NET 8 호스트는 복원된 **netstandard2.1 DLL**을 명시적으로
  참조한다. 실행 중 두 라이브러리의 TargetFrameworkAttribute를 검사해 net8 DLL로 대체되지
  않았음을 확인한다. MonoGame·모바일·AOT 등 다른 런타임의 실행 검증은 아니다.

두 모드 모두 실제 루프백 UDP로 연결 도중 취소→재시도, 조각난 신뢰 메시지의 바이트·순서,
비신뢰 스냅숏, 이벤트 큐, 다른 스레드의 송신 아웃박스, FIN 종료를 확인한다.
net8 모드는 실행마다 새 키로 내장 AES-GCM을 사용한다. netstandard 모드는 세션별
`CipherFactory` 생성·방향·96바이트 오버헤드·폐기를 테스트 전용 포맷으로 검증한다.
이 테스트 포맷은 **암호화가 아니며 보안 기능을 제공하지 않는다**.
기본 포트는 27180/27181이며 `-Port`로 27001~27198 중 시작 포트를 변경할 수 있다.
각 실행은 내부 15초 제한과 프로세스 20초 제한을 두며, 실패는 비정상 종료 코드로 전달한다.
