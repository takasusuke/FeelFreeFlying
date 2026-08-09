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

        [Tooltip("この距離まで近づいたら読み込む (m)")]
        [SerializeField, Min(0f)] private float loadDistance = 1500f;

        [Tooltip("この距離まで離れたら破棄する (m)。読み込みより広くしないと境界で出入りを繰り返す")]
        [SerializeField, Min(0f)] private float unloadDistance = 2200f;

        [Tooltip("進行方向を何秒先まで見るか。速度×この秒数だけ前方の判定を甘くする")]
        [SerializeField, Min(0f)] private float lookAheadSeconds = 10f;

        [Tooltip("同時に置いてよい枚数。多すぎるとfpsが落ちる（→ m0-plan.md §5.1）")]
        [SerializeField, Min(1)] private int maxLoaded = 9;

        [Tooltip("判定の間隔 (秒)。毎フレーム全タイルを見る必要はない")]
        [SerializeField, Min(0f)] private float checkInterval = 0.25f;

        [Tooltip("メモリを実際に返す間隔 (秒)。**破棄のたびに返すと引っかかる**（→ m2-plan.md §5.1）")]
        [SerializeField, Min(0f)] private float releaseInterval = 20f;

        [Tooltip("読み込み状況を画面に出す。M2の確認用")]
        [SerializeField] private bool showStatus = true;

        private readonly Dictionary<string, TileState> states = new Dictionary<string, TileState>();
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

            while (true)
            {
                yield return new WaitForSeconds(checkInterval);
                yield return UpdateTiles(waitForFirst: false);
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

            foreach ((TileCatalog.TileEntry entry, float _) in wanted)
            {
                if (LoadedCount >= maxLoaded) break;

                yield return Load(entry);
                if (!waitForFirst) break; // 1回の判定で読むのは1枚。まとめて読むと引っかかる
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
                $"タイル {LoadedCount}/{TileCount}  {names}", style);
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
