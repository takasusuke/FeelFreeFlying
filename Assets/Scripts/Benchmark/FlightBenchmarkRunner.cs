using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace FeelFreeFlying.Benchmark
{
    /// <summary>
    /// M0の計測用。<see cref="FlightBenchmarkPath"/> 上を一定速度でカメラを動かし、
    /// 1周分のフレーム時間を記録して結果をファイルに書き出す。
    ///
    /// 計測は「1周走りきるまで」で固定する。秒数で切ると重い設定ほど短い距離しか
    /// 通らず、比較にならないため。
    ///
    /// 使い方:
    ///   1. 空のGameObjectに <see cref="FlightBenchmarkPath"/> を付け、
    ///      コンテキストメニュー「円軌道を生成する」で軌道を作る（手で置いてもよい）
    ///   2. Main Camera にこのコンポーネントを付け、Path を割り当てる
    ///   3. Label に測定条件（例: "地域単位_URP"）を入れて再生
    ///   4. 終了後、Consoleに出力先パスが出る
    ///
    /// 注意: 入力（キー操作）は一切扱わない。旧Input Managerに依存すると
    /// Input Systemパッケージ構成のプロジェクトで実行時例外になるため。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlightBenchmarkRunner : MonoBehaviour
    {
        private enum Phase { Warmup, Measuring, Finished }

        [Header("参照")]
        [SerializeField] private FlightBenchmarkPath path;

        [Tooltip("動かす対象。未指定ならこのGameObject自身")]
        [SerializeField] private Transform target;

        [Header("飛行")]
        [Tooltip("移動速度 (m/s)。60 m/s ≒ 216 km/h")]
        [SerializeField, Min(1f)] private float speed = 60f;

        [Tooltip("俯角。正の値でカメラが下を向く")]
        [SerializeField, Range(0f, 89f)] private float pitchDegrees = 20f;

        [Header("計測")]
        [Tooltip("記録を始めるまでの助走。シェーダのコンパイルや初回ロードのスパイクを除くため")]
        [SerializeField, Min(0f)] private float warmupSeconds = 5f;

        [Tooltip("計測中はvsyncとフレームレート上限を外す。60fps上限のままだと余裕が測れない")]
        [SerializeField] private bool uncapFrameRate = true;

        [Tooltip("測定条件の名前。出力ファイル名に入る（例: 主要地物単位_URP）")]
        [SerializeField] private string label = "default";

        [Header("出力")]
        [SerializeField] private bool writeSummary = true;
        [SerializeField] private bool writeCsv = true;

        [Tooltip("進捗表示。IMGUIは僅かに負荷になるので、最終的な数値を取る時は切る")]
        [SerializeField] private bool showHud = true;

        [Tooltip("計測が終わったらアプリを終了する。ビルドで流しっぱなしにする時に使う")]
        [SerializeField] private bool quitOnFinish = false;

        private readonly List<float> frameTimes = new List<float>(60 * 1000);
        private Phase phase = Phase.Warmup;
        private float distance;
        private float travelled;
        private float warmupElapsed;
        private float measuredSeconds;
        private string lastOutputDirectory;
        private BenchmarkResult result;
        private GUIStyle hudStyle;

        private int previousVSyncCount;
        private int previousTargetFrameRate;

        private void Awake()
        {
            if (target == null) target = transform;
            if (path == null) path = FindFirstObjectByType<FlightBenchmarkPath>();

            previousVSyncCount = QualitySettings.vSyncCount;
            previousTargetFrameRate = Application.targetFrameRate;

            if (uncapFrameRate)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
        }

        private void OnDestroy()
        {
            if (!uncapFrameRate) return;
            QualitySettings.vSyncCount = previousVSyncCount;
            Application.targetFrameRate = previousTargetFrameRate;
        }

        private void Start()
        {
            if (path == null)
            {
                Debug.LogError($"{nameof(FlightBenchmarkRunner)}: Path が設定されていません。計測を中止します。");
                enabled = false;
                return;
            }

            path.Rebuild();
            if (!path.IsReady)
            {
                Debug.LogError($"{nameof(FlightBenchmarkRunner)}: 軌道が構築できていません。ウェイポイントを2点以上置いてください。");
                enabled = false;
                return;
            }

            Apply(0f);
        }

        private void Update()
        {
            if (phase == Phase.Finished) return;

            float delta = Time.unscaledDeltaTime;
            distance += speed * delta;
            Apply(distance);

            switch (phase)
            {
                case Phase.Warmup:
                    warmupElapsed += delta;
                    if (warmupElapsed < warmupSeconds) break;

                    // 助走ぶんを捨て、始点から1周を測り直す
                    distance = 0f;
                    travelled = 0f;
                    measuredSeconds = 0f;
                    frameTimes.Clear();
                    phase = Phase.Measuring;
                    Apply(0f);
                    break;

                case Phase.Measuring:
                    frameTimes.Add(delta);
                    travelled += speed * delta;
                    measuredSeconds += delta;
                    if (travelled >= path.TotalLength) Finish();
                    break;
            }
        }

        private void Apply(float d)
        {
            target.position = path.GetPositionAtDistance(d);

            Vector3 direction = path.GetDirectionAtDistance(d);
            Quaternion look = Quaternion.LookRotation(direction, Vector3.up);
            target.rotation = look * Quaternion.Euler(pitchDegrees, 0f, 0f);
        }

        private void Finish()
        {
            phase = Phase.Finished;
            result = BenchmarkResult.From(frameTimes, measuredSeconds);

            Debug.Log($"[Benchmark:{label}] {result.ToSummary()}");

            if (writeSummary || writeCsv) WriteFiles();
            if (quitOnFinish) Quit();
        }

        private void WriteFiles()
        {
            string directory = Path.Combine(Application.persistentDataPath, "m0-benchmark");
            Directory.CreateDirectory(directory);
            lastOutputDirectory = directory;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeLabel = string.IsNullOrWhiteSpace(label) ? "default" : Sanitize(label);
            string baseName = $"{stamp}_{safeLabel}";

            if (writeSummary)
            {
                var summary = new StringBuilder();
                summary.AppendLine($"label            : {label}");
                summary.AppendLine($"date             : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                summary.AppendLine($"unity            : {Application.unityVersion}");
                summary.AppendLine($"platform         : {Application.platform}");
                summary.AppendLine($"graphics device  : {SystemInfo.graphicsDeviceName}");
                summary.AppendLine($"graphics api     : {SystemInfo.graphicsDeviceType}");
                summary.AppendLine($"cpu              : {SystemInfo.processorType}");
                summary.AppendLine($"system memory    : {SystemInfo.systemMemorySize} MB");
                summary.AppendLine($"screen           : {Screen.width}x{Screen.height} fullscreen={Screen.fullScreen}");
                summary.AppendLine($"quality level    : {QualitySettings.names[QualitySettings.GetQualityLevel()]}");
                summary.AppendLine($"editor           : {Application.isEditor}");
                summary.AppendLine($"path length      : {path.TotalLength:F1} m");
                summary.AppendLine($"speed            : {speed:F1} m/s");
                summary.AppendLine();
                summary.AppendLine(result.ToSummary());

                File.WriteAllText(Path.Combine(directory, baseName + ".txt"), summary.ToString(), Encoding.UTF8);
            }

            if (writeCsv)
            {
                var csv = new StringBuilder(frameTimes.Count * 12);
                csv.AppendLine("frame,frame_time_ms");
                for (int i = 0; i < frameTimes.Count; i++)
                {
                    csv.Append(i.ToString(CultureInfo.InvariantCulture));
                    csv.Append(',');
                    csv.AppendLine((frameTimes[i] * 1000f).ToString("F3", CultureInfo.InvariantCulture));
                }
                File.WriteAllText(Path.Combine(directory, baseName + ".csv"), csv.ToString(), Encoding.UTF8);
            }

            Debug.Log($"[Benchmark:{label}] 出力先: {directory}");
        }

        private static string Sanitize(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }
            return value;
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OnGUI()
        {
            if (!showHud) return;

            // 毎フレーム生成すると計測対象のフレーム時間に乗るのでキャッシュする
            GUIStyle style = hudStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                normal = { textColor = Color.white },
            };

            var area = new Rect(12f, 12f, 640f, 200f);
            GUI.Box(new Rect(8f, 8f, 560f, phase == Phase.Finished ? 190f : 110f), GUIContent.none);
            GUILayout.BeginArea(area);

            switch (phase)
            {
                case Phase.Warmup:
                    GUILayout.Label($"WARMUP  {warmupElapsed:F1} / {warmupSeconds:F1} s", style);
                    break;
                case Phase.Measuring:
                    float progress = path.TotalLength > 0f ? travelled / path.TotalLength : 0f;
                    GUILayout.Label($"MEASURING  {progress * 100f:F0}%  ({travelled:F0} / {path.TotalLength:F0} m)", style);
                    break;
                case Phase.Finished:
                    GUILayout.Label("FINISHED", style);
                    break;
            }

            GUILayout.Label($"label: {label}", style);
            GUILayout.Label($"fps: {1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f):F1}", style);

            if (phase == Phase.Finished)
            {
                GUILayout.Label($"avg {result.AverageFps:F1} / 1% low {result.OnePercentLowFps:F1} / min {result.MinFps:F1}", style);
                GUILayout.Label($"60fps以上: {result.RatioAbove60 * 100f:F1}%   30fps以上: {result.RatioAbove30 * 100f:F1}%", style);
                if (!string.IsNullOrEmpty(lastOutputDirectory)) GUILayout.Label(lastOutputDirectory, style);
            }

            GUILayout.EndArea();
        }
    }

    /// <summary>フレーム時間の配列から、M0の判断に使う数値を出す。</summary>
    public readonly struct BenchmarkResult
    {
        public readonly int FrameCount;
        public readonly float DurationSeconds;
        public readonly float AverageFps;
        public readonly float MinFps;
        public readonly float MaxFps;
        public readonly float OnePercentLowFps;
        public readonly float PointOnePercentLowFps;
        public readonly float RatioAbove60;
        public readonly float RatioAbove30;

        private BenchmarkResult(int frameCount, float duration, float average, float min, float max,
            float onePercentLow, float pointOnePercentLow, float above60, float above30)
        {
            FrameCount = frameCount;
            DurationSeconds = duration;
            AverageFps = average;
            MinFps = min;
            MaxFps = max;
            OnePercentLowFps = onePercentLow;
            PointOnePercentLowFps = pointOnePercentLow;
            RatioAbove60 = above60;
            RatioAbove30 = above30;
        }

        public static BenchmarkResult From(List<float> frameTimes, float durationSeconds)
        {
            int count = frameTimes.Count;
            if (count == 0) return default;

            var sorted = new float[count];
            frameTimes.CopyTo(sorted);
            Array.Sort(sorted); // 昇順（先頭が速いフレーム）

            int above60 = 0;
            int above30 = 0;
            const float frame60 = 1f / 60f;
            const float frame30 = 1f / 30f;
            foreach (float t in frameTimes)
            {
                if (t <= frame60) above60++;
                if (t <= frame30) above30++;
            }

            return new BenchmarkResult(
                count,
                durationSeconds,
                durationSeconds > 0f ? count / durationSeconds : 0f,
                ToFps(sorted[count - 1]),
                ToFps(sorted[0]),
                ToFps(AverageOfWorst(sorted, 0.01f)),
                ToFps(AverageOfWorst(sorted, 0.001f)),
                above60 / (float)count,
                above30 / (float)count);
        }

        /// <summary>遅い方から指定割合ぶんのフレーム時間を平均する（1% low等）。</summary>
        private static float AverageOfWorst(float[] ascending, float ratio)
        {
            int take = Mathf.Max(1, Mathf.FloorToInt(ascending.Length * ratio));
            double sum = 0d;
            for (int i = ascending.Length - take; i < ascending.Length; i++) sum += ascending[i];
            return (float)(sum / take);
        }

        private static float ToFps(float frameTime) => frameTime > 0f ? 1f / frameTime : 0f;

        public string ToSummary()
        {
            var text = new StringBuilder();
            text.AppendLine($"frames           : {FrameCount}");
            text.AppendLine($"duration         : {DurationSeconds:F2} s");
            text.AppendLine($"average fps      : {AverageFps:F1}");
            text.AppendLine($"1% low fps       : {OnePercentLowFps:F1}");
            text.AppendLine($"0.1% low fps     : {PointOnePercentLowFps:F1}");
            text.AppendLine($"min fps          : {MinFps:F1}");
            text.AppendLine($"max fps          : {MaxFps:F1}");
            text.AppendLine($"frames >= 60fps  : {RatioAbove60 * 100f:F1} %");
            text.AppendLine($"frames >= 30fps  : {RatioAbove30 * 100f:F1} %");
            return text.ToString();
        }
    }
}
