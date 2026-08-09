using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 飛行と歩行（M1の本体 → `docs/m1-plan.md`）。
    ///
    /// **航空力学は再現しない**（`CLAUDE.md` 不変条件3）。失速・迎え角・エンジン出力を持たず、
    /// 姿勢角を直接積分する。落下も墜落も無い。速度の下限が0より上なので、**操作を止めても
    /// 滑空し続ける**——これが「浮遊感」の土台になる。
    ///
    /// 唯一物理っぽく振る舞うのが<see cref="diveAcceleration"/>で、機首を下げると速度が乗り、
    /// 上げると落ちる。これが無いと、どの姿勢でも同じ速度で飛ぶ「レール感」が出る。
    ///
    /// 飛行中は当たり判定を持たない（建物をすり抜ける）。**歩行中だけ**
    /// <see cref="CharacterController"/> を有効にして屋上に立つ。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlightController : MonoBehaviour
    {
        public enum MotionMode
        {
            Flying,
            Walking,
        }

        [Header("参照")]
        [SerializeField] private FlightInput input;

        [Header("速度 (m/s)")]
        [Tooltip("スロットルを絞りきった時の速度。**0にしない**。止まると浮遊感が消える")]
        [SerializeField, Min(0.5f)] private float speedMin = 6f;

        [SerializeField, Min(1f)] private float speedMax = 85f;

        [Tooltip("開始時および姿勢リセット時の速度")]
        [SerializeField, Min(1f)] private float speedStart = 22f;

        [Tooltip("スロットル操作に速度が追従する速さ (m/s^2)")]
        [SerializeField, Min(1f)] private float throttleAcceleration = 18f;

        [Tooltip("ブースト中の速度倍率")]
        [SerializeField, Range(1f, 3f)] private float boostMultiplier = 1.7f;

        [Header("姿勢")]
        [Tooltip("上下を反転する。**マウス・スティックのみ**に効く（矢印キーは常に↑で機首上げ）")]
        [SerializeField] private bool invertPitch = true;

        [Tooltip("機首上下の角速度 (度/秒)")]
        [SerializeField, Min(1f)] private float pitchRate = 55f;

        [SerializeField, Range(10f, 89f)] private float pitchLimit = 65f;

        [Tooltip("ロールの角速度 (度/秒)")]
        [SerializeField, Min(1f)] private float rollRate = 120f;

        [SerializeField, Range(10f, 89f)] private float rollLimit = 70f;

        [Tooltip("バンク角いっぱいで曲がる速さ (度/秒)")]
        [SerializeField, Min(1f)] private float turnRate = 60f;

        [Tooltip("入力が無いとき水平に戻る速さ (度/秒)。0で戻らない")]
        [SerializeField, Range(0f, 120f)] private float autoLevelRate = 25f;

        [Tooltip("姿勢の追従の鈍さ (秒)。大きいほどふわっとするが、遅れて感じる")]
        [SerializeField, Range(0f, 0.5f)] private float attitudeSmoothing = 0.1f;

        [Header("浮遊感")]
        [Tooltip("機首を下げた時に乗る加速度 (m/s^2)。上げた時は同じだけ減速する。0で無効")]
        [SerializeField, Range(0f, 60f)] private float diveAcceleration = 14f;

        [Header("高度")]
        [Tooltip("海面の高さ。シーン生成時に街の最下点から決めて入れる（M1SceneSetup）")]
        [SerializeField] private float seaLevel = 0f;

        [Tooltip("海面からこれ以上は下がれない。**海に沈まないための最後の受け皿**")]
        [SerializeField, Min(0f)] private float altitudeAboveSeaMin = 3f;

        [SerializeField] private float altitudeMax = 2000f;

        [Header("当たり判定")]
        [Tooltip("飛行中も建物にぶつかる。**負荷はほぼ無い**（コライダーは地域単位で9個、" +
                 "判定はカプセル1個ぶん）。問題は負荷ではなく、ぶつかった時の気持ちよさ")]
        [SerializeField] private bool collideWhileFlying = true;

        [Tooltip("接触した時に速度に掛ける係数。1で減速しない。**止めない**——" +
                 "急停止は墜落と同じ体験になる（CLAUDE.md 不変条件3）")]
        [SerializeField, Range(0.1f, 1f)] private float grazeSpeedFactor = 0.6f;

        [Header("歩行")]
        [Tooltip("着地できる高さの上限 (m)。真下にこれ以内で足場があれば降りられる")]
        [SerializeField, Min(1f)] private float landingRayLength = 400f;

        [SerializeField, Min(0.5f)] private float walkSpeed = 4.5f;

        [Tooltip("ダッシュ。屋上から屋上へ助走をつけて跳べる速さにしてある")]
        [SerializeField, Min(0.5f)] private float runSpeed = 11f;

        [SerializeField, Min(1f)] private float jumpSpeed = 7f;
        [SerializeField, Min(1f)] private float gravity = 22f;

        [Header("パルクール")]
        [Tooltip("空中でもう一度跳べる回数")]
        [SerializeField, Min(0)] private int airJumpsMax = 1;

        [SerializeField, Min(1f)] private float airJumpSpeed = 6f;

        [Tooltip("壁に触れながらジャンプを押し続けると駆け上がる速さ (m/s)")]
        [SerializeField, Min(0f)] private float wallRunSpeed = 7f;

        [Tooltip("駆け上がれる時間 (秒)。これを過ぎたら落ちる")]
        [SerializeField, Min(0f)] private float wallRunSeconds = 1.1f;

        [Tooltip("歩行時の視線の速さ (度/秒)")]
        [SerializeField, Min(1f)] private float lookRate = 130f;

        [Tooltip("飛び立つ時の初速 (m/s)")]
        [SerializeField, Min(1f)] private float launchSpeed = 16f;

        [Tooltip("飛び立つ時の機首角 (度)")]
        [SerializeField, Range(0f, 60f)] private float launchPitch = 25f;

        private float pitchDegrees;
        private float rollDegrees;
        private float yawDegrees;
        private float speed;
        private float verticalVelocity;
        private int airJumpsUsed;
        private float wallRunRemaining;
        private bool jumpHeldLastFrame;

        private Vector3 startPosition;
        private float startYaw;

        private CharacterController body;
        private bool viewToggleRequested;

        /// <summary>現在の速度 (m/s)。HUDとカメラが読む。</summary>
        public float Speed => speed;

        /// <summary>速度の範囲内での位置 (0〜1)。カメラの画角に使う。</summary>
        public float SpeedRatio => Mathf.InverseLerp(speedMin, speedMax, speed);

        public float PitchDegrees => pitchDegrees;
        public float RollDegrees => rollDegrees;
        public bool IsBoosting { get; private set; }
        public MotionMode Mode { get; private set; } = MotionMode.Flying;
        public bool IsWalking => Mode == MotionMode.Walking;
        public bool InvertPitch => invertPitch;

        /// <summary>着地に失敗した等、HUDに1行出したいときのメッセージ。</summary>
        public string Notice { get; private set; }
        public float NoticeTime { get; private set; }

        private void Awake()
        {
            if (input == null) input = GetComponent<FlightInput>();
            body = GetComponent<CharacterController>();
            if (body != null) body.enabled = collideWhileFlying;

            startPosition = transform.position;
            startYaw = transform.eulerAngles.y;
            ResetPose();
        }

        private void Update()
        {
            FlightInputState state = input != null ? input.Read() : default;
            float dt = Time.deltaTime;

            if (state.ToggleView) viewToggleRequested = true;
            if (state.ToggleInvert)
            {
                invertPitch = !invertPitch;
                ShowNotice(invertPitch ? "上下反転: あり" : "上下反転: なし");
            }

            if (state.ToggleCollision)
            {
                collideWhileFlying = !collideWhileFlying;
                ShowNotice(collideWhileFlying ? "飛行中の当たり判定: あり" : "飛行中の当たり判定: なし（すり抜け）");
            }

            if (state.Reset)
            {
                ResetPose();
                return;
            }

            if (state.ToggleMotion)
            {
                if (Mode == MotionMode.Flying) TryLand(); else Launch();
            }

            if (Mode == MotionMode.Flying)
            {
                UpdateAttitude(state, dt);
                UpdateSpeed(state, dt);
                MoveFlying(dt);
            }
            else
            {
                UpdateWalking(state, dt);
            }
        }

        // ------------------------------------------------------------------ 飛行

        private void UpdateAttitude(FlightInputState state, float dt)
        {
            // マウスとスティックだけ反転の対象。矢印キーは「↑で機首上げ」のまま動かさない
            float aim = (state.Aim.y + state.LeftStick.y) * (invertPitch ? 1f : -1f);
            float pitchInput = Mathf.Clamp(aim + state.Arrows.y, -1f, 1f);
            float rollInput = Mathf.Clamp(state.Aim.x + state.LeftStick.x + state.Keys.x, -1f, 1f);

            pitchDegrees += pitchInput * pitchRate * dt;
            rollDegrees += rollInput * rollRate * dt;

            if (state.LevelOrJump)
            {
                // 明示的な水平戻しは、自動より速くないと「効いた感じ」がしない
                pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, pitchRate * 2f * dt);
                rollDegrees = Mathf.MoveTowards(rollDegrees, 0f, rollRate * 2f * dt);
            }
            else if (autoLevelRate > 0f)
            {
                // 入力が無い軸だけ戻す。入力中に戻すと操作と喧嘩する
                if (Mathf.Approximately(rollInput, 0f))
                {
                    rollDegrees = Mathf.MoveTowards(rollDegrees, 0f, autoLevelRate * dt);
                }
                if (Mathf.Approximately(pitchInput, 0f))
                {
                    pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, autoLevelRate * 0.5f * dt);
                }
            }

            pitchDegrees = Mathf.Clamp(pitchDegrees, -pitchLimit, pitchLimit);
            rollDegrees = Mathf.Clamp(rollDegrees, -rollLimit, rollLimit);

            // バンク旋回。傾けた分だけ機首が回る
            yawDegrees += Mathf.Sin(rollDegrees * Mathf.Deg2Rad) * turnRate * dt;

            var target = Quaternion.Euler(-pitchDegrees, yawDegrees, -rollDegrees);
            transform.rotation = attitudeSmoothing > 0f
                ? Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-dt / attitudeSmoothing))
                : target;
        }

        private void UpdateSpeed(FlightInputState state, float dt)
        {
            IsBoosting = state.Boost;

            float throttleInput = Mathf.Clamp(state.Keys.y + state.Trigger, -1f, 1f);
            float throttle01 = Mathf.InverseLerp(-1f, 1f, throttleInput);
            float targetSpeed = Mathf.Lerp(speedMin, speedMax, throttle01);
            if (IsBoosting) targetSpeed = Mathf.Min(targetSpeed * boostMultiplier, speedMax * boostMultiplier);

            speed = Mathf.MoveTowards(speed, targetSpeed, throttleAcceleration * dt);

            // 降下で速度が乗り、上昇で削がれる。位置エネルギーの交換のつもりで、力学ではない
            speed += Mathf.Sin(-pitchDegrees * Mathf.Deg2Rad) * diveAcceleration * dt;
            speed = Mathf.Clamp(speed, speedMin * 0.5f, speedMax * boostMultiplier);
        }

        private void MoveFlying(float dt)
        {
            Vector3 delta = transform.forward * (speed * dt);

            if (collideWhileFlying && body != null)
            {
                if (!body.enabled) body.enabled = true;
                body.Move(delta);

                // 当たっても止めない。掠めて速度が落ちる程度にする
                if (body.collisionFlags != CollisionFlags.None)
                {
                    speed = Mathf.Max(speedMin, speed * grazeSpeedFactor);
                }
            }
            else
            {
                if (body != null && body.enabled) body.enabled = false;
                transform.position += delta;
            }

            ClampAltitude(dt);
        }

        /// <summary>海に沈まない・上がりすぎない。地面や建物は当たり判定に任せる。</summary>
        private void ClampAltitude(float dt)
        {
            Vector3 position = transform.position;
            float floor = seaLevel + altitudeAboveSeaMin;

            if (position.y < floor)
            {
                position.y = floor;
                if (pitchDegrees < 0f) pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, 90f * dt);
            }

            position.y = Mathf.Min(position.y, altitudeMax);
            transform.position = position;
        }

        // ------------------------------------------------------------------ 歩行

        /// <summary>
        /// 真下の足場を探して降りる。屋上でも地面でもよい。
        /// **飛行中は当たり判定が無いので、着地の瞬間にだけコライダーを起こす。**
        /// </summary>
        private void TryLand()
        {
            if (body == null)
            {
                ShowNotice("この機体は歩けません（CharacterControllerが無い）");
                return;
            }

            Vector3 origin = transform.position + Vector3.up * 2f;
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, landingRayLength))
            {
                ShowNotice("真下に足場がありません");
                return;
            }

            Mode = MotionMode.Walking;
            speed = 0f;
            verticalVelocity = 0f;
            rollDegrees = 0f;
            pitchDegrees = 0f;
            IsBoosting = false;

            transform.position = hit.point + Vector3.up * (body.height * 0.5f + body.skinWidth + 0.02f);
            transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);

            body.enabled = true;
            input?.ClearAim();
            ShowNotice($"着地（高度 {hit.point.y:F0} m）");
        }

        /// <summary>
        /// その場から飛び立つ。
        ///
        /// **視線を動かさない。** 落下中に飛行へ切り替えた時、機首を既定の角度に立て直すと
        /// 見ていた景色が飛んで「操作を取り上げられた」感覚になる。押した瞬間に見ている方向を
        /// そのまま進行方向にする。
        /// </summary>
        private void Launch()
        {
            Mode = MotionMode.Flying;
            if (body != null) body.enabled = collideWhileFlying;

            pitchDegrees = Mathf.Clamp(pitchDegrees, -pitchLimit, pitchLimit);
            rollDegrees = 0f;

            // 落下の勢いは活かす。止まってから飛び出すと重く感じる
            speed = Mathf.Max(launchSpeed, speed);
            verticalVelocity = 0f;

            input?.ClearAim();
            ShowNotice("飛び立った");
        }

        private void UpdateWalking(FlightInputState state, float dt)
        {
            // 視線。マウス（または右スティック）で回し、身体は水平のまま
            float lookX = state.Aim.x + state.RightStick.x;
            float lookY = (state.Aim.y + state.RightStick.y) * (invertPitch ? 1f : -1f) + state.Arrows.y;

            yawDegrees += lookX * lookRate * dt;
            pitchDegrees = Mathf.Clamp(pitchDegrees + lookY * lookRate * dt, -80f, 80f);
            transform.rotation = Quaternion.Euler(-pitchDegrees, yawDegrees, 0f);

            // 移動は水平面のみ。視線が上を向いていても足元は水平に進む
            Vector2 move = Vector2.ClampMagnitude(state.Keys + state.LeftStick, 1f);
            Quaternion heading = Quaternion.Euler(0f, yawDegrees, 0f);
            Vector3 horizontal = heading * new Vector3(move.x, 0f, move.y) *
                                 (state.Boost ? runSpeed : walkSpeed);

            bool jumpPressed = state.LevelOrJump && !jumpHeldLastFrame;
            jumpHeldLastFrame = state.LevelOrJump;

            if (body.isGrounded)
            {
                verticalVelocity = -2f; // 接地を維持する程度に押し付ける
                airJumpsUsed = 0;
                wallRunRemaining = wallRunSeconds;
                if (jumpPressed) verticalVelocity = jumpSpeed;
            }
            else if (CanWallRun(state, jumpHeldLastFrame))
            {
                // 壁走り。壁に触れながらジャンプを押し続けている間だけ上がる
                verticalVelocity = wallRunSpeed;
                wallRunRemaining -= dt;
            }
            else
            {
                verticalVelocity -= gravity * dt;

                // 二段ジャンプ。屋上から屋上へ届かなかった時の救済でもある
                if (jumpPressed && airJumpsUsed < airJumpsMax)
                {
                    verticalVelocity = airJumpSpeed;
                    airJumpsUsed++;
                }
            }

            body.Move((horizontal + Vector3.up * verticalVelocity) * dt);
            speed = new Vector2(horizontal.x, horizontal.z).magnitude;

            // 海へ落ちても死なせない。沈む前に飛行へ戻す（→ CLAUDE.md 不変条件3）
            if (transform.position.y < seaLevel + altitudeAboveSeaMin) Launch();
        }

        /// <summary>
        /// 壁を駆け上がれるか。**壁に押し当てながらジャンプを押し続ける**のが条件。
        /// 屋上から屋上へ移る時に、届かない縁を登れると移動が途切れない。
        /// </summary>
        private bool CanWallRun(FlightInputState state, bool jumpHeld)
        {
            if (!jumpHeld || wallRunSeconds <= 0f || wallRunRemaining <= 0f) return false;
            if ((body.collisionFlags & CollisionFlags.Sides) == 0) return false;

            // 壁へ向かって進もうとしている時だけ。触れただけで登り始めると事故になる
            return (state.Keys + state.LeftStick).sqrMagnitude > 0.01f;
        }

        // ------------------------------------------------------------------ 共通

        /// <summary>
        /// 視点切替が押されたか。**押されていたらtrueを返して同時に消費する。**
        /// 入力は<see cref="FlightController"/>が1フレームに1回だけ読む（マウスのデルタを
        /// 二重に積まないため）ので、カメラ側はここ経由で受け取る。
        /// </summary>
        public bool ConsumeViewToggle()
        {
            if (!viewToggleRequested) return false;
            viewToggleRequested = false;
            return true;
        }

        private void ShowNotice(string message)
        {
            Notice = message;
            NoticeTime = Time.time;
        }

        /// <summary>開始地点・水平姿勢・初期速度に戻す。</summary>
        public void ResetPose()
        {
            Mode = MotionMode.Flying;
            if (body != null) body.enabled = collideWhileFlying;

            pitchDegrees = 0f;
            rollDegrees = 0f;
            yawDegrees = startYaw;
            speed = speedStart;
            verticalVelocity = 0f;

            input?.ClearAim();
            transform.SetPositionAndRotation(startPosition, Quaternion.Euler(0f, startYaw, 0f));
        }
    }
}
