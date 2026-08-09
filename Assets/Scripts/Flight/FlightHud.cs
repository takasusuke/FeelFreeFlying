using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// M1の試遊用の表示。速度・高度・方角と操作ヒントだけ。
    ///
    /// **計器を作り込まない。** 見るべきは街であって数字ではない（`requirements.md` §5）。
    /// ここにあるのは試遊で判断するための最小限で、製品の画面ではない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlightHud : MonoBehaviour
    {
        /// <summary>方位帯に一度に映す角度。狭いほど方角の変化が大きく見える。</summary>
        private const float CompassSpanDegrees = 140f;

        private const float CompassWidth = 560f;

        [SerializeField] private FlightController target;
        [SerializeField] private FlightInput input;
        [SerializeField] private bool showHints = true;
        [SerializeField] private bool showCompass = true;

        private GUIStyle valueStyle;
        private GUIStyle unitStyle;
        private GUIStyle hintStyle;
        private GUIStyle compassStyle;
        private GUIStyle compassMinorStyle;

        private void Awake()
        {
            if (target == null) target = FindFirstObjectByType<FlightController>();
            if (input == null) input = FindFirstObjectByType<FlightInput>();
        }

        private void OnGUI()
        {
            if (target == null) return;

            EnsureStyles();
            DrawSpeedAndAltitude();
            if (showCompass) DrawCompass();
            DrawNotice();
            if (showHints) DrawHints();
        }

        private void EnsureStyles()
        {
            // 毎フレーム生成するとGCに乗るのでキャッシュする
            valueStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 40,
                normal = { textColor = Color.white },
            };
            unitStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                normal = { textColor = new Color(1f, 1f, 1f, 0.85f) },
            };
            hintStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
            };
            compassStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            compassMinorStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.55f) },
            };
        }

        private void DrawSpeedAndAltitude()
        {
            var area = new Rect(28f, Screen.height - 150f, 420f, 120f);
            GUILayout.BeginArea(area);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"{target.Speed * 3.6f:F0}", valueStyle, GUILayout.Width(120f));
            GUILayout.BeginVertical();
            GUILayout.Space(20f);
            GUILayout.Label("km/h", unitStyle);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Label(
                target.IsBoosting
                    ? $"高度 {target.transform.position.y:F0} m    BOOST"
                    : $"高度 {target.transform.position.y:F0} m",
                unitStyle);

            GUILayout.EndArea();
        }

        /// <summary>
        /// 画面上部の方位帯。
        ///
        /// PLATEAUの座標系はEUN（東・上・北）で取り込んでいるので、**Unityの+Zが北**になる。
        /// 数字だけでなく帯にするのは、旋回中に「どちらへ回っているか」が見えるようにするため。
        /// </summary>
        private void DrawCompass()
        {
            float heading = target.transform.eulerAngles.y;

            var area = new Rect((Screen.width - CompassWidth) * 0.5f, 18f, CompassWidth, 46f);
            GUI.Box(area, GUIContent.none);

            for (int degrees = 0; degrees < 360; degrees += 15)
            {
                float delta = Mathf.DeltaAngle(heading, degrees);
                if (Mathf.Abs(delta) > CompassSpanDegrees * 0.5f) continue;

                float x = area.center.x + delta / CompassSpanDegrees * area.width;
                bool major = degrees % 45 == 0;

                GUI.Label(
                    new Rect(x - 30f, area.y + (major ? 10f : 14f), 60f, 24f),
                    major ? DirectionLabel(degrees) : "・",
                    major ? compassStyle : compassMinorStyle);
            }

            GUI.Label(new Rect(area.center.x - 40f, area.yMax - 2f, 80f, 24f),
                $"{heading:F0}°", compassStyle);
        }

        private static string DirectionLabel(int degrees)
        {
            switch (degrees)
            {
                case 0: return "北";
                case 45: return "北東";
                case 90: return "東";
                case 135: return "南東";
                case 180: return "南";
                case 225: return "南西";
                case 270: return "西";
                case 315: return "北西";
                default: return string.Empty;
            }
        }

        /// <summary>着地の成否など、その場で伝えたい1行。数秒で消す。</summary>
        private void DrawNotice()
        {
            if (string.IsNullOrEmpty(target.Notice)) return;
            if (Time.time - target.NoticeTime > 3f) return;

            var area = new Rect(0f, Screen.height * 0.42f, Screen.width, 40f);
            GUI.Label(area, target.Notice, compassStyle);
        }

        private void DrawHints()
        {
            bool pad = input != null && input.UsingGamepad;

            string hints;
            if (target.IsWalking)
            {
                hints = pad
                    ? "左スティック: 歩く / 右スティック: 見回す / A: 走る / B: ジャンプ / X: 飛び立つ / Y: 視点 / Esc: カーソルを返す"
                    : "W A S D: 歩く / マウス: 見回す / Shift: 走る / Space: ジャンプ / F: 飛び立つ / C: 視点 / Esc: カーソルを返す";
            }
            else
            {
                hints = pad
                    ? "左スティック: 傾ける / トリガー: 加減速 / A: ブースト / B: 水平 / X: 着地 / Y: 視点 / 十字上: 上下反転 / SELECT: やり直す"
                    : "マウス: 傾ける / W・S: 加減速 / A・D: ロール / Shift: ブースト / Space: 水平 / F: 着地 / C: 視点 / I: 上下反転 / R: やり直す / Esc: カーソルを返す";
            }

            if (input != null && !input.CursorCaptured)
            {
                hints = "カーソルを返しています。画面をクリックすると操縦に戻ります（Alt+F4 で終了）";
            }

            GUILayout.BeginArea(new Rect(24f, Screen.height - 26f, Screen.width - 48f, 24f));
            GUILayout.Label(hints, hintStyle);
            GUILayout.EndArea();
        }
    }
}
