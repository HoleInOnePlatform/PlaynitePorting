# 내장 Playnite 스토어 연동

- 원본: https://github.com/JosefNemec/PlayniteExtensions/tree/886468f65724e6fd104f3a09847a17ba57cfb430
- SteamLibrary 2.44, EpicLibrary 2.30 및 공통 소스를 가져옴. MIT 라이선스는 LICENSE.md에 보존함.
- Steam의 UpstreamProcessMonitor.cs는 원본 확장이 참조한 Playnite 커밋 39e7ff05696d9f3f5561e4e62f4aa21cbb4cc2df의 source/Playnite/Common/ProcessMonitor.cs임.

## 홀인원에 맞춘 변경

- 프로젝트를 SDK 형식으로 변환하고 현재 Playnite 코어를 참조함. 원본 프로젝트 파일은 *.upstream.csproj.txt에 보관함.
- 원본 인증·계정 라이브러리·설정 화면·메타데이터·스토어 설치 감지·런처 실행을 사용함. 외부 애드온 검색 없이 두 내장 모듈만 로드함.
- XAML의 공용 타입 참조, HttpDownloader 호출과 타입 별칭을 현재 코어에 맞춤.
- Steam의 Playnite 메타데이터 서버 조회를 원본 Steam WebApiClient의 스토어 직접 조회로 대체함. 원격 구성 파일 의존성을 제거함.
- Steam 로그인 성공 시 인증 상태 갱신, 빈 토큰 거부, 인증 정보가 포함된 페이지 속성의 오류 로그 제거.
- Epic 자동 다운로드에만 Legendary를 연결함. 원본 Epic 인증으로 얻은 일회용 교환 코드를 보조 도구에 전달함. 보조 도구로 설치한 게임은 해당 도구로 실행·제거하며, Epic 런처 설치 게임의 실행은 원본 경로를 유지함.
- Legendary 설치 목록 조회 실패가 Epic 런처의 설치 목록을 지우지 않도록 분리함.

실제 SteamStoreService 로그인 처리의 회귀 검증은 tests/BuiltinLibraries.Checks에 있음. 실제 계정·네트워크에 접속하지 않고 웹뷰 응답과 로그인 완료 처리를 검증함.
