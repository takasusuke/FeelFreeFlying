# 引き継ぎ（2026-08-10 時点）

次のセッションが**最初に読む1枚**。詳細は各マイルストーンの計画書にあるので、
ここには「今どこにいるか」「何が動くか」「何が未確認か」だけを書く。

要件定義は [`requirements.md`](requirements.md) が正。
[`m0-plan.md`](m0-plan.md)（技術検証）/ [`m1-plan.md`](m1-plan.md)（飛行の操作）/
[`m2-plan.md`](m2-plan.md)（都市データの変換）/ [`m3-plan.md`](m3-plan.md)（見どころの抽出）。

---

## 1. 現在地

| | 状態 |
|---|---|
| **M0 技術検証** | 完了。**続行**の判断。出荷先はPC（Steam）、URP 17.3.0 |
| **M1 飛行の操作** | 完了。**続行**の判断。操作方式・速度域・一人称を確定。キーコンフィグ実装済み |
| **M2 変換パイプライン** | **ほぼ完了。** 1コマンドで街ができ、再現性も確認済み。残りは§3の未確認事項 |
| **M3 見どころの抽出** | **実装済み・未検分。** 5ルールで30個が出て、光の柱まで載った。飛んでの判断がまだ |

**M2の合否（都市を人手なしで増やせるか）は満たしている。** 新宿3km四方・26,559棟が
1コマンドで生成でき、同じ入力から同じ結果が出ることも確認した（`m2-plan.md` §2）。

**M3も人手ゼロで30個のスポットが出た**（内訳は`m3-plan.md` §8.1）。ただし
**M3の合否は「人が置いていない目的地に向かって飛びたくなるか」**なので、
数が出ただけでは判断できない。飛ぶまで未決。

---

## 2. 今すぐ動くもの

```
Build\M2Flight\FeelFreeFlying-M2Flight.exe   # 試遊（3km四方・遠景あり・2,233MB）
```

- 新宿3km四方を、近くのタイルだけ読みながら飛べる（近景6枚まで＋遠景）
- ランドマーク40棟のうち35棟に実写テクスチャ
- OPTIONS / Tab で**キーコンフィグ**（ボタン配置・機首/視点の上下反転）
- **見どころ30個に光の柱**。近づくと消え、画面左下に種類と距離が出る（`m3-plan.md` §4）

**都市データはgit管理外**（`Assets/Scenes/Tiles/`・`TilesFar/`・`Data/Plateau/`）。
別マシンや作り直しの時は下記で再生成する。

```powershell
$U = "C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe"
$P = "C:\Users\takasu\WorkingSpace\AIFiles\FeelFreeFlying"
$G = "53394525,53394526,53394527,53394535,53394536,53394537,53394545,53394546,53394547"

# 近景タイル（取り込み〜外壁〜当たり判定〜位置表〜カリング〜属性〜検証）9枚で約60分
& $U -projectPath $P -batchmode -logFile tiles.log `
  -executeMethod FeelFreeFlying.EditorTools.M2TilePipeline.BuildTiles -ffm2tiles-grid $G

# ランドマークの実写テクスチャ（対象タイルだけ取り込み直す）約40分
& $U -projectPath $P -batchmode -logFile landmark.log `
  -executeMethod FeelFreeFlying.EditorTools.M2Landmarks.Apply -ffm2tiles-grid $G -fflandmarks 40

# 遠景タイル 9枚で約15分
& $U -projectPath $P -batchmode -logFile far.log `
  -executeMethod FeelFreeFlying.EditorTools.M2FarTiles.Build -ffm2tiles-grid $G

# 見どころの抽出（タイルを開いて測る → 5ルール）約5分
& $U -projectPath $P -batchmode -quit -logFile spots.log `
  -executeMethod FeelFreeFlying.EditorTools.M3SpotExtract.Build -ffm2tiles-grid $G

# 試遊ビルド
& $U -projectPath $P -batchmode -quit -logFile build.log `
  -executeMethod FeelFreeFlying.EditorTools.M2StreamingBuild.BuildWindows64
```

**`-quit`を付けるのはビルドだけ。** 取り込み系は非同期なので、付けると途中で終了する。
完了は**プロジェクトパスで絞ったプロセス数**で見る（→ §5）。

---

## 3. 未確認・未解決（次の担当が引き継ぐもの）

### 3.1 fpsの測り直し（**機械が空いている時に、まとめて1回**）

**8月10日午前の計測はすべて無効。** Androidエミュレータ（qemu）とFlutterのツールが
動いており、遠景を隠しても隠さなくても45fps前後という結果になった
（隠したほうが遅い、という逆転まで出た）。同じ条件を早朝に測った時は94.4fps。

測り直したいのは3つ。**同じセッションで連続して取ること**（別の日の数字を並べない）。

| 何を | どう |
|---|---|
| 遠景タイルは速いのか | `-ffm2bench-farbuildings-off` / `-ffm2bench-farground-off` で隠して比較 |
| 遠景の影を切る効果 | 既に切ってある。切る前との比較は取り直し |
| 当たり判定の焼き込み | `PlayerSettings.bakeCollisionMeshes`。読み込み時間は4割縮むが最悪フレームは不明（→ `m2-plan.md` §6.2） |

```powershell
# 計測ビルド（半径・高度を指定）
& $U -projectPath $P -batchmode -quit -logFile bench.log `
  -executeMethod FeelFreeFlying.EditorTools.M2StreamingBenchmarkBuild.BuildWindows64 `
  -ffm2bench-radius 800 -ffm2bench-height 300
Build\M2Bench\FeelFreeFlying-M2Bench.exe -ffbenchmark-quit -ffbenchmark-nohud -ffbenchmark-label <名前>
# 結果: %USERPROFILE%\AppData\LocalLow\DefaultCompany\FeelFreeFlying\m0-benchmark\
```

**測る前に重いプロセスを確認する。** 同条件でも±10%ばらつく。

### 3.2 タイル読み込みの引っかかり

3km四方で1% lowが50前後に張り付く。1周22回の出し入れが原因。
**オクルージョンデータは無関係と確認済み**（0.253→0.237秒）。当たり判定の焼き込みは
読み込み時間を4割縮めるが、フレーム時間への影響は未確定。
**残る容疑はGPU転送とシェーダの初回コンパイル**で、ここから先はプロファイラが要る。

### 3.3 遠景の見た目

**まだ誰も見ていない。** 動作（近景4枚＋遠景5枚で街全体が揃う）はログで確認済みだが、
「街が水平線まで続いて見えるか」「近景と遠景の切り替わりが気になるか」は
**飛んで判断する**しかない。fpsに関係なく今すぐ判断できる。

### 3.4 見どころが面白いか（**M3の合否そのもの**）

30個の内訳は`m3-plan.md` §8.1。**数は出たが、良いかどうかは飛ばないと分からない。**
見てほしいのは3つ。

- 光の柱に**向かいたくなるか**。太さ・高さ・消える距離は仮の値（`SpotBeacons`の各項目）
- 着いた先に**何かあると分かるか**。分からないならルールかスコアの側を直す
- 30個は**多すぎ・少なすぎないか**（`-ffm3-density`で変えられる）

**空白（R4）は1個しか出ていない。** 道路を空白から除く処理を入れてもこの結果なので、
新宿御苑・明治神宮外苑は今の9メッシュの外にあると見ている（`m3-plan.md` §8.1）。

### 3.5 ランドマークの5棟

上位40棟のうち5棟は実写が付いていない（LOD1補完で元データにテクスチャが無い）。
**選定側は直してある**——取り込み時にLOD2の建物を記録し、そこからだけ選ぶ。
ただし**今あるタイルには記録が無い**ので、効くのは次に取り込み直した時から。

---

## 4. 次にやること（優先順）

1. **飛んで確かめる**（見どころ・遠景の見た目・キーコンフィグ・3km四方の広さ）。
   判断待ちが一番の律速で、**M3の合否がここに掛かっている**（§3.4）
2. fpsの測り直し（§3.1）。機械が空いた時にまとめて
3. コース生成（`m3-plan.md` §5）。スポットが出たので着手できる

**M4（2都市目）の前に決まっていること**: 都市は地続きに並べる（ステージ切り替えにしない）。
海の上に5km間隔、世界は原点±20kmに収める（→ `requirements.md` §12.1）。
実装は**都市ごとに取り込みの基準点をずらすだけ**で足りる見込み。

---

## 5. このマシン固有の注意

**同じPCで`StoneKnights`・`FightingPieces`のUnityも動く。**

- **プロセスをプロジェクトパスで絞る。** `Get-Process Unity`で数える／killすると
  隣のリポジトリの数時間の測定を巻き込む
- **こちらのUnityを起動すると、隣のUnityが落ちることがある。** 原因は特定できていない
  （ライセンスログに証跡なし・別バージョン間でも発生）。8月10日には
  `StoneKnights`の測定が5回連続で中断した。**起動前に他プロジェクトのUnityを確認し、
  短い起動を繰り返さない**（ビルドと計測は1回の起動にまとめる）
- 詳細は [`~/AIFiles/docs/unity-batch-runs.md`](../../docs/unity-batch-runs.md)

---

## 6. 落とし穴（同じ失敗を繰り返さないために）

| 罠 | 出典 |
|---|---|
| タイルごとに取り込むと**全部が原点に重なる**（SDKが取り込み範囲の中心を原点にする） | `m2-plan.md` §4.2 |
| 高さは`-9999`、階数は`9999`で欠測を表す。**空欄ではない** | `m3-plan.md` §1.1 |
| バッチモードでは`umbraDataSize`が0を返す。**生成物をディスクで測る** | `~/AIFiles/docs/unity-batch-runs.md` |
| オクルージョンの設定は**シーンを開いた後**に代入する（開く前だと上書きされる） | 同上 |
| 読み込んだだけで描画しないと、GPU転送が計上されず**速すぎる数字**が出る | `m2-plan.md` §3.1 |
| ビルド出力を同じフォルダに置くと、ログのサイズが**古いビルドを含む** | `m2-plan.md` の`M2StreamingBuild` |
| PowerShellの複数行commit messageは`git commit -F`（BOM無し）で渡す | `~/AIFiles/CLAUDE.md` §12 |
