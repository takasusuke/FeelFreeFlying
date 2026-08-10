# CLAUDE.md — FeelFreeFlying

このリポジトリは`AIFiles`直下にあるため、共通方針である[`../CLAUDE.md`](../CLAUDE.md)が自動的に
適用される。Git運用（commit/push方針・破壊的操作）、秘密情報の扱い、報告スタイル、
サブエージェントの使い分け、画像生成の運用はそちらに従う。ここには本リポジトリ固有の事項のみ書く。

## 概要

日本の街の上空を自由に飛び回って眺める体験ゲーム（Unity / PC・Steam想定）。
**セッションを再開する時は[`docs/HANDOFF.md`](docs/HANDOFF.md)から読む**（現在地・動くもの・未確認事項）。

要件定義は[`docs/requirements.md`](docs/requirements.md)が正。仕様の判断に迷ったらまずこれを読む。
各マイルストーンの手順と測定結果は[`docs/m0-plan.md`](docs/m0-plan.md)（技術検証）・
[`docs/m1-plan.md`](docs/m1-plan.md)（飛行の操作）・[`docs/m2-plan.md`](docs/m2-plan.md)（都市データの変換）・
[`docs/m3-plan.md`](docs/m3-plan.md)（見どころの抽出）。

**操縦シミュレータではない。** 判断に迷ったら「知らない街を空から見て回るのが気持ちいいか」で決める。

## 開発マシン

**Windowsを主開発機とする。** 出荷先がSteam（利用者の大半がWindows PC）であり、
**フレームレートは出荷先の実機で測らなければ判断材料にならない**ため。GPU性能もWindows機が上。

Macでも作業できるが、以下の2点により**計測は必ずWindowsで行い、2台の数値を混ぜない**。

- 都市データの生成物をコミットしない方針のため、同じシーンを開くには**各マシンで再インポート**が要る
  （→ データ管理）。2台で並行して都市データを触ると手間が増えるだけで得がない
- Unityのバージョンは2台で一致させる。マイナー差でもプロジェクトのアップグレードが走る

### Unityのバッチ実行

同じマシンで`StoneKnights`・`FightingPieces`のUnityも動く。**プロセスを数える時は必ず
プロジェクトパスで絞る** — `Get-Process Unity`は隣のリポジトリまで数えるため、そちらで
数時間の測定が走っていると待ちが終わらず、「片付ける」と判断すると他の計算を消す。

長時間の処理を1件ごとに書き出すこと、`-projectPath`を渡していても作業ディレクトリ側に
ログが落ちることと併せて、[`~/AIFiles/docs/unity-batch-runs.md`](../docs/unity-batch-runs.md)を参照。
M0での具体的な待ち方は[`docs/m0-plan.md`](docs/m0-plan.md) §2.1。

## 技術スタック

- Unity（バージョン・レンダーパイプラインはM0着手時に決定 → `docs/requirements.md` §11）
- 都市データは**Project PLATEAU**（国土交通省 / CC BY 4.0）。PLATEAU SDK for Unityを使う
- 出荷先はPC（Steam）を第一候補とするが、**M0の実測まで確定しない**

## 設計上の不変条件

以下は後から直すコストが極端に高いため、最初から守る。

1. **カリング・LOD・ストリーミングを前提に組む** — 「まず全部読み込んで、後で最適化する」を
   やらない。都市規模のシーンでは後付けできない（→ `docs/requirements.md` §7）。
2. **都市データの取り込みを手作業にしない** — CityGML → ゲーム用データの変換は必ずスクリプト化し、
   コマンド一発で再現できる状態を保つ。都市追加のたびにEditor上の手作業が要る構造にしない。
3. **航空力学を再現しない** — 失速・迎え角・エンジン出力といったモデルを持ち込まない。
   浮遊感が優先。落下でゲームオーバーにしない。
4. **実在建築物の名称・ロゴ・看板を出さない** — 形状は出してよい。名前は出さない
   （→ `docs/requirements.md` §4.2）。
5. **PLATEAUの出典表示を外さない** — ライセンス表記はゲーム内に常設し、都市を追加したら
   その都市のライセンス条件を確認して追記する。
6. **写実を目指さない** — データの粗さ（LOD1・テクスチャ無し）を前提に、様式化された見た目で
   設計する（→ `docs/requirements.md` §5）。
7. **見どころ・コース・依頼を手で置かない** — 都市ごとの手作業になり、都市追加が止まる。
   配置は建物属性と地形からの計算で決める（→ `docs/requirements.md` §2.1、`docs/m3-plan.md`）。
   **禁じているのは手作業であって、遊びの種類ではない。** 依頼やコースも、生成でまかなえて
   都市追加の手数が増えないなら入れてよい（→ `docs/requirements.md` §12.5）。
   対話ツリーのような1件ずつ書く作りは、その時点で条件を満たさない。

## データ管理

- **PLATEAUの生データ（CityGML等）をリポジトリにコミットしない。** サイズが大きく、
  再取得可能なため。取得元URLと取得手順、変換スクリプトの側をコミットする。
- 変換後のゲーム用データも、サイズ次第ではGit管理から外す判断をする（M2で決める）。
- 購入モデル・音源は、ライセンス条件（再配布可否・商用利用可否）を一覧で管理する。
  出所不明の資産をコミットしない。

公開前のチェック項目は[`~/AIFiles/docs/legal-review.md`](../docs/legal-review.md)「著作権チェック」に従う。
本リポジトリで特に効くのは次の2つ。

- **PLATEAUの出典表示はCC BY 4.0の利用条件そのもの** — 設計上の不変条件5は「行儀の問題」ではなく、
  外すと利用許諾自体が切れる。都市を追加したらその都市の条件を確認して追記する
- **フォントの埋め込み条件** — TextMesh Proでフォントアトラスを生成する行為は「埋め込み」に当たる。
  表示は可でも埋め込みは不可、というライセンスが多い

## マイルストーンの扱い

**M0（技術検証）とM1（飛行の操作）を飛ばして先の工程に進まない。** M0が済むまで工数は読めず、
M1が面白くないなら企画自体を中止する。この2つは順序を入れ替えてもよいが、省略はしない。

## iOS/TestFlight配信（土台のみ、未実働）

出荷はSteam（PC）が第一候補だが、iOS/TestFlightにもいずれ出す計画があるため、2026-08-08に
先行してApple Developer側の土台だけ用意した。詳細・共通の運用ルールは
[`~/AIFiles/docs/testflight-release.md`](../docs/testflight-release.md)を参照。

- Bundle ID `dev.appfactory.feelFreeFlying` 登録済み（Apple Developer Identifiers）
- GitHub Secrets設定済み（`ASC_KEY_ID`/`ASC_ISSUER_ID`/`ASC_PRIVATE_KEY`/`TESTFLIGHT_TESTER_EMAIL`/
  `IOS_DIST_CERT_P12`/`IOS_DIST_CERT_PASSWORD`。証明書はAppFactory/NumberBullet2/StoneKnightsと
  同じApple Developerチーム(FEPN2STZZX)の使い回し）
- `.github/workflows/testflight.yml`は`workflow_dispatch`のみで、実行すると意図的に失敗する
  （Unity側の準備が無いため）。**実際に動かす前に必要な作業はworkflowファイル冒頭のコメントを参照**
  （Player SettingsのBundle Identifier設定、Build Settingsへのシーン追加、Xcodeプロジェクト書き出し
  用Editorスクリプトの実装、`Builds/ExportOptions.plist`作成、App Store ConnectでのApp手動登録）
- App Store ConnectのApp登録はAPIから作成不可（Appsリソースはどのロールのキーでも403）と確認済み。
  Web UIでの手動登録が必須
