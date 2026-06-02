# StS2 Combat Advisor

**슬레이 더 스파이어 2** 모드 — 전투 중에 게임 자체의 타겟팅 화살표로 AI 추천 플레이를 보여줍니다. 어떤 카드를 먼저 쓸지, 어떤 적을 칠지, 지금 어떤 포션을 마실지.

> 핫키도, 메뉴도, 승률 로딩도 없습니다. 매 프레임 실시간 전투를 읽어 정답을 가리킬 뿐입니다.

[English README](README.md)

---

## 기능

게임 내장 타겟팅 화살표 그래픽을 그대로 쓰는 화면 마커 3종:

- **▶ 먼저 쓸 카드** — 플래너가 이번 턴 다음으로 낼 카드 위에 초록 화살표.
- **▼ 칠 적** — 단일 적을 대상으로 하는 카드(공격 *또는* 취약 부여 같은 적 대상 스킬)에 마우스를 올리면, 플래너가 겨냥할 적 위에 빨강 화살표. 같은 이름의 적은 위치 기반으로 처리해 항상 *맞는* 적을 가리킵니다.
- **▲ 쓸 포션** — 사용할 가치가 있는 포션 밑에 색깔 화살표:
  - 🔴 **빨강** — 이번 턴 적을 *확실히 처치*하는 데미지 포션.
  - 🔵 **파랑** — 치명타를 맞기 직전이거나 최대 HP 30% 아래로 떨어질 때의 방어/회복 포션.
  - 🟢 **초록** — 버프/유틸 포션. **엘리트/보스** 방에서만 추천.

보드 상황에 따라 실시간 갱신되며, 포션은 한 번에 가장 우선순위 높은 하나만 표시합니다.

## 동작 방식

- [**sts2-combat-ai**](https://github.com/ing-gom/sts2-combat-ai) 플래너(시뮬레이터 + 스코어러)를 **인프로세스로 링크**해 실시간 전투에 직접 돌립니다 — 외부 헬퍼나 프로세스 간 통신 없음.
- 매 프레임 현재 `CombatState`(덱·손패·에너지·적·파워·포션)를 스냅샷으로 떠 플래너에 다음 최적 수를 묻고, 화살표를 해당 게임 UI 노드(`NCreature` / `NHandCardHolder` / `NPotionHolder`) 위에 배치합니다.
- 포션 추천은 플래너의 피해 예측 위에 얹은 경량 휴리스틱입니다 — 라이브 포션의 실제 효과량(`DynamicVars`)과 방 종류(Monster/Elite/Boss)를 읽어 처치/방어/버프를 판정합니다.

## 멀티플레이어

클라이언트 사이드 · 읽기 전용. 게임 상태에 쓰거나 네트워크 메시지를 보내지 않고 전투를 *읽어* 오버레이만 그리므로, 다른 플레이어에겐 변경되지 않은 게임으로 보입니다. 매니페스트는 `"affects_gameplay": false` 입니다.

## 설치

1. [GitHub Releases](../../releases)에서 최신 `Sts2CombatAdvisor-vX.Y.Z.zip`을 받습니다.
2. 압축을 풀어 `Sts2CombatAdvisor/` 폴더를 다음 위치에 넣습니다:
   ```
   <슬레이 더 스파이어 2 설치 경로>/mods/
   ```
   결과:
   ```
   <Slay the Spire 2>/mods/Sts2CombatAdvisor/Sts2CombatAdvisor.dll
   <Slay the Spire 2>/mods/Sts2CombatAdvisor/Sts2CombatAdvisor.json
   ```
3. 게임을 켜고 전투를 시작하면 화살표가 자동으로 나타납니다.

## 소스 빌드

요구사항:
- .NET SDK 9.0
- Godot.NET.Sdk 4.5.1 (자동 해석)
- 로컬 슬레이 더 스파이어 2 설치 (`Sts2PathDiscovery.props`가 자동 탐지)
- [**sts2-combat-ai**](https://github.com/ing-gom/sts2-combat-ai) 저장소를 **이 저장소 옆에 클론** — 플래너 소스를 빌드 시 `../Sts2CombatAI/Sts2CombatAICode/Core`에서 링크합니다:
  ```
  <상위 폴더>/
  ├─ Sts2CombatAI/         (sts2-combat-ai 클론)
  └─ Sts2CombatAdvisor/    (이 저장소)
  ```

```sh
dotnet build Sts2CombatAdvisor.csproj -c Release
```

빌드 시 `Sts2CombatAdvisor.dll`과 `Sts2CombatAdvisor.json`이 `<sts2>/mods/Sts2CombatAdvisor/`로 자동 복사됩니다.

## 참고 / 한계

- 추천은 자매 모드 **sts2-combat-ai** 자동 플레이가 쓰는 것과 동일한 플래너입니다 — 품질은 그 플래너의 시뮬레이션 충실도에 좌우됩니다.
- 광역/무작위 대상/자기 대상/무대상 카드는 의도적으로 적 화살표가 없습니다(겨냥할 대상이 없음).
- 포션 추천은 휴리스틱입니다 — 명확한 처치와 명확한 위기를 표시하며 모든 상황적 사용을 다루진 않습니다. 버프 포션은 노이즈를 줄이기 위해 엘리트/보스에서만 추천합니다.
- 오버레이는 게임 전투 UI 노드에 고정됩니다 — 향후 게임 패치가 그 노드명이나 타겟팅 화살표 에셋을 바꾸면 소스 갱신이 필요할 수 있습니다.

## 크레딧

- **MegaCrit** — 슬레이 더 스파이어 2.
- **[sts2-combat-ai](https://github.com/ing-gom/sts2-combat-ai)** — 이 오버레이가 구동하는 플래너 / 시뮬레이터 / 스코어러.
- **HarmonyX** — 런타임 패칭 라이브러리 (게임에 번들, 여기서 재배포 안 함).

## 라이선스

[MIT](LICENSE).
