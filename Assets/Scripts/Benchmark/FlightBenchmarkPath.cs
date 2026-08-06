using System.Collections.Generic;
using UnityEngine;

namespace FeelFreeFlying.Benchmark
{
    /// <summary>
    /// M0の計測で使うカメラ軌道。ウェイポイントをCatmull-Romで繋ぎ、
    /// 弧長テーブルを作って「距離 → 位置」で引けるようにする。
    ///
    /// 距離で引くのは、フレームレートが変わっても同じ経路を同じ速度で通すため。
    /// 時間やフレーム数で補間すると、重い設定ほど経路が変わってしまい比較にならない。
    ///
    /// 前提: このGameObjectのスケールは変更しない（弧長がローカル空間基準のため）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlightBenchmarkPath : MonoBehaviour
    {
        [Header("軌道")]
        [Tooltip("ローカル座標。Sceneビューのギズモで位置を確認できる")]
        [SerializeField] private List<Vector3> waypoints = new List<Vector3>();

        [SerializeField] private bool loop = true;

        [Tooltip("1区間あたりの分割数。大きいほど弧長の精度が上がる")]
        [SerializeField, Range(2, 128)] private int samplesPerSegment = 24;

        [Header("円軌道の自動生成（コンテキストメニューから実行）")]
        [SerializeField] private float generateRadius = 800f;
        [SerializeField] private float generateHeight = 300f;
        [SerializeField, Range(3, 64)] private int generatePointCount = 12;

        private Vector3[] samples;
        private float[] cumulativeLengths;

        /// <summary>軌道の全長（メートル）。未構築なら0。</summary>
        public float TotalLength =>
            cumulativeLengths == null || cumulativeLengths.Length == 0
                ? 0f
                : cumulativeLengths[cumulativeLengths.Length - 1];

        public bool IsLooping => loop;

        public bool IsReady => samples != null && samples.Length >= 2 && TotalLength > 0f;

        private void Awake() => Rebuild();

        /// <summary>弧長テーブルを作り直す。ウェイポイントを変えたら呼ぶ。</summary>
        public void Rebuild()
        {
            int pointCount = waypoints.Count;
            if (pointCount < 2)
            {
                samples = null;
                cumulativeLengths = null;
                return;
            }

            int segmentCount = loop ? pointCount : pointCount - 1;
            int sampleCount = segmentCount * samplesPerSegment + 1;

            samples = new Vector3[sampleCount];
            cumulativeLengths = new float[sampleCount];

            int index = 0;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                for (int step = 0; step < samplesPerSegment; step++)
                {
                    samples[index++] = Interpolate(segment, (float)step / samplesPerSegment);
                }
            }
            samples[index] = loop ? Interpolate(0, 0f) : Interpolate(segmentCount - 1, 1f);

            cumulativeLengths[0] = 0f;
            for (int i = 1; i < sampleCount; i++)
            {
                cumulativeLengths[i] =
                    cumulativeLengths[i - 1] + Vector3.Distance(samples[i - 1], samples[i]);
            }
        }

        /// <summary>始点からの距離に対応するワールド座標を返す。</summary>
        public Vector3 GetPositionAtDistance(float distance)
        {
            if (!IsReady) return transform.position;

            float total = TotalLength;
            distance = loop ? Mathf.Repeat(distance, total) : Mathf.Clamp(distance, 0f, total);

            int high = FindUpperBound(distance);
            int low = high - 1;

            float segmentLength = cumulativeLengths[high] - cumulativeLengths[low];
            float t = segmentLength > Mathf.Epsilon
                ? (distance - cumulativeLengths[low]) / segmentLength
                : 0f;

            return transform.TransformPoint(Vector3.Lerp(samples[low], samples[high], t));
        }

        /// <summary>進行方向（正規化済み）。軌道が構築されていなければ前方を返す。</summary>
        public Vector3 GetDirectionAtDistance(float distance)
        {
            if (!IsReady) return transform.forward;

            // 前後に少しずらした2点から求める。端では内側に寄せる。
            const float delta = 1f;
            float total = TotalLength;
            float back = loop ? distance - delta : Mathf.Max(0f, distance - delta);
            float forward = loop ? distance + delta : Mathf.Min(total, distance + delta);

            Vector3 direction = GetPositionAtDistance(forward) - GetPositionAtDistance(back);
            return direction.sqrMagnitude > Mathf.Epsilon
                ? direction.normalized
                : transform.forward;
        }

        private int FindUpperBound(float distance)
        {
            int low = 1;
            int high = cumulativeLengths.Length - 1;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (cumulativeLengths[mid] < distance) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private Vector3 Interpolate(int segment, float t)
        {
            Vector3 p0 = ControlPoint(segment - 1);
            Vector3 p1 = ControlPoint(segment);
            Vector3 p2 = ControlPoint(segment + 1);
            Vector3 p3 = ControlPoint(segment + 2);

            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                2f * p1
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private Vector3 ControlPoint(int index)
        {
            int count = waypoints.Count;
            if (loop) return waypoints[((index % count) + count) % count];
            return waypoints[Mathf.Clamp(index, 0, count - 1)];
        }

        /// <summary>
        /// 街の上空を一周する円軌道を生成する。ウェイポイントを手で置く前の叩き台。
        /// 中心はこのGameObjectの位置。
        /// </summary>
        [ContextMenu("円軌道を生成する")]
        public void GenerateCircularPath()
        {
            waypoints.Clear();
            for (int i = 0; i < generatePointCount; i++)
            {
                float angle = i / (float)generatePointCount * Mathf.PI * 2f;
                waypoints.Add(new Vector3(
                    Mathf.Cos(angle) * generateRadius,
                    generateHeight,
                    Mathf.Sin(angle) * generateRadius));
            }
            loop = true;
            Rebuild();
        }

        private void OnDrawGizmos()
        {
            if (waypoints.Count < 2) return;

            // 実行中でなくても見えるように、その場で構築する
            if (!Application.isPlaying || !IsReady) Rebuild();
            if (!IsReady) return;

            Gizmos.color = Color.cyan;
            for (int i = 1; i < samples.Length; i++)
            {
                Gizmos.DrawLine(
                    transform.TransformPoint(samples[i - 1]),
                    transform.TransformPoint(samples[i]));
            }

            Gizmos.color = Color.yellow;
            foreach (Vector3 point in waypoints)
            {
                Gizmos.DrawWireSphere(transform.TransformPoint(point), 20f);
            }
        }
    }
}
