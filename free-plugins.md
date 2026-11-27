# 무료 공개 플러그인 사용법

앱의 **도구 모음 → Rhino 플러그인** 탭에서 설치할 수 있는 무료 플러그인이에요. 모두 Rhino 공식 패키지 저장소(Yak)에서 받아요. 설치한 뒤 **Rhino를 껐다 켜야** 나타나요.

| 플러그인 | 어디에 생기나 | 한 줄 요약 |
|---|---|---|
| byRhinoGadget | Rhino 명령어와 툴바 | 모델링 편의 명령 80여 개 (필렛, G3 매칭, 버튼, 무게 계산 등) |
| Parakeet | Grasshopper › **Parakeet** 탭 | 패턴 생성 (타일링, 벽돌, 에셔, 균열, 잎맥, 꽃무늬) |
| LunchBox | Grasshopper › **LunchBox** 탭 | 곡면 패널링 (다이아몬드, 육각, 삼각, 사각) |
| Pufferfish | Grasshopper › **Pufferfish** 탭 | 트윈과 블렌드 (두 형태 사이를 단계적으로 바꾸기) |
| SnappingGecko | Grasshopper 캔버스 메뉴 | 컴포넌트 정렬 스냅 (캔버스 정리) |
| gh-dark-mode | Grasshopper 전체 화면 | 다크 테마 |

---

## 1. byRhinoGadget (Rhino 명령어)

플러스플라스틱(한국 개발사)에서 만든 모델링 편의 명령 모음이에요. 명령창에 이름을 치면 바로 실행돼요. 툴바가 안 보이면 `Toolbar` 명령에서 **byRhinoGadget**을 켜세요.

**제품 디자인에 바로 쓸 만한 명령**

| 명령어 | 하는 일 |
|---|---|
| `FilletCornerCutting` / `FilletCornerCuttingAll` | 커브 모서리 하나 또는 전부를 둥글게 깎기 |
| `FilletSmooth` | 부드러운(곡률 연속) 필렛 커브 |
| `RoundedRectangle` / `RoundedRectangleSmooth` | 끝점 두 개와 지름으로 둥근 사각형. Smooth는 곡률이 이어지는 모서리 |
| `FullRoundCrv` / `FullRoundSmooth` | 양 끝이 완전히 둥근 커브 (슬롯, 버튼 외곽) |
| `MatchG3` / `MatchSrfG3` | 커브나 서피스를 G3로 이어 붙이기 |
| `EndBulgeG3` | 끝을 G3로 유지한 채 불룩함 조절 |
| `FairCrvPts` / `FairSrfPts` | 제어점 수를 유지하며 커브나 서피스 매끈하게 |
| `SymmetricCrv` / `SymmetricSrf` | 커브나 서피스 제어점을 대칭으로 맞추기 |
| `mkButtons` / `mkButtonsDome` | 서피스 위에 버튼, 돔 버튼 만들기 |
| `mkEngrave` | 양각이나 음각 (로고, 글자) |
| `mkGraphics` | 서피스에 그래픽 얹기 |
| `StepExtrude` | 단차를 두고 계단식으로 돌출 |
| `PipeSmooth` | 이음새가 매끈한 파이프 |
| `WeightSolid` / `WeightDensity` | 소재 밀도를 넣고 무게 계산 |
| `DistanceBetween2Faces` | 두 면 사이 거리 (두께 확인) |
| `ExplodedView` | 분해도 배치 |
| `OrientByFlatFaceToXY` | 평평한 면을 바닥(XY)에 눕히기 |
| `MoveToZLevel` | 지정한 Z 높이로 이동 |
| `Randomize` | 위치, 색, 크기, 회전을 무작위로 |
| `SelRandom` | 무작위로 일부만 선택 |
| `CountObjects` | 객체 개수 세기 |
| `SyncDisplayModes` | 모든 뷰의 표시 모드 맞추기 |
| `SlideForward` / `SlideBackward` | 저장된 뷰를 슬라이드처럼 넘기기 |

전체 목록과 설명은 [byRhinoGadget 도움말](https://www.byrhino3d.com/byRhinoGadget/help/index.html)에 있어요.

**예: 둥근 버튼 만들기**
1. 버튼을 놓을 서피스와 버튼 외곽 커브를 준비해요 (`RoundedRectangleSmooth`로 그리면 편해요).
2. `mkButtonsDome`를 실행하고 서피스와 커브를 골라요.
3. 높이와 필렛 값을 옵션에서 바꿔요.

---

## 2. Parakeet (Grasshopper · 패턴 생성)

알고리즘 패턴 전용 플러그인이에요. Grasshopper를 열면 **Parakeet** 탭이 생겨요.

**주요 컴포넌트**

| 분류 | 컴포넌트 | 만드는 것 |
|---|---|---|
| 타일링 | Uniform Tiling B·D·F | 정다각형 반복 타일 |
| 벽돌 | Brick Type A~J | 벽돌 쌓기 패턴 10종 |
| 헤링본 | Pattern Genotype O | 헤링본 (모듈 길이, 폭, 행, 열) |
| 에셔 | Escher Quad A, Escher Hex A·B, Meta Escher | 에셔식 맞물림 타일 |
| 오각형 | Pattern Genotype A~H, M, Q | 오각형 타일링 등 셀 패턴 |
| 랜덤 분할 | Gilbert Tessellation A·B·C | 직선 균열식 분할 |
| 자연 | Fracture Pattern A, Venation I, Differential Growth | 깨짐, 잎맥, 성장 패턴 |
| 장식 | Floral Pattern, Arabesque Pattern, Cymatic Pattern | 꽃무늬, 아라베스크, 파동 무늬 |
| 등고선 | Marching Square Pattern | 값에 따른 등고선 무늬 |
| 보조 | Dual Graph, Curve to Arcs and Lines, Offset Brep, Brep Split | 패턴 후처리 |

**기본 흐름 (면에 패턴 타공하기)**
1. Rhino에서 패턴을 넣을 평면 사각형(또는 서피스)을 그려요.
2. Grasshopper에서 `Surface` 파라미터로 그 면을 가져와요.
3. Parakeet › **Brick Type A**(또는 Uniform Tiling)에 연결하고 Count(반복 수)를 정해요.
4. 나온 셀 커브를 `Offset Curve`로 안쪽으로 줄여 구멍 커브를 만들어요.
5. `Extrude` 후 원래 판과 `Solid Difference`를 하면 타공판이 돼요.
6. 곡면에 입히려면 평면에서 패턴을 만든 뒤 Rhino `Flow` 또는 `FlowAlongSrf`로 옮겨요.

---

## 3. LunchBox (Grasshopper · 곡면 패널링)

곡면을 바로 패널로 나눠 주는 플러그인이에요. 그래스호퍼 **LunchBox** 탭 › Panels.

**주요 컴포넌트**

| 컴포넌트 | 만드는 것 |
|---|---|
| Diamond Panels | 마름모 패널 |
| Hexagon Cells | 육각 셀 (벌집) |
| Quad Panels / Staggered Quad Panels | 사각 / 엇갈린 사각 (타일, 벽돌) |
| Random Quad Panels | 무작위 크기 사각 |
| Skewed Quads | 기울어진 사각 |
| Triangular Panels A·B·C | 삼각 패널 3종 |
| Diamond Grid / Quad Grid | 패널 꼭짓점만 점으로 (직접 모양을 만들 때) |
| Shell Brep | 솔리드를 두께만 남기고 비우기 |
| Rebuild Surface / Reverse Surface Direction | 서피스 정리 |
| Quad Remesh | 메쉬를 사각 면으로 다시 만들기 |

**기본 흐름 (곡면에 육각 그릴)**
1. 곡면을 `Surface` 파라미터로 가져와요 (트림 안 된 서피스가 잘 돼요).
2. LunchBox › **Hexagon Cells**에 연결하고 U, V 개수를 정해요.
3. 나온 Cells(닫힌 폴리라인)를 `Scale` 또는 `Offset on Srf`로 줄여요.
4. 곡면에서 `Split` 하거나 Extrude해서 빼면 육각 그릴이 돼요.

---

## 4. Pufferfish (Grasshopper · 트윈과 블렌드)

두 형태 사이를 단계적으로 바꾸는 데 특화된 플러그인이에요. 그래스호퍼 **Pufferfish** 탭.

**주요 컴포넌트**

| 컴포넌트 | 하는 일 |
|---|---|
| Tween Two Curves | 커브 A에서 B로 단계별 중간 커브 |
| Tween Through Curves | 여러 커브를 차례로 지나는 중간 커브 |
| Tween Two Surfaces | 서피스 A에서 B로 중간 서피스 |
| Tween Two SubDs (WIP 그룹) | SubD 두 개 사이 중간 형태 |
| Average Curve / Weighted Average | 여러 형태의 평균 |
| Mirror Cut / Mirror Combine | 대칭 자르기, 합치기 (숫자, 색, 벡터, 형태) |
| Scale To Area / Scale To Volume | 원하는 면적이나 부피에 맞춰 크기 조절 |
| Tween Through Colors | 색 그라데이션 |

**예: 원에서 둥근 사각으로 변하는 패턴 구멍**
1. Rhino에서 원 하나와 둥근 사각 하나를 그려 각각 `Curve`로 가져와요.
2. Pufferfish › **Tween Two Curves**에 A, B를 연결해요.
3. Factor에 `Range`(0~1, 10단계)를 넣으면 원에서 사각까지 11개 형태가 나와요.
4. 이걸 배열하면 위치에 따라 모양이 점점 바뀌는 타공 패턴이 돼요.
5. **Scale To Volume**은 "부피 500ml로 맞춰줘" 같은 용기 작업에도 쓸 수 있어요.

---

## 5. SnappingGecko (Grasshopper · 캔버스 정리)

컴포넌트를 끌 때 다른 컴포넌트 가장자리나 가운데에 착 붙게 해 줘요. 설치하면 그래스호퍼 메뉴 막대에 **SnappingGecko** 메뉴가 생겨요.

- **Toggle object snap**: 스냅 켜기/끄기
- **Snap to edges**: 가장자리끼리 정렬
- **Snap to centers**: 가운데끼리 정렬
- **Snap to wires**: 연결선이 곧게 되도록 정렬

정의 파일이 커질수록 효과가 커요. 따로 쓸 컴포넌트는 없어요.

---

## 6. gh-dark-mode (Grasshopper · 다크 모드)

Grasshopper 캔버스와 컴포넌트를 어두운 테마로 바꿔 줘요. Rhino 8.24 이상에서 돼요.

1. Grasshopper 캔버스에 **GH Dark Mode** 컴포넌트를 올려요 (캔버스 빈 곳 더블클릭 → `Dark Mode` 검색).
2. 입력에 `Button` 또는 `Boolean Toggle`을 연결하고 **Dark Mode / Light Mode**를 골라 적용해요.
3. **Grasshopper를 껐다 켜야** 모든 화면 요소에 적용돼요.
4. 원래 밝은 테마로 돌아가려면 같은 컴포넌트에서 Light Mode를 적용해요. 꼬였을 때는 Factory reset 옵션이 있어요.
5. 색을 직접 바꾸려면 **GH Dark Mode Override** 컴포넌트에 바꿀 항목과 색을 넣어 연결해요.
- 자세한 설명: [github.com/puya/gh-dark-mode](https://github.com/puya/gh-dark-mode)

---

## 앱 탭에서 설치되지 않는 것

아래는 Yak 저장소에 없어서 탭으로 설치할 수 없어요. 필요하면 [food4Rhino](https://www.food4rhino.com)에서 로그인 후 직접 받아야 해요.

- **Vectorize** (이미지 → 커브)
- **Treesloth** (데이터 트리 정리)
- **Blindfold** (참조 객체 숨기기)
- **Moonlight** (그래스호퍼 다크 모드). 대신 위의 `gh-dark-mode`를 쓰세요.
