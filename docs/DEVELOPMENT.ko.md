# Series Relay Launcher 개발자 안내서

[English](DEVELOPMENT.md) · [简体中文](DEVELOPMENT.zh-CN.md) · [繁體中文](DEVELOPMENT.zh-TW.md) · [日本語](DEVELOPMENT.ja.md) · 한국어 · [사용자 안내서](GUIDE.ko.md)

## 프로세스와 데이터 소유권

v1.0.4에서 WPF는 인스턴스, 화면, 입력 프로필과 사용자 확인을 관리합니다. 전송 도구는 메타데이터 검사·다운로드·압축 해제를 수행합니다. Python 세션 워커는 선택한 엔진을 준비하고 실행 로그를 정규화합니다. 현상을 재현할 때는 표시 이름과 함께 TitleId, 엔진 ID, 고정 커밋 및 해당 실행 폴더를 기록하세요. [데스크톱](../Source/GTA6Launcher/DesktopLauncher.cs) · [세션 워커](../Source/XCL-VIctory-Lab/desktop/session.py)

## 가져오기 상태와 설치 확정

1. `Address` 단계는 로컬 디렉터리 또는 바로 아래 `GameData` 의 매니페스트, 혹은 같은 디렉터리의 압축 세트 첫 볼륨을 찾습니다. 마그넷은 토렌트 파일 목록으로 분류합니다.
2. `Contents` 단계에서 게임 EXE 포함 여부를 묻습니다. 필요하면 `External` 에서 별도 소스를 받습니다. `Archive` 에서는 보관·삭제 방식과 출력 폴더를 기록합니다. 입력이 변경되면 이전 검사 결과를 무효화합니다.
3. 이미 풀린 데이터는 선언된 상대 경로, AMD64 PE, 해당 타이틀의 필수 RPF 네 개를 검사합니다. 아직 받아야 하는 소스는 본체와 EXE 작업을 분리한 `InstallPlan` 과 토렌트 메타데이터로 저장합니다.
4. 라이브러리에서 설치를 누르면 별도 EXE 작업, 본체 작업 순서로 처리하고 완료된 출력 경로를 저장합니다. 최종 검사가 성공하면 동일한 인스턴스 ID로 대기 레코드를 교체합니다. [소스 판별](../Source/GTA6Launcher/ImportSource.cs) · [설치 계획](../Source/GTA6Launcher/DeferredInstall.cs)

## 도전 과제 ID와 진행률

`achievement_record` 는 XUser의 `local achievement update` JSON을 읽습니다. ID는 1~256자의 문자열, 진행률은 0~100 범위의 정수입니다. 불리언, 범위 밖 값, 손상된 JSON은 진행률로 채택하지 않습니다. 시간대가 있는 시각을 보존하고 없으면 UTC 시각을 사용합니다. `forward_runtime_logs` 는 정규화된 이벤트를 `LOG` 로 전송하고 `runs/<run>/achievements.log` 에 같은 행을 추가합니다.

```text
2026-01-01T12:00:00Z [CGBJ achievement] local-progress {"achievementId":"1","percentComplete":42}
2026-01-01T12:01:00Z [CGBJ achievement] unlocked achievementId=1
```

위 두 줄은 테스트용 예입니다. `query` 와 `progress-update-request` 는 진단 이벤트로 남습니다. `AchievementJournal.ReadHistory` 는 `desktop/play-history/<instance>/Desktop-*.time` 에서 연결된 세션을 찾아 ID별 최고 진행률과 최초 달성 시각을 재구성합니다. 42% 이후 12%를 수신해도 최대치는 42%입니다. 명시적 해제는 100%로 처리하고 같은 해제 이벤트를 재생해도 알림이 반복되지 않습니다.

실시간 알림은 카탈로그에 등록된 ID가 이전 최고치를 넘어야 생성됩니다. TSV의 55개 행은 ID, 점수, 다섯 언어 이름, 같은 순서의 다섯 설명으로 구성됩니다. 로그에 ID가 보이는데 타일이 변하지 않으면 원본 로그, 정규화 로그, 인스턴스의 세션 표식, 카탈로그 순서로 확인합니다. [파서](../Source/GTA6Launcher/AchievementJournal.cs) · [카탈로그](../Source/GTA6Launcher/Assets/GtaViAchievements.tsv) · [알림 큐](../Source/GTA6Launcher/AchievementToast.cs)

## 입력 설정에서 HUD까지

편집기의 18개 입력 중 처음 16개는 두 자리 16진수 가상 키 코드로 `PAD=` 에 전달됩니다. Share와 Guide는 이 필드 밖의 단계입니다. 기본 프리셋은 F6으로 도보·운전 레이어를 바꿉니다. 같은 트리거에 연결된 동작도 현재 상황에 맞는 키를 표시할 수 있습니다. `PROMPTS=` 는 `INPUT_AIM=RMB` 와 같은 ASCII 줄 목록을 Base64로 인코딩합니다.

CGBJ, TitleId `7952987A`, 키보드 모드, 완성된 프로필, 게임 내 키 표시 옵션이 모두 필요합니다. `replace_labels` 는 지원 문자열 풀의 토큰을 원래 엔트리 범위 안에서 바꿉니다. `replace_button_glyphs` 는 Scaleform 심볼을 동작과 연결하고 외곽선·바탕·키 문양의 벡터 레이어를 재구성합니다. `create_copy` 는 원본 `data.rpf` 를 읽기 전용으로 열어 세션 복사본에 적용합니다. HUD 글리프가 0개일 때는 준비 실패로 처리하며, 텍스트 치환이 0개인 경우에는 글리프만 성공할 수 있습니다. 프리로드 이후 매핑 변경도 실행 시점에 확인합니다. [바인딩 인코더](../Source/GTA6Launcher/GameKeyPrompts.cs) · [리소스 재구성](../Source/XCL-VIctory-Lab/desktop/key_prompts.py)

## 프리로드, 저장 및 릴리스

`PRELOADING` 은 준비 시작, `COUNT` 는 셰이더 수, `PROGRESS` 는 처리된 파이프라인 수와 총수, `READY` 는 실행 명령 대기 상태입니다. `DEVICE_READY` 이후 첫 프레임 증거를 관측한 `PLAYING` 에서 실행 성공 알림을 표시합니다. 설치, 준비 시작, 준비 완료, 실행 성공 알림은 도전 과제와 별도로 대기열을 관리하고 같은 모니터에서 공간을 나눕니다. [알림 구현](../Source/GTA6Launcher/LauncherNotice.cs)

VIctory-NVIDIA는 `preload.env` 에 지원되는 F1 메뉴 값을 반영한 뒤 네이티브 프리로드를 수행합니다. `prewarm.log` 에서 셰이더 캐시와 파이프라인 라이브러리 수치를 분리해서 읽으세요. 구성 요소 복구는 내장 인덱스의 경로, 길이와 SHA-256을 검증합니다. 저장 슬롯은 `GameSaves/<instance>`, 도전 과제는 실행 저널, 인스턴스 정의는 `desktop/game-instances.xml` 에 보관합니다. [구성 요소 복구](../Source/GTA6Launcher/ComponentLibrary.cs) · [저장 슬롯](../Source/GTA6Launcher/LocalSaveLibrary.cs)

저장소 루트에서 동일한 버전의 전체 ZIP으로 입력을 복원한 뒤 빌드 및 회귀 검사를 실행합니다.

```powershell
py -3 prepare_inputs.py Updates/Launcher-1.0.4.zip --portable
dotnet build Source/GTA6Launcher/GTA6Launcher.csproj -c Release
py -3 -m unittest discover -s Source/GTA6Launcher/Tools/tests -p test_cgbj_log.py
py -3 Source/GTA6Launcher/Tools/tests/test_import_archive.py
```

배포 시 `history.json` 에 버전과 유형을 추가합니다. 전체 ZIP, 엔진 ZIP, 체크섬 2개, 업데이트 도구와 설정까지 6개 첨부를 검증합니다. 1.0.1 → 1.0.4는 중요 한 건·일반 두 건, 1.0.3 → 1.0.4는 일반 한 건입니다. 업데이트 도구는 관리 파일을 스테이징하고 교체 전 트랜잭션을 기록합니다. 중단된 설치는 백업으로 복구합니다. [전체 배포 절차](DEVELOPMENT.md#ship-a-full-update) · [트랜잭션 설치](../Source/GTA6Launcher/Updater/PackageInstall.cs)
