# ida-plugins

found/Founded 팀이 쓰는 Rhino 8 플러그인 모음이에요. 산업디자이너 에이전트 앱의 **도구 모음 → Rhino 플러그인** 탭이 이 저장소의 `plugins.json`을 읽어서, 팀원 PC에 설치하고 업데이트해 줘요.

## 플러그인

| 이름 | Rhino 명령어 | 하는 일 |
|---|---|---|
| 3export | `Export3` | 선택한 객체를 STEP·3DS·FBX로 한 번에 내보내 `(확장자)제품명_FOUNDFOUNDED` 폴더로 정리 |
| NPatch | `NPatch`, `NPatchFill` | N각형 구멍을 매끈한 NURBS 서피스 한 장으로 채우기 (G0·G1·G2) |
| ida-connect | `IdaConnect` | Rhino가 켜질 때 RhinoMCP(mcpstart)를 자동으로 켜서 에이전트와 연결 (`IdaConnect`에서 AutoConnect On/Off) |

## 폴더 구조

```
ida-plugins/
├─ plugins.json        ← 앱이 읽는 목록 (id, 버전, 설명, 파일 위치)
├─ dist/               ← 팀원에게 배포되는 설치 파일(.yak)
├─ 3export/          ← 소스(src/) + manifest.yml
├─ npatch/           ← 소스(src/) + manifest.yml
└─ ida-connect/      ← 소스(src/) + manifest.yml
```

## 에이전트 규칙 · KeyShot 환경 (`agent/`)

앱은 켜질 때 `agent/agent.json`을 읽어요.

- **규칙(`agent/system.md`)**: 에이전트가 따르는 작업 규칙이에요 (Rhino 모델링 방식, KeyShot 렌더 스타일 등). 고친 뒤 `agent.json`의 `rules.version`을 바꿔서(예: `2026-11-26.1` → `2026-11-27.1`) 푸시하면, 팀원 앱이 다음에 켜질 때 받아서 **새 대화부터** 적용해요. 앱 업데이트는 필요 없어요. 못 받으면 앱에 들어 있는 규칙을 써요.
- **KeyShot 환경(`agent/environments/`)**: `FAFA.hdz`처럼 팀이 같이 쓰는 KeyShot 환경이에요. 팀원 PC의 `문서\KeyShot Studio\Environments`에 **없을 때만** 설치하고, 이미 있는 파일은 덮어쓰지 않아요.

## 새 버전 올리는 법 (3export 예시)

1. `3export/src/Export3.csproj`와 `3export/manifest.yml`의 버전을 올려요 (예: 1.0.0 → 1.1.0).
2. 빌드하고 .yak으로 묶어요:
   ```powershell
   cd 3export\src; dotnet build -c Release -o ..\build
   cd ..; Remove-Item pkg -Recurse -ErrorAction SilentlyContinue; mkdir pkg | Out-Null
   copy build\3export.rhp pkg\; copy manifest.yml pkg\
   cd pkg; & "C:\Program Files\Rhino 8\System\Yak.exe" build --platform win
   ```
3. 만들어진 `3export-<버전>-rh8_0-win.yak`을 `dist/`에 넣어요.
4. `plugins.json`에서 그 플러그인의 `version`과 `file`을 새 버전으로 바꿔요.
5. 커밋하고 푸시하면 끝. 팀원 앱에 **업데이트**가 떠요 (`"required": true`면 앱을 켤 때 자동 설치).

## 새 플러그인 추가하는 법

1. 폴더를 하나 만들어 개발해요 (예: `npatch/`).
2. Yak으로 `.yak`을 만들어 `dist/`에 넣어요 (`manifest.yml`의 `name`이 id가 돼요).
3. `plugins.json`의 `plugins`에 한 줄 추가:
   ```json
   { "id": "npatch", "title": "Npatch", "description": "…", "version": "1.0.0",
     "command": "Npatch", "required": false, "file": "dist/npatch-1.0.0-rh8_0-win.yak" }
   ```
