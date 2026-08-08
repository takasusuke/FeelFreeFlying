using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// M1の試遊用の最小限の表示。速度・高度と操作ヒントだけ。
    ///
    /// **計器を作り込まない。** 見るべきは街であって数字ではない（`requirements.md` §5）。
    /// ここにあるのは「今どのくらいの速さで飛んでいるか」を言葉で確認するためのもので、
    /// 製品の画面ではない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlightHud : MonoBehaviour
    {
        [SerializeField] private FlightController target;
        [SerializeField] private FlightInput input;
        [SerializeField] private bool showHints = true;

        private GUIStyle style;
        private GUIStyle hintStyle;

        private void Awake()
        {
            if (target == null) target = FindFirstObjectByType<FlightController>();
            if (input == null) input = FindFirstObjectByType<FlightInput>();
        }

        private void OnGUI()
        {
            if (target == null) return;

            // 毎フレーム生成するとGCに乗るのでキャッシュする
            style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                normal = { textColor = Color.white },
            };
            hintStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
            };

            GUILayout.BeginArea(new Rect(24f, 20f, 460f, 220f));
            GUILayout.Label($"{target.Speed * 3.6f:F0} km/h", style);
            GUILayout.Label($"高度 {target.transform.position.y:F0} m", style);
            if (target.IsBoosting) GUILayout.Label("BOOST", style);
            GUILayout.EndArea();

            if (!showHints) return;

            bool pad = input != null && input.UsingGamepad;
            string hints = pad
                ? "左スティック: 傾ける / トリガー: 加減速 / A: ブースト / B: 水平 / Y: 視点 / SELECT: やり直す / Esc: カーソルを返す"
                : "マウス: 傾ける / W・S: 加減速 / A・D: ロール / ↑↓: 機首 / Shift: ブースト / Space: 水平 / C: 視点 / R: やり直す / Esc: カーソルを返す";

            if (input != null && !input.CursorCaptured)
            {
                hints = "カーソルを返しています。画面をクリックすると操縦に戻ります（Alt+F4 で終了）";
            }

            GUILayout.BeginArea(new Rect(24f, Screen.height - 48f, Screen.width - 48f, 40f));
            GUILayout.Label(hints, hintStyle);
            GUILayout.EndArea();
        }
    }
}
