using UnityEngine;
using UnityEngine.InputSystem;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 起動画面。STARTを押すまで操作を始めない。
    ///
    /// 街を背景に出したままにするので、シーンは分けない（都市データの読み込みをもう一度
    /// 待たせないため）。操作系を止めているだけで、街はそこにある。
    ///
    /// **出典表示をここに置く**（`CLAUDE.md` 不変条件5）。PLATEAUの3D都市モデルと
    /// 地理院タイルはどちらも表示義務がある。ゲーム内の常設表示はM5で作るが、
    /// **表示が無い状態のビルドを配らない**ために、まず起動画面に出す。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TitleScreen : MonoBehaviour
    {
        [SerializeField] private FlightController controller;
        [SerializeField] private FlightInput input;
        [SerializeField] private FlightHud hud;

        [SerializeField] private string title = "Feel Free Flying";
        [SerializeField] private string subtitle = "新宿の空を飛ぶ";

        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle promptStyle;
        private GUIStyle creditStyle;

        private bool started;

        private void Awake()
        {
            if (controller == null) controller = FindFirstObjectByType<FlightController>();
            if (input == null) input = FindFirstObjectByType<FlightInput>();
            if (hud == null) hud = FindFirstObjectByType<FlightHud>();

            SetPlaying(false);
        }

        private void Update()
        {
            if (started || !StartPressed()) return;

            started = true;
            SetPlaying(true);
        }

        private static bool StartPressed()
        {
            Gamepad pad = Gamepad.current;
            if (pad != null && (pad.startButton.wasPressedThisFrame ||
                                pad.buttonSouth.wasPressedThisFrame)) return true;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame ||
                                     keyboard.spaceKey.wasPressedThisFrame)) return true;

            Mouse mouse = Mouse.current;
            return mouse != null && mouse.leftButton.wasPressedThisFrame;
        }

        /// <summary>
        /// 操作系の有効・無効。**カーソルの確保は<see cref="FlightInput"/>のOnEnableに任せる**——
        /// 起動画面ではカーソルを返しておきたいので、こちらで掴まない。
        /// </summary>
        private void SetPlaying(bool playing)
        {
            if (controller != null) controller.enabled = playing;
            if (input != null) input.enabled = playing;
            if (hud != null) hud.enabled = playing;
        }

        private void OnGUI()
        {
            if (started) return;

            EnsureStyles();

            float centerY = Screen.height * 0.32f;
            GUI.Label(new Rect(0f, centerY, Screen.width, 90f), title, titleStyle);
            GUI.Label(new Rect(0f, centerY + 96f, Screen.width, 40f), subtitle, subtitleStyle);

            bool pad = Gamepad.current != null;
            string prompt = pad
                ? "OPTIONS または × ボタンで開始"
                : "Enter / Space / クリックで開始";
            GUI.Label(new Rect(0f, Screen.height * 0.62f, Screen.width, 40f), prompt, promptStyle);

            // 出典表示。**外さない**（CLAUDE.md 不変条件5）
            var credits = new Rect(0f, Screen.height - 78f, Screen.width, 70f);
            GUI.Label(credits,
                "3D都市モデル: Project PLATEAU（国土交通省） / CC BY 4.0\n" +
                "地図・航空写真: 地理院タイル（国土地理院）", creditStyle);
        }

        private void EnsureStyles()
        {
            titleStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 64,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            subtitleStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.85f) },
            };
            promptStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.7f, 0.92f, 1f, 0.95f) },
            };
            creditStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.7f) },
            };
        }
    }
}
