using UnityEngine;
using UnityEngine.InputSystem;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 1フレーム分の入力。**用途ではなくデバイスの生の値**を持つ。
    ///
    /// 飛行と歩行で同じスティックの意味が変わる（左スティックは飛行なら姿勢、歩行なら移動）ため、
    /// ここで「ピッチ」「移動」と決めてしまうと両立できない。割り当ては
    /// <see cref="FlightController"/> がモードごとに行う。
    /// </summary>
    public struct FlightInputState
    {
        /// <summary>マウスの仮想スティック（-1〜1）。飛行なら姿勢、歩行なら視線。</summary>
        public Vector2 Aim;

        /// <summary>ゲームパッド左スティック。飛行なら姿勢、歩行なら移動。</summary>
        public Vector2 LeftStick;

        /// <summary>ゲームパッド右スティック。歩行時の視線。</summary>
        public Vector2 RightStick;

        /// <summary>WASD。x=A/D、y=W/S。飛行ならロールとスロットル、歩行なら移動。</summary>
        public Vector2 Keys;

        /// <summary>矢印キー。y=↑↓（機首を明示的に上下させる）。</summary>
        public Vector2 Arrows;

        /// <summary>R2。飛行の加速（アナログ）。</summary>
        public float Trigger;

        /// <summary>L2。飛行の減速（アナログ）。歩行ではジャンプ。</summary>
        public float TriggerLeft;

        /// <summary>飛行中のブースト（L1）。</summary>
        public bool Boost;

        /// <summary>歩行中のダッシュ（R1）。</summary>
        public bool Dash;

        /// <summary>飛行時に水平へ戻す。</summary>
        public bool LevelFlight;

        /// <summary>歩行時のジャンプ。</summary>
        public bool Jump;

        /// <summary>着地する / 飛び立つ。</summary>
        public bool ToggleMotion;

        public bool Reset;
        public bool ToggleView;

        /// <summary>上下の反転を切り替える。</summary>
        public bool ToggleInvert;

        /// <summary>飛行中の当たり判定を切り替える。</summary>
        public bool ToggleCollision;

        /// <summary>視点を進行方向へ戻す（R3）。</summary>
        public bool RecenterView;

        /// <summary>落下中に慣性を消して真下へ落ちる（○ / 左Ctrl）。</summary>
        public bool DropStraight;
    }

    /// <summary>
    /// 入力の読み取り。**Input Systemのデバイスを直接見る**。
    ///
    /// .inputactions アセットを使わないのは、M1で試したいのが「操作方式そのもの」だからで、
    /// キー割り当てを変えるたびにアセットを開いて編集するより、ここを直接書き換えるほうが速い。
    /// 操作方式が固まったら（→ `requirements.md` §11）アクションアセットへ移す。
    ///
    /// マウスは**画面中央からの仮想スティック**として扱う。生のデルタを角速度に流すと、
    /// 手を止めた瞬間に機体も止まって「浮いている」感じが消えるため。
    /// </summary>
    public sealed class FlightInput : MonoBehaviour
    {
        [Header("マウス")]
        [Tooltip("仮想スティックが端まで振り切れるまでのマウス移動量（px）")]
        [SerializeField, Min(1f)] private float mouseRange = 400f;

        [Tooltip("入力を止めたときに中央へ戻る速さ（1秒あたりの割合）。0で戻らない")]
        [SerializeField, Range(0f, 5f)] private float mouseSelfCentering = 0.6f;

        [Header("ゲームパッド")]
        [Tooltip("スティックの遊び")]
        [SerializeField, Range(0f, 0.5f)] private float stickDeadZone = 0.15f;

        [Header("カーソル")]
        [Tooltip("マウスで操作するので既定で隠す。**Escで必ず解放できるようにしておくこと**")]
        [SerializeField] private bool lockCursor = true;

        private Vector2 virtualStick;
        private bool cursorCaptured;

        /// <summary>カーソルを掴んでいるか。falseの間はマウスで操縦しない。</summary>
        public bool CursorCaptured => cursorCaptured;

        /// <summary>直近に操作されたのがゲームパッドか。操作ヒントの出し分けに使う。</summary>
        public bool UsingGamepad { get; private set; }

        /// <summary>直近に使ったパッドの名前。認識されているかを画面で確かめるために出す。</summary>
        public string PadName { get; private set; }

        /// <summary>PlayStation系のパッドか。操作ヒントの記号を出し分ける。</summary>
        public bool IsPlayStationPad { get; private set; }

        private void OnEnable()
        {
            SetCursorCaptured(lockCursor);
            LogDevices();
        }

        /// <summary>
        /// 認識されている入力デバイスを一覧でログに出す。
        ///
        /// **パッドが動かない時、原因が「未接続」なのか「Input Systemが対応していない」なのかを
        /// 切り分けられるようにするため。** PS5のDualSenseはHIDとして繋がるが、
        /// Input Systemのバージョンによっては<see cref="Gamepad"/>として認識されないことがある。
        /// その場合ここに名前は出るが<see cref="Gamepad.current"/>はnullになる。
        /// </summary>
        private void LogDevices()
        {
            foreach (InputDevice device in InputSystem.devices)
            {
                Debug.Log($"[FlightInput] 認識: {device.displayName} / {device.GetType().Name} " +
                          $"(gamepadとして使える: {device is Gamepad})");
            }

            if (Gamepad.current == null) Debug.Log("[FlightInput] ゲームパッドは見つかりません。");
        }

        private void OnDisable() => SetCursorCaptured(false);

        public FlightInputState Read()
        {
            var state = new FlightInputState();

            UpdateCursorCapture();
            ReadGamepad(ref state);
            ReadKeyboardAndMouse(ref state);

            return state;
        }

        private void ReadGamepad(ref FlightInputState state)
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return;

            PadName = pad.displayName;

            // DualShock/DualSenseはInput Systemが専用クラスで拾う。拾えない機種のために名前でも見る
            IsPlayStationPad = pad is UnityEngine.InputSystem.DualShock.DualShockGamepad ||
                               pad.displayName.IndexOf("DualSense", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                               pad.displayName.IndexOf("Wireless Controller", System.StringComparison.OrdinalIgnoreCase) >= 0;

            state.LeftStick = ApplyDeadZone(pad.leftStick.ReadValue());
            state.RightStick = ApplyDeadZone(pad.rightStick.ReadValue());

            // R2で加速、L2で減速。L2は歩行中だけジャンプを兼ねる
            state.Trigger = pad.rightTrigger.ReadValue();
            state.TriggerLeft = pad.leftTrigger.ReadValue();

            if (state.LeftStick.sqrMagnitude > 0f || state.RightStick.sqrMagnitude > 0f ||
                Mathf.Abs(state.Trigger) > 0.01f)
            {
                UsingGamepad = true;
            }

            // 加減速はR2/L2（トリガー）。**顔ボタンの加減速はやめた**——結局使われず、
            // ジャンプが押しにくい位置へ追いやられていた
            if (pad.leftShoulder.isPressed) { state.Boost = true; UsingGamepad = true; }   // L1: 飛行のブースト
            if (pad.rightShoulder.isPressed) { state.Dash = true; UsingGamepad = true; }   // R1: 歩行のダッシュ

            bool jumpPressed = FlightSettings.Jump == JumpBinding.Cross
                ? pad.buttonSouth.isPressed
                : pad.leftTrigger.ReadValue() > 0.4f;
            if (jumpPressed) { state.Jump = true; UsingGamepad = true; }
            if (pad.buttonEast.wasPressedThisFrame) { state.DropStraight = true; UsingGamepad = true; }
            if (pad.leftStickButton.isPressed) { state.LevelFlight = true; UsingGamepad = true; }
            if (pad.rightStickButton.wasPressedThisFrame) { state.RecenterView = true; UsingGamepad = true; }
            if (pad.buttonWest.wasPressedThisFrame) { state.ToggleMotion = true; UsingGamepad = true; }
            if (pad.buttonNorth.wasPressedThisFrame) { state.ToggleView = true; UsingGamepad = true; }
            if (pad.selectButton.wasPressedThisFrame) { state.Reset = true; UsingGamepad = true; }
            if (pad.dpad.up.wasPressedThisFrame) { state.ToggleInvert = true; UsingGamepad = true; }
            if (pad.dpad.down.wasPressedThisFrame) { state.ToggleCollision = true; UsingGamepad = true; }
        }

        private void ReadKeyboardAndMouse(ref FlightInputState state)
        {
            // カーソルを返している間はマウスで操縦しない（他のアプリを触っている最中に動かないように）
            Mouse mouse = lockCursor && !cursorCaptured ? null : Mouse.current;
            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                if (delta.sqrMagnitude > 0f) UsingGamepad = false;

                virtualStick += delta / mouseRange;

                if (mouseSelfCentering > 0f)
                {
                    virtualStick = Vector2.MoveTowards(
                        virtualStick, Vector2.zero, mouseSelfCentering * Time.deltaTime);
                }

                virtualStick = Vector2.ClampMagnitude(virtualStick, 1f);
                state.Aim = virtualStick;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            var keys = Vector2.zero;
            if (keyboard.wKey.isPressed) keys.y += 1f;
            if (keyboard.sKey.isPressed) keys.y -= 1f;
            if (keyboard.aKey.isPressed) keys.x -= 1f;
            if (keyboard.dKey.isPressed) keys.x += 1f;
            state.Keys = keys;

            var arrows = Vector2.zero;
            if (keyboard.upArrowKey.isPressed) arrows.y += 1f;
            if (keyboard.downArrowKey.isPressed) arrows.y -= 1f;
            if (keyboard.leftArrowKey.isPressed) arrows.x -= 1f;
            if (keyboard.rightArrowKey.isPressed) arrows.x += 1f;
            state.Arrows = arrows;

            if (keys.sqrMagnitude > 0f || arrows.sqrMagnitude > 0f) UsingGamepad = false;

            if (keyboard.leftShiftKey.isPressed)
            {
                state.Boost = true;
                state.Dash = true;
                UsingGamepad = false;
            }
            if (keyboard.spaceKey.isPressed)
            {
                state.LevelFlight = true;
                state.Jump = true;
                UsingGamepad = false;
            }
            if (keyboard.fKey.wasPressedThisFrame) { state.ToggleMotion = true; UsingGamepad = false; }
            if (keyboard.cKey.wasPressedThisFrame) { state.ToggleView = true; UsingGamepad = false; }
            if (keyboard.iKey.wasPressedThisFrame) { state.ToggleInvert = true; UsingGamepad = false; }
            if (keyboard.kKey.wasPressedThisFrame) { state.ToggleCollision = true; UsingGamepad = false; }
            if (keyboard.leftCtrlKey.wasPressedThisFrame) { state.DropStraight = true; UsingGamepad = false; }

            if (keyboard.rKey.wasPressedThisFrame)
            {
                state.Reset = true;
                virtualStick = Vector2.zero;
                UsingGamepad = false;
            }
        }

        /// <summary>マウスの蓄積を消す。姿勢リセットや着地で視線を戻す時に使う。</summary>
        public void ClearAim() => virtualStick = Vector2.zero;

        /// <summary>
        /// Escでカーソルを返し、画面をクリックで掴み直す。
        ///
        /// **解放手段の無いカーソルロックを作らない。** ウィンドウ表示のビルドで閉じ込めると、
        /// 他のアプリを操作できなくなり、ゲームを終了させることすらできない。
        /// </summary>
        private void UpdateCursorCapture()
        {
            if (!lockCursor) return;

            Keyboard keyboard = Keyboard.current;
            if (cursorCaptured && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                SetCursorCaptured(false);
                return;
            }

            Mouse mouse = Mouse.current;
            if (!cursorCaptured && mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                SetCursorCaptured(true);
            }
        }

        private void SetCursorCaptured(bool captured)
        {
            cursorCaptured = captured;
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }

        private Vector2 ApplyDeadZone(Vector2 stick)
        {
            float magnitude = stick.magnitude;
            if (magnitude < stickDeadZone) return Vector2.zero;

            // 遊びの外側を0〜1に引き伸ばす。切り捨てるだけだと中心付近で入力が飛ぶ
            return stick / magnitude * Mathf.InverseLerp(stickDeadZone, 1f, magnitude);
        }
    }
}
