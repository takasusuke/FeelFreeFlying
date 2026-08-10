using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 近くのタイルだけを読み込む（docs/m2-plan.md §4、要件 §7）。
    ///
    /// **街を全部置く方式は3km四方で60fpsを割る**（→ `m0-plan.md` §5.1）ので、
    /// 都市を増やすには近くだけ読むしかない。タイル1枚の読み込みは実測0.04〜0.08秒で、
    /// 1枚を横切る7秒に対して桁違いに速い（→ `m2-plan.md` §3）。
    /// **したがって間に合うかどうかではなく、同時に何枚置くかが設計の焦点になる。**
    ///
    /// 読み込みの判断は距離と進行方向で行う。速度が乗っているほど前方を広く見る。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileStreamer : MonoBehaviour
    {
        [Tooltip("この人の周りを読み込む。未設定ならFlightControllerを探す")]
        [SerializeField] private Transform viewer;

        [Tooltip("この距離まで近づいたら読み込む (m)。**進行方向は別に先読みする**ので狭くてよい")]
        [SerializeField, Min(0f)] private float loadDistance = 500f;

        [Tooltip("この距離まで離れたら破棄する (m)。読み込みより広くしないと境界で出入りを繰り返す")]
        [SerializeField, Min(0f)] private float unloadDistance = 900f;

        [Tooltip("進行方向を何秒先まで見るか。速度×この秒数だけ前方の判定を甘くする")]
        [SerializeField, Min(0f)] private float lookAheadSeconds = 10f;

        [Tooltip("同時に置いてよい枚数。**6枚で実測**（→ m2-plan.md §6）。9枚置くと60fpsを割る")]
        [SerializeField, Min(1)] private int maxLoaded = 6;

        [Tooltip("枠が埋まっている時、入れ替えに必要な距離の差 (m)。小さいと出し入れを繰り返す")]
        [SerializeField, Min(0f)] private float evictMargin = 500f;

        [Tooltip("遠景タイルを置く距離 (m)。**近景だけだと街が1〜1.5kmで途切れる**（→ m2-plan.md §4.6）")]
        [SerializeField, Min(0f)] private float farDistance = 6000f;

        [Tooltip("同時に置いてよい遠景の枚数。1メッシュずつなので近景よりずっと多く置ける")]
        [SerializeField, Min(1)] private int maxFarLoaded = 64;

        [Tooltip("判定の間隔 (秒)。毎フレーム全タイルを見る必要はない")]
        [SerializeField, Min(0f)] private float checkInterval = 0.25f;

        [Tooltip("メモリを実際に返す間隔 (秒)。**破棄のたびに返すと引っかかる**（→ m2-plan.md §5.1）")]
        [SerializeField, Min(0f)] private float releaseInterval = 20f;

        [Tooltip("読み込み状況を画面に出す。M2の確認用")]
        [SerializeField] private bool showStatus = true;

        private readonly Dictionary<string, TileState> states = new Dictionary<string, TileState>();

        /// <summary>置かれている遠景タイル。近景と違い出し入れが軽いので状態は持たない。</summary>
        private readonly HashSet<string> farStates = new HashSet<string>();
        private TileCatalog catalog;
        private Vector3 previousPosition;
        private Vector3 velocity;
        private bool busy;
        private bool releasePending;
        private float lastReleaseTime;

        /// <summary>今いくつ置かれているか。HUDと計測から読む。</summary>
        public int LoadedCount { get; private set; }

        /// <summary>
        /// 破棄のたびに<see cref="Resources.UnloadUnusedAssets"/>を呼ぶか。
        /// **切ると引っかかりは減るがメモリが戻らない。** どちらを取るかは計測で決める（M2）。
        /// </summary>
        public bool ReleaseAssetsOnUnload { get; set; } = true;

        public int TileCount => catalog?.Tiles.Count ?? 0;

        private enum TileState
        {
            Loading,
            Loaded,
            Unloading,
        }

        /// <summary>読み込む距離を上書きする。計測で「同時に何枚まで置けるか」を詰めるため。</summary>
        public void OverrideDistances(float load, float unload)
        {
            loadDistance = load;
            unloadDistance = unload;
        }

        private void Awake()
        {
            catalog = TileCatalog.Load();

            if (viewer == null)
            {
                var controller = FindAnyObjectByType<FlightController>();
                if (controller != null) viewer = controller.transform;
            }

            if (viewer != null) previousPosition = viewer.position;
        }

        private IEnumerator Start()
        {
            // **最初の1枚は読み終わるまで待つ。** 足元が無い状態で始まると、
            // 開始直後に空中へ放り出されたように見える
            yield return UpdateTiles(waitForFirst: true);
            yield return UpdateFarTiles();

            while (true)
            {
                yield return new WaitForSeconds(checkInterval);
                yield return UpdateTiles(waitForFirst: false);
                yield return UpdateFarTiles();
                yield return ReleaseIfDue();
            }
        }

        private void Update()
        {
            if (viewer == null) return;

            // FlightControllerの速度を直接見ない。歩行・落下・飛行で持ち主が変わるため、
            // 位置の差分から取れば取りこぼしがない
            Vector3 position = viewer.position;
            if (Time.deltaTime > 0f)
            {
                velocity = Vector3.Lerp(velocity, (position - previousPosition) / Time.deltaTime, 0.2f);
            }

            previousPosition = position;
        }

        private IEnumerator UpdateTiles(bool waitForFirst)
        {
            if (viewer == null || catalog == null || busy) yield break;

            Vector3 position = viewer.position;
            Vector3 ahead = new Vector3(velocity.x, 0f, velocity.z) * lookAheadSeconds;

            var wanted = new List<(TileCatalog.TileEntry entry, float score)>();

            foreach (TileCatalog.TileEntry entry in catalog.Tiles)
            {
                // 現在地からの距離と、進行方向の先から見た距離の小さいほう。
                // **前に進むほど前方のタイルが早く読まれる**
                float distance = Mathf.Min(
                    entry.HorizontalDistanceTo(position),
                    entry.HorizontalDistanceTo(position + ahead));

                bool loaded = states.ContainsKey(entry.sceneName);

                if (!loaded && distance <= loadDistance) wanted.Add((entry, distance));
                else if (loaded && distance > unloadDistance) yield return Unload(entry);
            }

            if (wanted.Count == 0) yield break;

            // 近い順に読む。**遠くを先に読むと、目の前が最後になる**
            wanted.Sort((a, b) => a.score.CompareTo(b.score));

            foreach ((TileCatalog.TileEntry entry, float score) in wanted)
            {
                // 枠が埋まっていたら、**近づいたタイルのために一番遠いタイルを退ける**。
                // 埋まった時点で打ち切ると、目の前のタイルが遠いタイルの後ろで待たされる
                if (LoadedCount >= maxLoaded)
                {
                    TileCatalog.TileEntry farthest = FarthestLoaded(position, out float farthestDistance);

                    // **入れ替えるだけの差が要る。** 僅差で入れ替えると、外したタイルが
                    // すぐまた「読みたい」側に回って出し入れを繰り返す
                    // （余裕を入れずに測ったら出し入れが22回から64回に増え、
                    //  最悪フレームが21.4fpsから7.3fpsまで落ちた → m2-plan.md §6）
                    if (farthest == null || farthestDistance <= score + evictMargin) break;

                    yield return Unload(farthest);
                }

                yield return Load(entry);
                if (!waitForFirst) break; // 1回の判定で読むのは1枚。まとめて読むと引っかかる
            }
        }

        /// <summary>置かれているタイルのうち、今いる場所から一番遠いもの。</summary>
        private TileCatalog.TileEntry FarthestLoaded(Vector3 position, out float distance)
        {
            TileCatalog.TileEntry farthest = null;
            distance = 0f;

            foreach (TileCatalog.TileEntry entry in catalog.Tiles)
            {
                if (!states.TryGetValue(entry.sceneName, out TileState state)) continue;
                if (state != TileState.Loaded) continue;

                float candidate = entry.HorizontalDistanceTo(position);
                if (farthest != null && candidate <= distance) continue;

                farthest = entry;
                distance = candidate;
            }

            return farthest;
        }

        /// <summary>
        /// 遠景タイルの出し入れ（→ docs/m2-plan.md §4.6）。
        ///
        /// **近景が置かれているタイルには遠景を置かない。** 同じ建物が二重に描かれ、
        /// 面が重なってちらつく。順序は**近景を置いてから遠景を外す**——逆にすると
        /// 一瞬地面に穴が空く。1〜2フレーム重なるが、穴よりは目立たない。
        /// </summary>
        private IEnumerator UpdateFarTiles()
        {
            if (viewer == null || catalog == null) yield break;

            Vector3 position = viewer.position;

            foreach (TileCatalog.TileEntry entry in catalog.Tiles)
            {
                if (!entry.HasFar) continue;

                bool nearLoaded = states.TryGetValue(entry.sceneName, out TileState nearState) &&
                                  nearState == TileState.Loaded;
                bool farLoaded = farStates.Contains(entry.farSceneName);
                float distance = entry.HorizontalDistanceTo(position);

                bool wanted = !nearLoaded && distance <= farDistance && farStates.Count < maxFarLoaded;

                if (wanted && !farLoaded)
                {
                    farStates.Add(entry.farSceneName);
                    AsyncOperation load = SceneManager.LoadSceneAsync(entry.farSceneName, LoadSceneMode.Additive);
                    while (load != null && !load.isDone) yield return null;

                    Debug.Log($"[TileStreamer] 遠景を読込 {entry.farSceneName}（{farStates.Count}枚）");
                }
                else if (!wanted && farLoaded)
                {
                    farStates.Remove(entry.farSceneName);
                    AsyncOperation unload = SceneManager.UnloadSceneAsync(entry.farSceneName);
                    while (unload != null && !unload.isDone) yield return null;
                    releasePending = true;

                    Debug.Log($"[TileStreamer] 遠景を破棄 {entry.farSceneName}（{farStates.Count}枚）");
                }
            }
        }

        /// <summary>
        /// 溜まった破棄ぶんのメモリをまとめて返す。
        /// **1回あたり十数msかかる**ので、タイルの出し入れとは別の頻度で走らせる。
        /// </summary>
        private IEnumerator ReleaseIfDue()
        {
            if (!releasePending || !ReleaseAssetsOnUnload) yield break;
            if (Time.realtimeSinceStartup - lastReleaseTime < releaseInterval) yield break;

            releasePending = false;
            lastReleaseTime = Time.realtimeSinceStartup;
            yield return Resources.UnloadUnusedAssets();
        }

        private GUIStyle style;

        private void OnGUI()
        {
            if (!showStatus) return;

            style ??= new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };

            string names = string.Join(" ", states.Keys);
            GUI.Label(new Rect(24f, Screen.height - 52f, 1200f, 24f),
                $"近景 {LoadedCount}/{TileCount}  遠景 {farStates.Count}  {names}", style);
        }

        private IEnumerator Load(TileCatalog.TileEntry entry)
        {
            if (states.ContainsKey(entry.sceneName)) yield break;

            busy = true;
            states[entry.sceneName] = TileState.Loading;

            AsyncOperation operation = SceneManager.LoadSceneAsync(entry.sceneName, LoadSceneMode.Additive);
            while (operation != null && !operation.isDone) yield return null;

            states[entry.sceneName] = TileState.Loaded;
            LoadedCount++;
            busy = false;

            // **試遊中に画面を見ずに確かめられるようにする。** 街が出ない時、
            // 読み込みが走っていないのか位置がずれているのかがログで分かる
            Debug.Log($"[TileStreamer] 読込 {entry.sceneName}（{LoadedCount}/{TileCount}）");
        }

        private IEnumerator Unload(TileCatalog.TileEntry entry)
        {
            if (!states.TryGetValue(entry.sceneName, out TileState state) || state != TileState.Loaded)
            {
                yield break;
            }

            busy = true;
            states[entry.sceneName] = TileState.Unloading;

            AsyncOperation operation = SceneManager.UnloadSceneAsync(entry.sceneName);
            while (operation != null && !operation.isDone) yield return null;

            states.Remove(entry.sceneName);
            LoadedCount--;
            Debug.Log($"[TileStreamer] 破棄 {entry.sceneName}（{LoadedCount}/{TileCount}）");

            // **シーンを外しただけではメッシュは解放されない。** ただし破棄のたびに返すと
            // そこで引っかかる（1% lowが99.6→85.6に落ちた → m2-plan.md §5.1）。
            // 返すこと自体は要るので、間隔を空けてまとめて返す
            releasePending = true;

            busy = false;
        }
    }
}
