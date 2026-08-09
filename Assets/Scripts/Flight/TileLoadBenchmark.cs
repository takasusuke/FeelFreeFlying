using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// タイル1枚の読み込みにかかる時間を測る（docs/m2-plan.md §3）。
    ///
    /// **M2で最初に潰すべき未知数。** 飛行速度は最大150m/sで、3次メッシュ1枚は約1km。
    /// **7秒で1枚を横切る**ので、読み込みがそれより遅いと先読みが間に合わず、
    /// 目の前に街が生えてくることになる。粒度（1タイルの大きさ）の妥当性がここで決まる。
    ///
    /// 秒数だけでなく**読み込み中のフレーム時間**も見る。裏で読めても、その間に
    /// カクつくなら飛行中には使えない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileLoadBenchmark : MonoBehaviour
    {
        [Tooltip("読み込むタイルのシーン名。Build Settingsに入っている必要がある")]
        [SerializeField] private List<string> tileScenes = new List<string>();

        [Tooltip("1枚ごとに読み込み→破棄を繰り返す回数")]
        [SerializeField, Min(1)] private int repeats = 2;

        [Tooltip("計測を始めるまでの助走（秒）")]
        [SerializeField, Min(0f)] private float warmupSeconds = 2f;

        [SerializeField] private bool quitOnFinish = true;

        /// <summary>初回描画のコストを見るフレーム数。60fpsなら1秒。</summary>
        private const int FirstDrawFrames = 60;

        private readonly StringBuilder report = new StringBuilder();
        private GUIStyle style;
        private string status = "準備中";

        private IEnumerator Start()
        {
            // **垂直同期を切る。** 60fps上限のままだとフレーム時間が16.7msに張り付き、
            // 読み込みによる引っかかりが埋もれる（M0の計測と同じ理由）
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            yield return new WaitForSeconds(warmupSeconds);

            report.AppendLine($"date        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"unity       : {Application.unityVersion}");
            report.AppendLine($"gpu         : {SystemInfo.graphicsDeviceName}");
            report.AppendLine($"cpu         : {SystemInfo.processorType}");
            report.AppendLine();
            report.AppendLine("tile,pass,load_seconds,worst_load_frame_ms,first_draw_worst_ms,renderers,native_mb");

            for (int pass = 1; pass <= repeats; pass++)
            {
                foreach (string sceneName in tileScenes)
                {
                    yield return Measure(sceneName, pass);
                }
            }

            WriteReport();
            status = "完了";

            if (quitOnFinish)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
        }

        private IEnumerator Measure(string sceneName, int pass)
        {
            status = $"{sceneName} を読み込み中（{pass}回目）";

            float startTime = Time.realtimeSinceStartup;
            float worstFrame = 0f;

            AsyncOperation load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);

            while (!load.isDone)
            {
                // **読み込み中のカクつきを拾う。** 秒数だけでは飛行中に使えるか分からない
                worstFrame = Mathf.Max(worstFrame, Time.unscaledDeltaTime);
                yield return null;
            }

            float seconds = Time.realtimeSinceStartup - startTime;

            // **読み込んだだけでは終わらない。** メッシュのGPUへの転送は最初に描画された
            // フレームで起きるため、カメラを街の中へ運んで**実際に映してから**測る。
            // ここを省くと「0.1秒で読めた」という嘘の数字が出る
            Scene loaded = SceneManager.GetSceneByName(sceneName);
            Bounds bounds = MeasureBounds(loaded, out int renderers);
            yield return DrawFrom(bounds);

            float firstDrawWorst = 0f;
            for (int frame = 0; frame < FirstDrawFrames; frame++)
            {
                firstDrawWorst = Mathf.Max(firstDrawWorst, Time.unscaledDeltaTime);
                yield return null;
            }

            long native = Profiler.GetTotalAllocatedMemoryLong() / 1024 / 1024;

            report.AppendLine(string.Join(",",
                sceneName,
                pass.ToString(CultureInfo.InvariantCulture),
                seconds.ToString("F3", CultureInfo.InvariantCulture),
                (worstFrame * 1000f).ToString("F1", CultureInfo.InvariantCulture),
                (firstDrawWorst * 1000f).ToString("F1", CultureInfo.InvariantCulture),
                renderers.ToString(CultureInfo.InvariantCulture),
                native.ToString(CultureInfo.InvariantCulture)));

            Debug.Log($"[TileLoad] {sceneName} #{pass}: {seconds:F2} 秒 / 読み込み中 {worstFrame * 1000f:F0} ms / " +
                      $"初回描画 {firstDrawWorst * 1000f:F0} ms / {renderers} 個");

            yield return null;

            AsyncOperation unload = SceneManager.UnloadSceneAsync(sceneName);
            while (unload != null && !unload.isDone) yield return null;

            // 破棄したメモリを実際に返させてから次を測る
            yield return Resources.UnloadUnusedAssets();
            yield return null;
        }

        /// <summary>
        /// タイルの中身を数えて範囲を取る。**空のシーンを読んでも秒数は出てしまう**ので、
        /// 描画対象が本当に入っていることをレポートに残す。
        /// </summary>
        private static Bounds MeasureBounds(Scene scene, out int renderers)
        {
            renderers = 0;
            var bounds = new Bounds();
            bool first = true;

            if (!scene.IsValid()) return bounds;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
                {
                    renderers++;

                    if (first) { bounds = renderer.bounds; first = false; }
                    else bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }

        /// <summary>タイルの中心を見下ろす位置にカメラを運ぶ。飛行中に近い見え方にする。</summary>
        private IEnumerator DrawFrom(Bounds bounds)
        {
            Camera camera = Camera.main;
            if (camera == null) yield break;

            camera.farClipPlane = Mathf.Max(camera.farClipPlane, 4000f);

            Vector3 center = bounds.center;
            float distance = Mathf.Max(bounds.extents.x, bounds.extents.z) + 200f;

            camera.transform.position = center + new Vector3(0f, distance * 0.5f, -distance);
            camera.transform.LookAt(center);

            yield return null;
        }

        private void WriteReport()
        {
            string directory = Path.Combine(Application.persistentDataPath, "m2-tileload");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}_tileload.csv");
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);

            Debug.Log($"[TileLoad] 出力: {path}");
        }

        private void OnGUI()
        {
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white } };
            GUI.Label(new Rect(24f, 24f, 900f, 40f), status, style);
        }
    }
}
